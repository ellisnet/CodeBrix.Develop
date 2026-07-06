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
/// sequence — adoption of a staged options_incoming.sqlite import, silent
/// re-creation when the file is missing, quarantine and backup-restore when
/// it is corrupt, and the automatic timestamped backup plus retention
/// pruning on every start.
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
    /// The name an imported options file is staged under; when present at
    /// startup it replaces options.sqlite before the store opens.
    /// </summary>
    public const string IncomingFileName = "options_incoming.sqlite";

    /// <summary>
    /// The file-name prefix the previous options.sqlite is renamed to when an
    /// imported file is adopted at startup. These copies are never pruned.
    /// </summary>
    public const string OldFilePrefix = "options_old_";

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

    // SQLite's companion files, kept (or moved) with the database they belong to.
    static readonly string[] sidecarSuffixes = { "-wal", "-shm", "-journal" };

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

    /// <summary>
    /// True when a staged options_incoming.sqlite file was adopted at startup,
    /// replacing the previous options.sqlite (kept as an options_old_ copy).
    /// </summary>
    public bool WasReplacedByImport { get; private set; }

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

        AdoptIncomingFile();
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

    /// <summary>
    /// Exports the live options database to the given file as a safe,
    /// complete, self-contained copy (quiesce, WAL checkpoint, then SQLite
    /// online backup — no companion files needed). The destination must lie
    /// outside the options folder, which holds nothing but the live store
    /// and its own backup copies.
    /// </summary>
    public void ExportToFile(string destinationFilePath)
    {
        if (string.IsNullOrEmpty(destinationFilePath))
            throw new ArgumentException("A destination file path is required", nameof(destinationFilePath));

        var fullPath = Path.GetFullPath(destinationFilePath);
        var insideOptionsFolder = string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.Ordinal)
            || fullPath.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (insideOptionsFolder)
            throw new InvalidOperationException(
                "Options cannot be exported into the options folder itself; please choose another location.");

        lock (gate)
            database.BackupToFile(fullPath);
        LoggingService.LogInfo($"Options exported to {fullPath}");
    }

    /// <summary>
    /// Validates that the given file looks like a real options database
    /// (a SQLite database that passes an integrity check and contains the
    /// Option table) and stages it as options_incoming.sqlite, to be adopted
    /// in place of options.sqlite on the next start. Throws
    /// <see cref="InvalidDataException"/> when the file appears to have
    /// problems; the validation never opens the user's file in place.
    /// </summary>
    public void StageIncomingFile(string sourceFilePath)
    {
        if (string.IsNullOrEmpty(sourceFilePath))
            throw new ArgumentException("A source file path is required", nameof(sourceFilePath));
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("The selected file does not exist.", sourceFilePath);

        // Work on a private copy so the selected file is never opened (or
        // given WAL companion files) where the user keeps it.
        var tempDirectory = Path.Combine(Path.GetTempPath(), "CodeBrix.Develop", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var tempPath = Path.Combine(tempDirectory, OptionsFileName);
            File.Copy(sourceFilePath, tempPath);
            foreach (var suffix in sidecarSuffixes)
            {
                if (File.Exists(sourceFilePath + suffix))
                    File.Copy(sourceFilePath + suffix, tempPath + suffix);
            }

            using var candidate = new SqliteDatabase(tempPath, null, new SqliteDatabaseOptions());
            try
            {
                candidate.SafeOpen();
                if (!string.Equals(candidate.ExecuteScalar("PRAGMA integrity_check") as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The file failed the SQLite integrity check.");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"The file could not be opened as a SQLite database: {ex.Message}", ex);
            }

            if (candidate.ExecuteScalar("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Option'") == null)
                throw new InvalidDataException("The file is a SQLite database, but does not contain the Option table an options file holds.");
            try
            {
                candidate.Connection.Query("SELECT Key, Value FROM Option").ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"The file's Option table could not be read: {ex.Message}", ex);
            }

            // Stage a clean, checkpointed, self-contained copy — never the raw
            // source bytes, which may depend on companion files.
            candidate.BackupToFile(Path.Combine(DirectoryPath, IncomingFileName));
            LoggingService.LogInfo($"Options file {sourceFilePath} staged as {IncomingFileName}");
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

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

    void AdoptIncomingFile()
    {
        var incomingPath = Path.Combine(DirectoryPath, IncomingFileName);
        if (!File.Exists(incomingPath))
            return;
        try
        {
            if (File.Exists(DatabaseFilePath))
            {
                var oldPath = Path.Combine(DirectoryPath,
                    $"{OldFilePrefix}{clock().ToString(TimestampFormat, CultureInfo.InvariantCulture)}.sqlite");
                File.Move(DatabaseFilePath, oldPath, overwrite: true);
                foreach (var suffix in sidecarSuffixes)
                {
                    var sidecar = DatabaseFilePath + suffix;
                    if (File.Exists(sidecar))
                        File.Move(sidecar, oldPath + suffix, overwrite: true);
                }
                LoggingService.LogInfo($"Previous options kept as {Path.GetFileName(oldPath)}");
            }
            else
            {
                // Orphaned companion files must not pair up with the adopted file.
                foreach (var suffix in sidecarSuffixes)
                {
                    var sidecar = DatabaseFilePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }

            File.Move(incomingPath, DatabaseFilePath);
            WasReplacedByImport = true;
            LoggingService.LogInfo($"Imported options file {IncomingFileName} adopted as {OptionsFileName}");
        }
        catch (Exception ex)
        {
            // A failed adoption must never prevent the application from
            // starting; continue with whatever options file is in place.
            LoggingService.LogError("The imported options file could not be adopted", ex);
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
        foreach (var suffix in sidecarSuffixes)
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
