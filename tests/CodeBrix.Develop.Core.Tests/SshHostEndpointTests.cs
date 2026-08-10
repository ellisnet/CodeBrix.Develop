using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class SshHostEndpointTests
{
    [Fact]
    public void TryParse_takes_a_plain_host_with_the_default_port()
    {
        //Arrange / Act
        var endpoint = SshHostEndpoint.TryParse("mydevice.local");

        //Assert
        endpoint.Host.Should().Be("mydevice.local");
        endpoint.Port.Should().Be(22);
    }

    [Fact]
    public void TryParse_takes_a_host_with_a_port_suffix()
    {
        //Arrange / Act
        var endpoint = SshHostEndpoint.TryParse("192.168.1.20:2222");

        //Assert
        endpoint.Host.Should().Be("192.168.1.20");
        endpoint.Port.Should().Be(2222);
    }

    [Fact]
    public void TryParse_trims_surrounding_whitespace()
    {
        //Arrange / Act
        var endpoint = SshHostEndpoint.TryParse("  mydevice  ");

        //Assert
        endpoint.Host.Should().Be("mydevice");
        endpoint.Port.Should().Be(22);
    }

    [Fact]
    public void TryParse_treats_a_bare_ipv6_address_as_all_host()
    {
        //Arrange / Act
        var endpoint = SshHostEndpoint.TryParse("fe80::1");

        //Assert
        endpoint.Host.Should().Be("fe80::1");
        endpoint.Port.Should().Be(22);
    }

    [Fact]
    public void TryParse_takes_a_bracketed_ipv6_address_with_a_port()
    {
        //Arrange / Act
        var endpoint = SshHostEndpoint.TryParse("[fe80::1]:2222");

        //Assert
        endpoint.Host.Should().Be("fe80::1");
        endpoint.Port.Should().Be(2222);
    }

    [Fact]
    public void TryParse_takes_a_bracketed_ipv6_address_without_a_port()
    {
        //Arrange / Act
        var endpoint = SshHostEndpoint.TryParse("[fe80::1]");

        //Assert
        endpoint.Host.Should().Be("fe80::1");
        endpoint.Port.Should().Be(22);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":22")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:abc")]
    [InlineData("host:-1")]
    [InlineData("my host")]
    [InlineData("[fe80::1")]
    [InlineData("[fe80::1]22")]
    [InlineData("[]:22")]
    public void TryParse_rejects_invalid_input(string input)
        => SshHostEndpoint.TryParse(input).Should().BeNull();
}
