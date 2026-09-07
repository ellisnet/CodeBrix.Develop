using System;
using System.IO;
using CodeBrix.Develop.Core.Android;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class AndroidDebugBridgeTests
{
    [Fact]
    public void ParseDeviceSerial_reads_the_serial_the_android_targets_announce()
        // The real line, as the _Upload target writes it.
        => AndroidDebugBridge.ParseDeviceSerial("  Found device: RFCRB0MPHYH")
            .Should().Be("RFCRB0MPHYH");

    [Theory]
    [InlineData("Found device: RFCRB0MPHYH")]
    [InlineData("Found device:RFCRB0MPHYH")]
    [InlineData("      Found device:   RFCRB0MPHYH   ")]
    public void ParseDeviceSerial_tolerates_the_surrounding_whitespace(string line)
        => AndroidDebugBridge.ParseDeviceSerial(line).Should().Be("RFCRB0MPHYH");

    [Theory]
    [InlineData("  Using cached value from RegisterTaskObject")]
    [InlineData("Build succeeded.")]
    [InlineData("Found device:")]
    [InlineData("Found device:    ")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseDeviceSerial_is_null_when_the_line_names_no_device(string line)
        => AndroidDebugBridge.ParseDeviceSerial(line).Should().BeNull();

    [Fact]
    public void FindAdb_finds_adb_under_the_first_root_that_has_it()
    {
        using var roots = new TemporarySdkRoots("empty", "real");
        roots.CreateAdb("real");

        AndroidDebugBridge.FindAdb(new[] { roots.RootPath("empty"), roots.RootPath("real") })
            .Should().Be(Path.Combine(roots.RootPath("real"), "platform-tools", "adb"));
    }

    [Fact]
    public void FindAdb_prefers_the_earlier_root_when_several_have_adb()
    {
        using var roots = new TemporarySdkRoots("first", "second");
        roots.CreateAdb("first");
        roots.CreateAdb("second");

        AndroidDebugBridge.FindAdb(new[] { roots.RootPath("first"), roots.RootPath("second") })
            .Should().Be(Path.Combine(roots.RootPath("first"), "platform-tools", "adb"));
    }

    [Fact]
    public void FindAdb_is_null_when_no_root_has_adb()
    {
        using var roots = new TemporarySdkRoots("empty");
        AndroidDebugBridge.FindAdb(new[] { roots.RootPath("empty") }).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindAdb_skips_the_roots_that_are_not_set(string root)
        // The environment variables are read unconditionally, so most machines
        // hand this a couple of nulls before any real path.
        => AndroidDebugBridge.FindAdb(new[] { root }).Should().BeNull();

    [Fact]
    public void FindAdb_is_null_for_no_roots_at_all()
    {
        AndroidDebugBridge.FindAdb(Array.Empty<string>()).Should().BeNull();
        AndroidDebugBridge.FindAdb(null).Should().BeNull();
    }

    [Fact]
    public void DefaultSdkRoots_ends_at_the_studio_default_location()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AndroidDebugBridge.DefaultSdkRoots()
            .Should().Contain(Path.Combine(home, "Android", "Sdk"));
    }

    [Fact]
    public async System.Threading.Tasks.Task ForceStopAsync_declines_an_empty_application_id()
        // Nothing to stop, and adb would otherwise be handed a blank package.
        => (await AndroidDebugBridge.ForceStopAsync("", "RFCRB0MPHYH",
            TestContext.Current.CancellationToken)).Should().BeFalse();

    /// <summary>Throwaway folders standing in for Android SDK installations.</summary>
    sealed class TemporarySdkRoots : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(),
            "codebrix-adb-tests-" + Guid.NewGuid().ToString("N"));

        public TemporarySdkRoots(params string[] names)
        {
            foreach (var name in names)
                Directory.CreateDirectory(Path.Combine(root, name));
        }

        public string RootPath(string name) => Path.Combine(root, name);

        public void CreateAdb(string name)
        {
            var tools = Path.Combine(root, name, "platform-tools");
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "adb"), "");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test run over.
            }
        }
    }
}
