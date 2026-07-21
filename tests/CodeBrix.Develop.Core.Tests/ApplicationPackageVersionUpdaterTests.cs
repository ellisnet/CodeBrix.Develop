using System;
using System.Collections.Generic;
using CodeBrix.Develop.Core.Templates;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class ApplicationPackageVersionUpdaterTests
{
    const string PlatformVersion = "1.0.201.336";
    const string SkiaVersion = "4.150.1";
    const string HarfBuzzVersion = "14.2.1.1";

    static IReadOnlyDictionary<string, string> Latest(params (string Id, string Version)[] entries)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in entries)
            versions[id] = version;
        return versions;
    }

    [Fact]
    public void The_platform_ceiling_holds_back_the_core_and_head_packages()
    {
        //Arrange — nuget.org has newer head packages than the platform package.
        var latest = Latest(
            ("CodeBrix.Platform.ApacheLicenseForever", "1.0.202.900"),
            ("CodeBrix.Platform.Runtime.Skia.X11.ApacheLicenseForever", "1.0.202.900"),
            ("CodeBrix.Platform.Runtime.Skia.Wpf.ApacheLicenseForever", "1.0.202.900"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, HarfBuzzVersion);

        //Assert
        result["CodeBrix.Platform.ApacheLicenseForever"].Should().Be(PlatformVersion);
        result["CodeBrix.Platform.Runtime.Skia.X11.ApacheLicenseForever"].Should().Be(PlatformVersion);
        result["CodeBrix.Platform.Runtime.Skia.Wpf.ApacheLicenseForever"].Should().Be(PlatformVersion);
    }

    [Fact]
    public void The_skia_ceiling_covers_the_whole_skia_family()
    {
        //Arrange — SkiaSharp has shipped a version CodeBrix.Platform has not adopted.
        var latest = Latest(
            ("SkiaSharp", "4.151.0"),
            ("SkiaSharp.HarfBuzz", "4.151.0"),
            ("SkiaSharp.NativeAssets.Linux", "4.151.0"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, HarfBuzzVersion);

        //Assert
        result["SkiaSharp"].Should().Be(SkiaVersion);
        result["SkiaSharp.HarfBuzz"].Should().Be(SkiaVersion);
        result["SkiaSharp.NativeAssets.Linux"].Should().Be(SkiaVersion);
    }

    [Fact]
    public void The_harfbuzz_ceiling_covers_the_whole_harfbuzz_family()
    {
        //Arrange
        var latest = Latest(
            ("HarfBuzzSharp", "15.0.0"),
            ("HarfBuzzSharp.NativeAssets.Linux", "15.0.0"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, HarfBuzzVersion);

        //Assert
        result["HarfBuzzSharp"].Should().Be(HarfBuzzVersion);
        result["HarfBuzzSharp.NativeAssets.Linux"].Should().Be(HarfBuzzVersion);
    }

    [Fact]
    public void Package_ids_are_matched_case_insensitively()
    {
        //Arrange — the X11 and Wayland heads published before 2026-07-20 spell
        // the dependency "HarfbuzzSharp"; NuGet ids are case-insensitive.
        var latest = Latest(
            ("harfbuzzsharp", "15.0.0"),
            ("skiasharp.nativeassets.linux", "4.151.0"),
            ("codebrix.platform.apachelicenseforever", "1.0.202.900"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, HarfBuzzVersion);

        //Assert
        result["HarfBuzzSharp"].Should().Be(HarfBuzzVersion);
        result["SkiaSharp.NativeAssets.Linux"].Should().Be(SkiaVersion);
        result["CodeBrix.Platform.ApacheLicenseForever"].Should().Be(PlatformVersion);
    }

    [Fact]
    public void Uncapped_packages_keep_their_latest_version()
    {
        //Arrange — everything outside the three ceilings goes to its own
        // latest, the non-head CodeBrix.Platform packages included.
        var latest = Latest(
            ("Microsoft.Extensions.Hosting", "10.1.0"),
            ("xunit.v3", "3.2.2"),
            ("CodeBrix.Platform.Fonts.Roboto.OflLicenseForever", "1.0.181.661"),
            ("CodeBrix.Platform.Runtime.Skia.ApacheLicenseForever", "1.0.202.900"),
            // A dotted family match must not catch an unrelated id.
            ("SkiaSharpener", "9.9.9"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, HarfBuzzVersion);

        //Assert
        result["Microsoft.Extensions.Hosting"].Should().Be("10.1.0");
        result["xunit.v3"].Should().Be("3.2.2");
        result["CodeBrix.Platform.Fonts.Roboto.OflLicenseForever"].Should().Be("1.0.181.661");
        result["CodeBrix.Platform.Runtime.Skia.ApacheLicenseForever"].Should().Be("1.0.202.900");
        result["SkiaSharpener"].Should().Be("9.9.9");
    }

    [Fact]
    public void A_ceiling_never_raises_a_version_that_is_already_older()
    {
        //Arrange — the latest release is behind the ceiling.
        var latest = Latest(("SkiaSharp", "4.148.0"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, HarfBuzzVersion);

        //Assert
        result["SkiaSharp"].Should().Be("4.148.0");
    }

    [Fact]
    public void An_absent_ceiling_leaves_the_latest_version_alone()
    {
        //Arrange — no generated head declared a HarfBuzzSharp dependency.
        var latest = Latest(("HarfBuzzSharp", "15.0.0"), ("SkiaSharp", "4.151.0"));

        //Act
        var result = ApplicationPackageVersionUpdater.ApplyCeilings(
            latest, PlatformVersion, SkiaVersion, harfBuzzVersion: null);

        //Assert
        result["HarfBuzzSharp"].Should().Be("15.0.0");
        result["SkiaSharp"].Should().Be(SkiaVersion);
    }

    [Fact]
    public void The_six_head_packages_are_the_primary_head_package_ids()
    {
        //Assert
        ApplicationPackageVersionUpdater.PrimaryHeadPackageIds.Count.Should().Be(6);
        ApplicationPackageVersionUpdater.PrimaryHeadPackageIds
            .Should().Contain("CodeBrix.Platform.Runtime.Skia.X11.ApacheLicenseForever");
        ApplicationPackageVersionUpdater.PrimaryHeadPackageIds
            .Should().Contain("CodeBrix.Platform.Runtime.Skia.MacOS.ApacheLicenseForever");
    }
}
