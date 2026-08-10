using System.Linq;
using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class DeviceProvisioningTests
{
    [Fact]
    public void HasVideoAndInputGroups_is_true_when_both_are_present()
        => DeviceProvisioning.HasVideoAndInputGroups("debian adm cdrom sudo video input netdev\n")
            .Should().BeTrue();

    [Theory]
    [InlineData("debian sudo video netdev")]   // input missing
    [InlineData("debian sudo input netdev")]   // video missing
    [InlineData("debian sudo netdev")]          // both missing
    [InlineData("")]
    [InlineData(null)]
    public void HasVideoAndInputGroups_is_false_when_either_is_missing(string listing)
        => DeviceProvisioning.HasVideoAndInputGroups(listing).Should().BeFalse();

    [Fact]
    public void HasVideoAndInputGroups_does_not_match_substrings()
        // "videofoo" / "inputbar" are not the video / input groups
        => DeviceProvisioning.HasVideoAndInputGroups("debian videofoo inputbar").Should().BeFalse();

    [Fact]
    public void IsServiceMasked_is_true_for_masked()
        => DeviceProvisioning.IsServiceMasked("masked\n").Should().BeTrue();

    [Theory]
    [InlineData("enabled")]
    [InlineData("disabled")]
    [InlineData("static")]
    [InlineData("generated")]
    [InlineData("")]
    [InlineData(null)]
    public void IsServiceMasked_is_false_for_anything_else(string state)
        => DeviceProvisioning.IsServiceMasked(state).Should().BeFalse();

    [Fact]
    public void MissingNativeDependencies_finds_the_ones_absent_from_the_loader_cache()
    {
        //Arrange — a cache with fontconfig + icu present, libinput + xkbcommon absent
        var ldconfig =
            "\tlibfontconfig.so.1 (libc6,x86-64) => /lib/x86_64-linux-gnu/libfontconfig.so.1\n" +
            "\tlibicuuc.so.76 (libc6,x86-64) => /lib/x86_64-linux-gnu/libicuuc.so.76\n";

        //Act
        var missing = DeviceProvisioning.MissingNativeDependencies(ldconfig);

        //Assert
        missing.Select(dependency => dependency.AptPackage)
            .Should().BeEquivalentTo(new[] { "libinput10", "libxkbcommon-dev" });
    }

    [Fact]
    public void MissingNativeDependencies_is_empty_when_all_are_present()
    {
        //Arrange
        var ldconfig =
            "libfontconfig.so.1 => x\nlibicuuc.so.76 => x\nlibinput.so.10 => x\nlibxkbcommon.so.0 => x\n";

        //Act / Assert — the versioned libxkbcommon.so.0 satisfies the substring
        DeviceProvisioning.MissingNativeDependencies(ldconfig).Should().BeEmpty();
    }

    [Fact]
    public void MissingNativeDependencies_reports_all_for_a_bare_device()
        => DeviceProvisioning.MissingNativeDependencies("")
            .Should().HaveCount(DeviceProvisioning.FrameBufferNativeDependencies.Count);

    [Fact]
    public void AptInstallCommand_lists_the_packages_non_interactively()
        => DeviceProvisioning.AptInstallCommand(new[] { "libfontconfig1", "libinput10" })
            .Should().Be("sudo apt install -y libfontconfig1 libinput10");

    [Fact]
    public void The_fatal_native_dependencies_are_fontconfig_and_icu()
        => DeviceProvisioning.FrameBufferNativeDependencies
            .Where(dependency => dependency.Fatal)
            .Select(dependency => dependency.AptPackage)
            .Should().BeEquivalentTo(new[] { "libfontconfig1", "libicu-dev" });

    [Fact]
    public void Command_builders_use_the_given_user_and_the_console_tty()
    {
        DeviceProvisioning.AddVideoInputGroupsCommand("debian")
            .Should().Be("sudo usermod -aG video,input debian");
        DeviceProvisioning.MaskGettyCommand
            .Should().Be("sudo systemctl mask --now getty@tty1");
        DeviceProvisioning.PersistentGroupsCommand("debian").Should().Be("id -nG debian");
        DeviceProvisioning.GettyEnabledCommand.Should().Be("systemctl is-enabled getty@tty1");
    }
}
