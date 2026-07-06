//
// OptionsStore.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Core.Properties/PropertyService, rebuilt on
//      SQLite storage for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeBrix.Sqlite;

namespace CodeBrix.Develop.Core.Options;

/// <summary>
/// The persistent key/value store behind all CodeBrix.Develop configuration:
/// a single portable SQLite database file ("options.sqlite") holding
/// everything configurable in the application. Handles the full startup
/// sequence — silent re-creation when the file is missing, quarantine and
/// backup-restore when it is corrupt, and the automatic timestamped backup
/// plus retention pruning on every start.
/// </summary>
public sealed class OptionsStore : IDisposable
{
    /// <summary>The name of the options database file.</summary>
    public const string OptionsFileName = "options.sqlite";

    /// <summary>The file-name prefix of automatic startup backups.</summary>
    public const string AutoBackupFilePrefix = "options_auto_backup_";

    /// <summary>The file-name prefix a corrupt options file is quarantined under.</summary>
    public const string CorruptFilePrefix = "options_corrupt_";

    /// <summary>
    /// The local-time timestamp format used in backup and quarantine file
    /// names; fixed-width so an alphabetical listing is chronological.
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>The option key holding the auto-backup retention count.</summary>
    public const string AutoBackupRetentionKey = "CodeBrix.Develop.Options.AutoBackupRetention";

    /// <summary>The default auto-backup retention count.</summary>
    public const int DefaultAutoBackupRetention = 5;

    /// <summary>The maximum selectable auto-backup retention count.</summary>
    public const int MaxAutoBackupRetention = 10;

    static readonly JsonSerializerOptions serializerOptions = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() },
    };

    readonly object gate = new object();
    readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
    readonly Dictionary<string, EventHandler<PropertyChangedEventArgs>> keyHandlers =
        new Dictionary<string, EventHandler<PropertyChangedEventArgs>>(StringComparer.Ordinal);
    readonly Func<DateTime> clock;
    SqliteDatabase database;

    /// <summary>The folder holding options.sqlite and its backup copies.</summary>
    public string DirectoryPath { get; }

    /// <summary>The full path of the options.sqlite file.</summary>
    public string DatabaseFilePath { get; }

    /// <summary>
    /// True when this run started without a usable existing options file and
    /// the store was created fresh with first-run settings.
    /// </summary>
    public bool WasCreatedFresh { get; private set; }

    /// <summary>
    /// True when the existing options file was corrupt and the store was
    /// restored from the most recent automatic backup.
    /// </summary>
    public bool WasRestoredFromBackup { get; private set; }

    /// <summary>Raised after any option value changes.</summary>
    public event EventHandler<PropertyChangedEventArgs> PropertyChanged;

    /// <summary>
    /// Opens (or silently creates) the options store in the given folder and
    /// runs the startup auto-backup and retention pruning.
    /// </summary>
    public OptionsStore(string directoryPath) : this(directoryPath, null)
    {
    }

    internal OptionsStore(string directoryPath, Func<DateTime> testClock)
    {
        if (string.IsNullOrEmpty(directoryPath))
            throw new ArgumentException("A directory path is required", nameof(directoryPath));

        clock = testClock ?? (() => DateTime.Now);
        DirectoryPath = Path.GetFullPath(directoryPath);
        DatabaseFilePath = Path.Combine(DirectoryPath, OptionsFileName);
        Directory.CreateDirectory(DirectoryPath);

        OpenWithRecovery();

        var retention = GetAutoBackupRetention();
        if (retention > 0)
        {
            try
            {
                CreateAutoBackup();
                PruneAutoBackups(retention);
            }
            catch (Exception ex)
            {
                // A failed backup must never prevent the application from starting.
                LoggingService.LogError("Options auto-backup failed", ex);
            }
        }
    }

    /// <summary>The retention count currently stored (clamped to the legal 0..10 range).</summary>
    public int GetAutoBackupRetention() =>
        Math.Clamp(Get(AutoBackupRetentionKey, DefaultAutoBackupRetention), 0, MaxAutoBackupRetention);

    /// <summary>Whether a value is stored for the given key.</summary>
    public bool HasValue(string key)
    {
        lock (gate)
            return values.ContainsKey(key);
    }

    /// <summary>Returns the stored value for the key, or the type's default when not set.</summary>
    public T Get<T>(string key) => Get(key, default(T));

    /// <summary>Returns the stored value for the key, or the given default when not set.</summary>
    public T Get<T>(string key, T defaultValue)
    {
        string json;
        lock (gate)
        {
            if (!values.TryGetValue(key, out json))
                return defaultValue;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, serializerOptions);
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Option '{key}' could not be read as {typeof(T).Name}: {ex.Message}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Stores a value for the key (writing through to options.sqlite
    /// immediately); a null value removes the key. Returns true when the
    /// stored value actually changed.
    /// </summary>
    public bool Set(string key, object value)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("An option key is required", nameof(key));

        object oldValue = null;
        lock (gate)
        {
            values.TryGetValue(key, out var oldJson);
            if (value == null)
            {
                if (oldJson == null)
                    return false;
                oldValue = oldJson;
                values.Remove(key);
                database.Connection.Execute("DELETE FROM Option WHERE Key = @key", new { key });
            }
            else
            {
                var newJson = JsonSerializer.Serialize(value, value.GetType(), serializerOptions);
                if (newJson == oldJson)
                    return false;
                oldValue = oldJson;
                values[key] = newJson;
                database.Connection.Execute(
                    "INSERT INTO Option (Key, Value) VALUES (@key, @newJson) " +
                    "ON CONFLICT (Key) DO UPDATE SET Value = @newJson",
                    new { key, newJson });
            }
        }

        var args = new PropertyChangedEventArgs(key, oldValue, value);
        PropertyChanged?.Invoke(this, args);
        EventHandler<PropertyChangedEventArgs> handler;
        lock (gate)
            keyHandlers.TryGetValue(key, out handler);
        handler?.Invoke(this, args);
        return true;
    }

    /// <summary>Registers a handler raised when the given key's value changes.</summary>
    public void AddPropertyHandler(string key, EventHandler<PropertyChangedEventArgs> handler)
    {
        lock (gate)
        {
            keyHandlers.TryGetValue(key, out var existing);
            keyHandlers[key] = (EventHandler<PropertyChangedEventArgs>) Delegate.Combine(existing, handler);
        }
    }

    /// <summary>Removes a handler previously added with <see cref="AddPropertyHandler"/>.</summary>
    public void RemovePropertyHandler(string key, EventHandler<PropertyChangedEventArgs> handler)
    {
        lock (gate)
        {
            if (!keyHandlers.TryGetValue(key, out var existing))
                return;
            var remaining = (EventHandler<PropertyChangedEventArgs>) Delegate.Remove(existing, handler);
            if (remaining == null)
                keyHandlers.Remove(key);
            else
                keyHandlers[key] = remaining;
        }
    }

    /// <summary>Closes the underlying database.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            database?.Dispose();
            database = null;
        }
    }

    void OpenWithRecovery()
    {
        WasCreatedFresh = !File.Exists(DatabaseFilePath);
        try
        {
            OpenAndLoad();
            return;
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"The options file '{DatabaseFilePath}' could not be opened; quarantining it", ex);
            QuarantineCorruptFile();
        }

        // The corrupt file has been renamed away; try the most recent
        // automatic backup, and fall back to a fresh first-run store.
        if (TryRestoreNewestAutoBackup())
        {
            try
            {
                OpenAndLoad();
                WasRestoredFromBackup = true;
                return;
            }
            catch (Exception ex)
            {
                LoggingService.LogError("The restored options backup could not be opened either; starting fresh", ex);
                QuarantineCorruptFile();
            }
        }

        WasCreatedFresh = true;
        OpenAndLoad();
    }

    void OpenAndLoad()
    {
        var db = new SqliteDatabase(DatabaseFilePath, null, new SqliteDatabaseOptions());
        try
        {
            db.SafeOpen();
            if (!string.Equals(db.ExecuteScalar("PRAGMA integrity_check") as string, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PRAGMA integrity_check did not report 'ok'");
            db.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS Option (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL)");

            values.Clear();
            foreach (var row in db.Connection.Query("SELECT Key, Value FROM Option"))
                values[(string) row.Key] = (string) row.Value;
        }
        catch
        {
            db.Dispose();
            throw;
        }
        database = db;
    }

    void QuarantineCorruptFile()
    {
        database?.Dispose();
        database = null;
        values.Clear();

        if (!File.Exists(DatabaseFilePath))
            return;
        var quarantinePath = Path.Combine(DirectoryPath,
            $"{CorruptFilePrefix}{clock().ToString(TimestampFormat, CultureInfo.InvariantCulture)}.sqlite");
        File.Move(DatabaseFilePath, quarantinePath, overwrite: true);
        // Keep SQLite's companion files with the database they belong to.
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = DatabaseFilePath + suffix;
            if (File.Exists(sidecar))
                File.Move(sidecar, quarantinePath + suffix, overwrite: true);
        }
    }

    bool TryRestoreNewestAutoBackup()
    {
        var newest = EnumerateAutoBackups().OrderByDescending(backup => backup.Timestamp).FirstOrDefault();
        if (newest.Path == null)
            return false;
        try
        {
            File.Copy(newest.Path, DatabaseFilePath, overwrite: true);
            LoggingService.LogInfo($"Options restored from backup {Path.GetFileName(newest.Path)}");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Could not restore options backup {newest.Path}", ex);
            return false;
        }
    }

    void CreateAutoBackup()
    {
        var backupPath = Path.Combine(DirectoryPath,
            $"{AutoBackupFilePrefix}{clock().ToString(TimestampFormat, CultureInfo.InvariantCulture)}.sqlite");
        // Orchestrated clean copy: quiesce, checkpoint the WAL, then run
        // SQLite's online backup — the single resulting file is the
        // complete database.
        database.BackupToFile(backupPath);
        LoggingService.LogInfo($"Options auto-backup created: {Path.GetFileName(backupPath)}");
    }

    void PruneAutoBackups(int retainCount)
    {
        // Recency comes from the timestamp encoded in the file name — never
        // from file-system created/modified metadata. Files that do not
        // match the auto-backup naming scheme exactly (including manual
        // copies a user made) are never deleted.
        var expired = EnumerateAutoBackups()
            .OrderByDescending(backup => backup.Timestamp)
            .Skip(retainCount);
        foreach (var backup in expired)
        {
            try
            {
                File.Delete(backup.Path);
                LoggingService.LogInfo($"Options auto-backup pruned: {Path.GetFileName(backup.Path)}");
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"Could not prune options auto-backup {backup.Path}: {ex.Message}");
            }
        }
    }

    IEnumerable<(string Path, DateTime Timestamp)> EnumerateAutoBackups()
    {
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, $"{AutoBackupFilePrefix}*.sqlite"))
        {
            var name = Path.GetFileName(path);
            var stampText = name.Substring(AutoBackupFilePrefix.Length, name.Length - AutoBackupFilePrefix.Length - ".sqlite".Length);
            if (DateTime.TryParseExact(stampText, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var stamp))
                yield return (path, stamp);
        }
    }
}
