using System.Linq;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class StartupHeadPolicyTests
{
    [Theory]
    [InlineData("MyApp.MacOS", "MacOS")]
    [InlineData("MyApp.LinuxX11", "LinuxX11")]
    [InlineData("MyApp.LinuxWayland", "LinuxWayland")]
    [InlineData("MyApp.LinuxFrameBuffer", "LinuxFrameBuffer")]
    [InlineData("MyApp.Win32Skia", "Win32Skia")]
    [InlineData("MyApp.WinWpfSkia", "WinWpfSkia")]
    [InlineData("LinuxX11", "LinuxX11")]
    public void Head_kind_is_recognized_by_name_suffix(string name, string expected)
    {
        StartupHeadPolicy.GetHeadKind(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("MyApp.Core")]
    [InlineData("MyApp")]
    [InlineData("SomeLinuxX11Thing")] // the suffix must follow a dot, or be the whole name
    [InlineData("")]
    [InlineData(null)]
    public void Non_head_names_have_no_head_kind(string name)
    {
        StartupHeadPolicy.GetHeadKind(name).Should().BeNull();
        StartupHeadPolicy.IsHead(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("macos", "", "MacOS")]
    [InlineData("windows", "", "Win32Skia,WinWpfSkia")]
    [InlineData("linux", "x11", "LinuxX11,LinuxFrameBuffer")]
    [InlineData("linux", "wayland", "LinuxWayland,LinuxX11,LinuxFrameBuffer")]
    [InlineData("linux", "unknown", "LinuxX11,LinuxFrameBuffer")]
    [InlineData("linux", "", "LinuxX11,LinuxFrameBuffer")]
    [InlineData("freebsd-unsupported", "", "")]
    public void Runnable_head_kinds_match_the_os_session_table(string os, string session, string expectedCsv)
    {
        //Act
        var result = string.Join(",", StartupHeadPolicy.RunnableHeadKinds(os, session));

        //Assert
        result.Should().Be(expectedCsv);
    }

    [Theory]
    [InlineData("macos", "", "MacOS")]
    [InlineData("windows", "", "Win32Skia,WinWpfSkia")]
    [InlineData("linux", "x11", "LinuxX11,LinuxFrameBuffer")]
    [InlineData("linux", "wayland", "LinuxWayland,LinuxX11,LinuxFrameBuffer")]
    [InlineData("linux", "unknown", "")] // an undetected session auto-selects nothing
    [InlineData("linux", "", "")]
    [InlineData("freebsd-unsupported", "", "")]
    public void Auto_startup_preference_drops_only_the_undetected_linux_session(string os, string session, string expectedCsv)
    {
        //Act
        var result = string.Join(",", StartupHeadPolicy.AutoStartupPreference(os, session));

        //Assert
        result.Should().Be(expectedCsv);
    }

    [Theory]
    // A non-head project always runs.
    [InlineData("MyApp.Core", "linux", "x11", true)]
    // A Wayland head can't run under a pure X11 session; X11/frame-buffer can.
    [InlineData("MyApp.LinuxWayland", "linux", "x11", false)]
    [InlineData("MyApp.LinuxX11", "linux", "x11", true)]
    [InlineData("MyApp.LinuxFrameBuffer", "linux", "x11", true)]
    // Under Wayland all three Linux heads run.
    [InlineData("MyApp.LinuxWayland", "linux", "wayland", true)]
    [InlineData("MyApp.LinuxX11", "linux", "wayland", true)]
    // An undetected Linux session still allows the compositor-agnostic heads.
    [InlineData("MyApp.LinuxX11", "linux", "unknown", true)]
    [InlineData("MyApp.LinuxWayland", "linux", "unknown", false)]
    // Cross-OS heads can't run.
    [InlineData("MyApp.Win32Skia", "linux", "x11", false)]
    [InlineData("MyApp.MacOS", "linux", "x11", false)]
    [InlineData("MyApp.LinuxX11", "windows", "", false)]
    // Each platform's own heads run there.
    [InlineData("MyApp.Win32Skia", "windows", "", true)]
    [InlineData("MyApp.WinWpfSkia", "windows", "", true)]
    [InlineData("MyApp.MacOS", "macos", "", true)]
    public void Can_run_reflects_os_session_runnability(string name, string os, string session, bool expected)
    {
        StartupHeadPolicy.CanRun(name, os, session).Should().Be(expected);
    }
}
