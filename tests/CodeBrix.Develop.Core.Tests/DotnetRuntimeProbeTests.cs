using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class DotnetRuntimeProbeTests
{
    [Fact]
    public void A_listed_netcore_runtime_indicates_installed()
    {
        //Arrange
        var output = "Microsoft.AspNetCore.App 10.0.0 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n"
            + "Microsoft.NETCore.App 10.0.0 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n";

        //Act / Assert
        DotnetRuntimeProbe.IndicatesInstalled(0, output).Should().BeTrue();
    }

    [Fact]
    public void A_nonzero_exit_indicates_not_installed()
        => DotnetRuntimeProbe.IndicatesInstalled(127, "").Should().BeFalse();

    [Fact]
    public void A_missing_exit_status_indicates_not_installed()
        => DotnetRuntimeProbe.IndicatesInstalled(null, "Microsoft.NETCore.App 10.0.0 [x]").Should().BeFalse();

    [Fact]
    public void A_dotnet_host_with_no_runtimes_indicates_not_installed()
        => DotnetRuntimeProbe.IndicatesInstalled(0, "\n").Should().BeFalse();

    [Fact]
    public void An_aspnet_only_listing_is_not_a_usable_runtime()
        => DotnetRuntimeProbe.IndicatesInstalled(0,
            "Microsoft.AspNetCore.App 10.0.0 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n")
            .Should().BeFalse();

    [Fact]
    public void GetRuntimeVersions_extracts_the_netcore_app_versions()
    {
        //Arrange
        var output = "Microsoft.AspNetCore.App 10.0.10 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n"
            + "Microsoft.NETCore.App 10.0.10 [/home/debian/.dotnet/shared/Microsoft.NETCore.App]\n";

        //Act
        var versions = DotnetRuntimeProbe.GetRuntimeVersions(output);

        //Assert — only the shared runtime, not ASP.NET
        versions.Should().ContainSingle().Which.Should().Be("10.0.10");
    }

    [Fact]
    public void GetRuntimeVersions_returns_every_listed_runtime_in_order()
    {
        //Arrange
        var output = "Microsoft.NETCore.App 9.0.8 [/x]\nMicrosoft.NETCore.App 10.0.10 [/y]\n";

        //Act
        var versions = DotnetRuntimeProbe.GetRuntimeVersions(output);

        //Assert
        versions.Should().Equal("9.0.8", "10.0.10");
    }

    [Fact]
    public void GetRuntimeVersions_is_empty_for_no_runtimes()
        => DotnetRuntimeProbe.GetRuntimeVersions("").Should().BeEmpty();

    [Fact]
    public void The_probe_command_tries_the_path_then_the_known_install_locations()
    {
        //Assert — the exec channel's non-login shell cannot see a
        //dotnet-install.sh install's PATH export, so the command must.
        DotnetRuntimeProbe.Command.Should().Contain("dotnet --list-runtimes");
        DotnetRuntimeProbe.Command.Should().Contain("/usr/bin/dotnet");
        DotnetRuntimeProbe.Command.Should().Contain("$HOME/.dotnet/dotnet");
    }
}
