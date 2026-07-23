using System;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Options;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class EnvironmentInfoTests : IDisposable
{
    readonly string root;
    readonly string directory;

    public EnvironmentInfoTests()
    {
        root = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests", Path.GetRandomFileName());
        directory = Path.Combine(root, "options");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(true, false, false, "linux")]
    [InlineData(false, true, false, "windows")]
    [InlineData(false, false, true, "macos")]
    public void Operating_system_is_named_for_the_known_platforms(bool linux, bool windows, bool macos, string expected)
    {
        //Act
        var result = EnvironmentInfo.NormalizeOperatingSystem(linux, windows, macos, "ignored");

        //Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Unrecognized_operating_system_uses_first_description_token_with_unsupported_suffix()
    {
        //Act
        var result = EnvironmentInfo.NormalizeOperatingSystem(false, false, false, "FreeBSD 13.2-RELEASE-p1");

        //Assert
        result.Should().Be("freebsd-unsupported");
    }

    [Fact]
    public void Unrecognized_operating_system_with_no_description_falls_back_to_unknown()
    {
        //Act
        var result = EnvironmentInfo.NormalizeOperatingSystem(false, false, false, "   ");

        //Assert
        result.Should().Be("unknown-unsupported");
    }

    [Fact]
    public void Desktop_session_type_is_empty_on_non_linux()
    {
        //Act
        var result = EnvironmentInfo.NormalizeDesktopSessionType(isLinux: false, "wayland");

        //Assert
        result.Should().Be(string.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Desktop_session_type_is_unknown_on_linux_when_unreadable(string value)
    {
        //Act
        var result = EnvironmentInfo.NormalizeDesktopSessionType(isLinux: true, value);

        //Assert
        result.Should().Be("unknown");
    }

    [Theory]
    [InlineData("x11", "x11")]
    [InlineData("wayland", "wayland")]
    [InlineData("  X11  ", "x11")]
    [InlineData("Wayland", "wayland")]
    public void Desktop_session_type_is_trimmed_and_lowercased_on_linux(string value, string expected)
    {
        //Act
        var result = EnvironmentInfo.NormalizeDesktopSessionType(isLinux: true, value);

        //Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Report_shows_a_single_architecture_line_when_os_and_process_match()
    {
        //Act
        var lines = EnvironmentInfo.BuildReportLines("linux", "x64", "x64", "x11");

        //Assert
        lines.Count(line => line.Contains("Architecture:")).Should().Be(1);
        lines.Any(line => line.Contains("Architecture:") && line.Contains("x64")).Should().BeTrue();
        lines.Any(line => line.Contains("OS architecture:")).Should().BeFalse();
        lines.Any(line => line.Contains("Process architecture:")).Should().BeFalse();
    }

    [Fact]
    public void Report_shows_separate_architecture_lines_when_os_and_process_differ()
    {
        //Act
        var lines = EnvironmentInfo.BuildReportLines("windows", "arm64", "x64", "");

        //Assert
        lines.Any(line => line.Contains("OS architecture:") && line.Contains("arm64")).Should().BeTrue();
        lines.Any(line => line.Contains("Process architecture:") && line.Contains("x64")).Should().BeTrue();
        lines.Any(line => line.Contains("Architecture:")).Should().BeFalse();
    }

    [Fact]
    public void Report_omits_the_desktop_session_line_when_blank()
    {
        //Act
        var lines = EnvironmentInfo.BuildReportLines("windows", "x64", "x64", "");

        //Assert
        lines.Any(line => line.Contains("Desktop session type:")).Should().BeFalse();
    }

    [Fact]
    public void Report_includes_the_desktop_session_line_when_present()
    {
        //Act
        var lines = EnvironmentInfo.BuildReportLines("linux", "x64", "x64", "unknown");

        //Assert
        lines.Any(line => line.Contains("Desktop session type:") && line.Contains("unknown")).Should().BeTrue();
    }

    [Fact]
    public void Detection_writes_all_four_values_into_the_store()
    {
        //Arrange
        using var store = new OptionsStore(directory);

        //Act
        EnvironmentInfo.DetectStoreAndReport(store);

        //Assert — the stored values match what detection reports on this host.
        store.Get(EnvironmentInfo.OperatingSystemKey, "").Should().Be(EnvironmentInfo.DetectOperatingSystem());
        store.Get(EnvironmentInfo.OSArchitectureKey, "").Should().Be(EnvironmentInfo.DetectOSArchitecture());
        store.Get(EnvironmentInfo.ProcessArchitectureKey, "").Should().Be(EnvironmentInfo.DetectProcessArchitecture());
        store.Get(EnvironmentInfo.DesktopSessionTypeKey, "missing").Should().Be(EnvironmentInfo.DetectDesktopSessionType());
    }

    [Fact]
    public void Detection_overwrites_previous_values()
    {
        //Arrange
        using var store = new OptionsStore(directory);
        store.Set(EnvironmentInfo.OperatingSystemKey, "stale-value");

        //Act
        EnvironmentInfo.DetectStoreAndReport(store);

        //Assert
        store.Get(EnvironmentInfo.OperatingSystemKey, "").Should().NotBe("stale-value");
    }
}
