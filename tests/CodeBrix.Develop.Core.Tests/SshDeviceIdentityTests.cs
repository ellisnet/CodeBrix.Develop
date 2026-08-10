using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class SshDeviceIdentityTests
{
    const string Tw700HostnamectlJson =
        "{\"Hostname\":\"tw700debian\",\"StaticHostname\":\"tw700debian\"," +
        "\"IconName\":\"computer-handset\",\"Chassis\":\"handset\"," +
        "\"KernelName\":\"Linux\",\"KernelRelease\":\"6.12.101+deb13-amd64\"," +
        "\"KernelVersion\":\"#1 SMP PREEMPT_DYNAMIC\"," +
        "\"OperatingSystemPrettyName\":\"Debian GNU/Linux 13 (trixie)\"," +
        "\"HardwareVendor\":\"WinBook\",\"HardwareModel\":\"TW700\"," +
        "\"ProductUUID\":null}";

    [Fact]
    public void Create_parses_the_tw700_outputs()
    {
        //Arrange / Act — the cat outputs end with a newline, as on the wire
        var identity = SshDeviceIdentity.Create(
            "WinBook\n", "TW700\n", Tw700HostnamectlJson, "x86_64\n");

        //Assert
        identity.Vendor.Should().Be("WinBook");
        identity.Model.Should().Be("TW700");
        identity.OperatingSystem.Should().Be("Debian GNU/Linux 13 (trixie)");
        identity.Kernel.Should().Be("Linux 6.12.101+deb13-amd64");
        identity.Architecture.Should().Be("x86-64");
    }

    [Fact]
    public void Create_turns_blank_outputs_into_unknown()
    {
        //Arrange / Act
        var identity = SshDeviceIdentity.Create("", "   \n", null, "");

        //Assert
        identity.Vendor.Should().Be("unknown");
        identity.Model.Should().Be("unknown");
        identity.OperatingSystem.Should().Be("unknown");
        identity.Kernel.Should().Be("unknown");
        identity.Architecture.Should().Be("unknown");
    }

    [Fact]
    public void Create_turns_malformed_hostnamectl_json_into_unknown()
    {
        //Arrange / Act
        var identity = SshDeviceIdentity.Create("WinBook", "TW700", "not json at all", "x86_64");

        //Assert
        identity.OperatingSystem.Should().Be("unknown");
        identity.Kernel.Should().Be("unknown");
    }

    [Fact]
    public void Create_turns_missing_json_keys_into_unknown()
    {
        //Arrange / Act
        var identity = SshDeviceIdentity.Create("WinBook", "TW700", "{\"Hostname\":\"x\"}", "x86_64");

        //Assert
        identity.OperatingSystem.Should().Be("unknown");
        identity.Kernel.Should().Be("unknown");
    }

    [Fact]
    public void Create_keeps_a_kernel_with_only_a_name()
    {
        //Arrange / Act
        var identity = SshDeviceIdentity.Create("v", "m", "{\"KernelName\":\"Linux\"}", "x86_64");

        //Assert
        identity.Kernel.Should().Be("Linux");
    }

    [Theory]
    [InlineData("x86_64", "x86-64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("armv7l", "arm")]
    [InlineData("i686", "x86")]
    [InlineData("riscv64", "riscv64")]
    public void Create_maps_uname_machine_types_to_systemd_spellings(string uname, string expected)
        => SshDeviceIdentity.Create("v", "m", null, uname).Architecture.Should().Be(expected);
}
