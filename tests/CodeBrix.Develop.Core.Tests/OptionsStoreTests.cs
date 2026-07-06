using System;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core.Options;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class OptionsStoreTests : IDisposable
{
    readonly string directory;

    public OptionsStoreTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests", Path.GetRandomFileName());
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }

    OptionsStore CreateStore(DateTime? now = null) =>
        new OptionsStore(directory, now == null ? null : () => now.Value);

    static string BackupName(string timestamp) => $"options_auto_backup_{timestamp}.sqlite";

    string[] AutoBackupFiles() =>
        Directory.EnumerateFiles(directory, "options_auto_backup_*.sqlite")
            .Select(Path.GetFileName).OrderBy(name => name).ToArray();

    [Fact]
    public void Missing_file_is_silently_created_fresh()
    {
        //Act
        using var store = CreateStore();

        //Assert
        store.WasCreatedFresh.Should().BeTrue();
        File.Exists(Path.Combine(directory, "options.sqlite")).Should().BeTrue();
    }

    [Fact]
    public void Get_returns_default_when_not_set()
    {
        //Arrange
        using var store = CreateStore();

        //Assert
        store.Get("CodeBrix.Test.Missing", 42).Should().Be(42);
        store.Get<string>("CodeBrix.Test.Missing").Should().BeNull();
        store.HasValue("CodeBrix.Test.Missing").Should().BeFalse();
    }

    [Fact]
    public void Set_and_Get_round_trip_common_types()
    {
        //Arrange
        using var store = CreateStore();

        //Act
        store.Set("CodeBrix.Test.String", "hello");
        store.Set("CodeBrix.Test.Int", 7);
        store.Set("CodeBrix.Test.Bool", true);
        store.Set("CodeBrix.Test.Enum", DayOfWeek.Friday);

        //Assert
        store.Get<string>("CodeBrix.Test.String").Should().Be("hello");
        store.Get<int>("CodeBrix.Test.Int").Should().Be(7);
        store.Get<bool>("CodeBrix.Test.Bool").Should().BeTrue();
        store.Get<DayOfWeek>("CodeBrix.Test.Enum").Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public void Values_persist_across_reopen()
    {
        //Arrange
        using (var store = CreateStore())
            store.Set("CodeBrix.Test.Persisted", "survives");

        //Act
        using var reopened = CreateStore();

        //Assert
        reopened.WasCreatedFresh.Should().BeFalse();
        reopened.Get<string>("CodeBrix.Test.Persisted").Should().Be("survives");
    }

    [Fact]
    public void Set_null_removes_the_key()
    {
        //Arrange
        using var store = CreateStore();
        store.Set("CodeBrix.Test.Removed", "value");

        //Act
        store.Set("CodeBrix.Test.Removed", null);

        //Assert
        store.HasValue("CodeBrix.Test.Removed").Should().BeFalse();
    }

    [Fact]
    public void Set_returns_true_only_when_the_value_changes()
    {
        //Arrange
        using var store = CreateStore();

        //Assert
        store.Set("CodeBrix.Test.Changed", "a").Should().BeTrue();
        store.Set("CodeBrix.Test.Changed", "a").Should().BeFalse();
        store.Set("CodeBrix.Test.Changed", "b").Should().BeTrue();
    }

    [Fact]
    public void Set_raises_change_events()
    {
        //Arrange
        using var store = CreateStore();
        PropertyChangedEventArgs broadcast = null;
        PropertyChangedEventArgs keyed = null;
        store.PropertyChanged += (_, args) => broadcast = args;
        store.AddPropertyHandler("CodeBrix.Test.Watched", (_, args) => keyed = args);

        //Act
        store.Set("CodeBrix.Test.Watched", "new");

        //Assert
        broadcast.Should().NotBeNull();
        broadcast.Key.Should().Be("CodeBrix.Test.Watched");
        keyed.Should().NotBeNull();
        keyed.NewValue.Should().Be("new");
    }

    [Fact]
    public void Startup_creates_an_autobackup_with_the_timestamp_naming_scheme()
    {
        //Act
        using var store = CreateStore(new DateTime(2026, 7, 6, 14, 32, 5));

        //Assert
        AutoBackupFiles().Should().Equal(new[] { BackupName("2026-07-06_14-32-05") });
    }

    [Fact]
    public void Autobackup_is_a_complete_usable_database()
    {
        //Arrange
        using (var store = CreateStore(new DateTime(2026, 7, 6, 8, 0, 0)))
            store.Set("CodeBrix.Test.InBackup", "captured");

        // Second start backs up the file that now contains the value.
        using (CreateStore(new DateTime(2026, 7, 6, 9, 0, 0))) { }

        //Act — pretend the main file was lost and only the newest backup remains.
        File.Delete(Path.Combine(directory, "options.sqlite"));
        File.Copy(Path.Combine(directory, BackupName("2026-07-06_09-00-00")),
            Path.Combine(directory, "options.sqlite"));
        using var restored = CreateStore(new DateTime(2026, 7, 6, 10, 0, 0));

        //Assert
        restored.Get<string>("CodeBrix.Test.InBackup").Should().Be("captured");
    }

    [Fact]
    public void Prune_keeps_only_the_newest_n_by_filename_timestamp()
    {
        //Arrange — retention 3, with five stale backups whose file times are
        // deliberately misleading (all identical), so only the name matters.
        using (var store = CreateStore(new DateTime(2026, 7, 1, 0, 0, 0)))
            store.Set(OptionsStore.AutoBackupRetentionKey, 3);
        foreach (var stamp in new[] { "2026-07-02_00-00-00", "2026-07-03_00-00-00", "2026-07-04_00-00-00" })
            File.Copy(Path.Combine(directory, BackupName("2026-07-01_00-00-00")),
                Path.Combine(directory, BackupName(stamp)));

        //Act
        using (CreateStore(new DateTime(2026, 7, 5, 0, 0, 0))) { }

        //Assert — newest three remain: the fresh backup counts toward n.
        AutoBackupFiles().Should().Equal(new[]
        {
            BackupName("2026-07-03_00-00-00"),
            BackupName("2026-07-04_00-00-00"),
            BackupName("2026-07-05_00-00-00"),
        });
    }

    [Fact]
    public void Retention_zero_creates_no_backup_and_prunes_nothing()
    {
        //Arrange
        using (var store = CreateStore(new DateTime(2026, 7, 1, 0, 0, 0)))
            store.Set(OptionsStore.AutoBackupRetentionKey, 0);
        var before = AutoBackupFiles();

        //Act
        using (CreateStore(new DateTime(2026, 7, 2, 0, 0, 0))) { }

        //Assert
        AutoBackupFiles().Should().Equal(before);
    }

    [Fact]
    public void Files_not_matching_the_autobackup_scheme_are_never_deleted()
    {
        //Arrange
        using (var store = CreateStore(new DateTime(2026, 7, 1, 0, 0, 0)))
            store.Set(OptionsStore.AutoBackupRetentionKey, 1);
        var manualCopy = Path.Combine(directory, "options_bak_bob_before_changes.sqlite");
        File.Copy(Path.Combine(directory, "options.sqlite"), manualCopy);
        // Matches the prefix and extension but has no parseable timestamp.
        var oddName = Path.Combine(directory, "options_auto_backup_not-a-timestamp.sqlite");
        File.WriteAllText(oddName, "not a database");

        //Act
        using (CreateStore(new DateTime(2026, 7, 2, 0, 0, 0))) { }

        //Assert — the manual copy and the unparseable name survive; the real
        // 2026-07-01 backup was pruned by retention 1.
        File.Exists(manualCopy).Should().BeTrue();
        File.Exists(oddName).Should().BeTrue();
        AutoBackupFiles().Should().Equal(new[]
        {
            BackupName("2026-07-02_00-00-00"),
            "options_auto_backup_not-a-timestamp.sqlite",
        });
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_restored_from_newest_backup()
    {
        //Arrange — a healthy run that leaves one backup containing the value.
        using (var store = CreateStore(new DateTime(2026, 7, 1, 0, 0, 0)))
            store.Set("CodeBrix.Test.Value", "from-backup");
        using (CreateStore(new DateTime(2026, 7, 2, 0, 0, 0))) { }
        File.WriteAllText(Path.Combine(directory, "options.sqlite"), "this is not a sqlite database");

        //Act
        using var store2 = CreateStore(new DateTime(2026, 7, 3, 0, 0, 0));

        //Assert
        store2.WasRestoredFromBackup.Should().BeTrue();
        store2.WasCreatedFresh.Should().BeFalse();
        store2.Get<string>("CodeBrix.Test.Value").Should().Be("from-backup");
        File.Exists(Path.Combine(directory, "options_corrupt_2026-07-03_00-00-00.sqlite")).Should().BeTrue();
    }

    [Fact]
    public void Corrupt_file_without_backups_starts_fresh()
    {
        //Arrange
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "options.sqlite"), "garbage");

        //Act
        using var store = CreateStore(new DateTime(2026, 7, 3, 0, 0, 0));

        //Assert
        store.WasCreatedFresh.Should().BeTrue();
        store.WasRestoredFromBackup.Should().BeFalse();
        File.Exists(Path.Combine(directory, "options_corrupt_2026-07-03_00-00-00.sqlite")).Should().BeTrue();
    }

    [Fact]
    public void Retention_read_is_clamped_to_the_legal_range()
    {
        //Arrange
        using var store = CreateStore();
        store.Set(OptionsStore.AutoBackupRetentionKey, 99);

        //Assert
        store.GetAutoBackupRetention().Should().Be(OptionsStore.MaxAutoBackupRetention);
    }

    [Fact]
    public void Mismatched_type_read_returns_the_default()
    {
        //Arrange
        using var store = CreateStore();
        store.Set("CodeBrix.Test.Typed", "not a number");

        //Assert
        store.Get("CodeBrix.Test.Typed", 5).Should().Be(5);
    }
}
