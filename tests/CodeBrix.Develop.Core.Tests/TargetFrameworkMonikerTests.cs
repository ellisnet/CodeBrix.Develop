using System;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class TargetFrameworkMonikerTests
{
    [Fact]
    public void An_android_moniker_splits_into_framework_and_platform()
    {
        var tfm = TargetFrameworkMoniker.Parse("net11.0-android37.0");

        tfm.FrameworkVersion.Should().Be(new Version(11, 0));
        tfm.PlatformIdentifier.Should().Be("android");
        tfm.PlatformVersion.Should().Be(new Version(37, 0));
        tfm.IsAndroid.Should().BeTrue();
        tfm.Moniker.Should().Be("net11.0-android37.0");
    }

    [Fact]
    public void The_dotnet_10_android_moniker_parses_too()
    {
        var tfm = TargetFrameworkMoniker.Parse("net10.0-android36.1");

        tfm.FrameworkVersion.Should().Be(new Version(10, 0));
        tfm.PlatformVersion.Should().Be(new Version(36, 1));
        tfm.FrameworkVersionText.Should().Be("10.0");
        tfm.PlatformVersionText.Should().Be("36.1");
    }

    [Fact]
    public void A_plain_moniker_has_no_platform()
    {
        var tfm = TargetFrameworkMoniker.Parse("net10.0");

        tfm.FrameworkVersion.Should().Be(new Version(10, 0));
        tfm.PlatformIdentifier.Should().BeEmpty();
        tfm.PlatformVersion.Should().BeNull();
        tfm.IsAndroid.Should().BeFalse();
    }

    [Fact]
    public void A_platform_without_a_version_still_parses()
        => TargetFrameworkMoniker.Parse("net10.0-windows").PlatformIdentifier.Should().Be("windows");

    [Fact]
    public void A_long_platform_version_is_kept_whole()
        => TargetFrameworkMoniker.Parse("net10.0-windows10.0.19041.0")
            .PlatformVersion.Should().Be(new Version(10, 0, 19041, 0));

    [Theory]
    [InlineData("netstandard2.0")]   // starts with "net" but is not a version
    [InlineData("net472")]           // old-style, no dot
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void What_is_not_a_dotnet5_style_moniker_parses_as_null(string moniker)
        => TargetFrameworkMoniker.Parse(moniker).Should().BeNull();

    [Fact]
    public void Whitespace_around_a_moniker_is_tolerated()
        // MSBuild's TargetFrameworks split can leave padding behind.
        => TargetFrameworkMoniker.Parse("  net11.0-android37.0  ").IsAndroid.Should().BeTrue();

    [Fact]
    public void Parsing_is_case_insensitive_on_the_net_prefix()
        => TargetFrameworkMoniker.Parse("NET11.0-Android37.0").PlatformIdentifier
            .Should().Be("android"); // identifier normalized to lower case

    [Fact]
    public void Describe_reads_as_prose()
    {
        TargetFrameworkMoniker.Parse("net11.0-android37.0").Describe()
            .Should().Be(".NET 11.0 / android 37.0");
        TargetFrameworkMoniker.Parse("net10.0").Describe().Should().Be(".NET 10.0");
    }
}
