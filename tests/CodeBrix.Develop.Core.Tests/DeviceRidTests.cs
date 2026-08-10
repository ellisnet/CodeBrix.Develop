using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class DeviceRidTests
{
    [Theory]
    [InlineData("x86-64", "linux-x64")]   // the systemd spelling we capture
    [InlineData("x86_64", "linux-x64")]   // the uname spelling, just in case
    [InlineData("arm64", "linux-arm64")]
    [InlineData("aarch64", "linux-arm64")]
    [InlineData("arm", "linux-arm")]
    [InlineData("armv7l", "linux-arm")]
    public void ForArchitecture_maps_known_architectures(string architecture, string expected)
        => DeviceRid.ForArchitecture(architecture).Should().Be(expected);

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("sparc64")]
    public void ForArchitecture_is_null_for_the_unrecognized(string architecture)
        => DeviceRid.ForArchitecture(architecture).Should().BeNull();
}
