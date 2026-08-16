using System.Linq;
using System.Runtime.InteropServices;
using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class FrameBufferLaunchEnvironmentTests
{
    [Fact]
    public void The_launch_environment_names_the_runtime_and_forces_the_software_renderer()
    {
        //Act
        var environment = FrameBufferLaunchEnvironment.Create("/home/debian", touchRotationDegrees: 0, orientationEnabled: false);

        //Assert
        environment.Select(variable => variable.Key).Should().Equal(new[]
        {
            "DOTNET_ROOT", "CODEBRIX_FRAMEBUFFER_USE_DRM", "CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE",
        });
        environment[0].Value.Should().Be("/home/debian/.dotnet");
        environment[1].Value.Should().Be("0");
        environment[2].Value.Should().Be("none");
    }

    [Fact]
    public void Enabling_orientation_points_the_head_at_the_ide()
        => FrameBufferLaunchEnvironment.Create("/home/debian", 0, orientationEnabled: true)
            .Single(variable => variable.Key == "CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE")
            .Value.Should().Be("develop");

    [Fact]
    public void A_touch_rotation_is_only_passed_when_the_device_needs_one()
    {
        //Assert — an upright digitizer gets no variable at all
        FrameBufferLaunchEnvironment.Create("/home/debian", 0, false)
            .Any(variable => variable.Key == "CODEBRIX_FRAMEBUFFER_TOUCH_ROTATION").Should().BeFalse();
        FrameBufferLaunchEnvironment.Create("/home/debian", 180, false)
            .Single(variable => variable.Key == "CODEBRIX_FRAMEBUFFER_TOUCH_ROTATION").Value.Should().Be("180");
    }

    [Fact]
    public void The_run_command_keeps_expanding_home_on_the_device()
    {
        //Act — a plain run passes $HOME through to the device's own shell
        var assignments = FrameBufferLaunchEnvironment.ToShellAssignments(
            FrameBufferLaunchEnvironment.Create("$HOME", 180, orientationEnabled: true));

        //Assert
        assignments.Should().Be(
            "DOTNET_ROOT=\"$HOME/.dotnet\" CODEBRIX_FRAMEBUFFER_USE_DRM=\"0\" " +
            "CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE=\"develop\" CODEBRIX_FRAMEBUFFER_TOUCH_ROTATION=\"180\"");
    }

    [Fact]
    public void The_map_carries_the_same_values_for_the_debug_adapter()
    {
        //Act
        var map = FrameBufferLaunchEnvironment.CreateMap("/home/debian", 180, orientationEnabled: false);

        //Assert
        map.Count.Should().Be(4);
        map["DOTNET_ROOT"].Should().Be("/home/debian/.dotnet");
        map["CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE"].Should().Be("none");
        map["CODEBRIX_FRAMEBUFFER_TOUCH_ROTATION"].Should().Be("180");
    }
}

public class RemoteDebuggerTests
{
    [Fact]
    public void The_adapter_lives_in_a_cache_folder_under_the_device_users_home()
        => RemoteDebugger.RemotePath("/home/debian")
            .Should().Be("/home/debian/.cache/codebrix-develop/netcoredbg/netcoredbg");

    [Fact]
    public void The_home_directory_command_adds_no_newline_of_its_own()
        => RemoteDebugger.HomeDirectoryCommand.Should().Be("printf %s \"$HOME\"");

    [Fact]
    public void Sftp_copies_contents_not_permissions_so_both_binaries_are_made_executable()
        => RemoteDebugger.MakeExecutableCommand(new[]
        {
            "/home/debian/.cache/codebrix-develop/netcoredbg/netcoredbg",
            "/home/debian/apps/JustBetweenUs.LinuxFrameBuffer/JustBetweenUs.LinuxFrameBuffer",
        }).Should().Be(
            "chmod +x \"/home/debian/.cache/codebrix-develop/netcoredbg/netcoredbg\" " +
            "\"/home/debian/apps/JustBetweenUs.LinuxFrameBuffer/JustBetweenUs.LinuxFrameBuffer\"");

    [Fact]
    public void The_adapter_runs_in_the_apps_folder_with_the_apps_environment()
    {
        //Arrange
        var environment = FrameBufferLaunchEnvironment.Create("/home/debian", 0, orientationEnabled: false);

        //Act — exec replaces the shell, so the channel talks straight to the adapter
        var command = RemoteDebugger.StartCommand(
            RemoteDebugger.RemotePath("/home/debian"), "/home/debian/apps/JustBetweenUs.LinuxFrameBuffer", environment);

        //Assert — the device's .NET is added to PATH by the shell (an exec
        // channel is not a login shell), with $PATH left for it to expand
        command.Should().Be(
            "cd \"/home/debian/apps/JustBetweenUs.LinuxFrameBuffer\" && exec env " +
            "PATH=\"/home/debian/.dotnet:$PATH\" " +
            "DOTNET_ROOT=\"/home/debian/.dotnet\" CODEBRIX_FRAMEBUFFER_USE_DRM=\"0\" " +
            "CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE=\"none\" " +
            "\"/home/debian/.cache/codebrix-develop/netcoredbg/netcoredbg\" --interpreter=vscode");
    }

    [Fact]
    public void A_device_of_the_hosts_own_architecture_can_be_debugged()
        => RemoteDebugger.CanDebugDevice(RemoteDebugger.HostRuntimeIdentifier).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("linux-mips")]
    public void An_unknown_device_architecture_cannot_be_debugged(string deviceRuntimeIdentifier)
        => RemoteDebugger.CanDebugDevice(deviceRuntimeIdentifier).Should().BeFalse();

    [Fact]
    public void A_cross_architecture_device_cannot_be_debugged_by_the_bundled_adapter()
    {
        //Arrange — whichever architecture this test host is NOT
        var otherArchitecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-x64"
            : "linux-arm64";

        //Assert — and the explanation names both sides
        RemoteDebugger.CanDebugDevice(otherArchitecture).Should().BeFalse();
        RemoteDebugger.IncompatibleDeviceMessage(otherArchitecture)
            .Should().Contain(otherArchitecture).And.Contain(RemoteDebugger.HostRuntimeIdentifier);
    }
}
