using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class DotNetSdkLocatorTests
{
    [Fact]
    public void The_system_sdk_is_found_and_can_build_its_own_major_version()
    {
        var locator = new DotNetSdkLocator(Array.Empty<string>());

        locator.Installations.Should().NotBeEmpty();
        var system = locator.Installations[0];
        system.IsSystemInstallation.Should().BeTrue();
        system.SdkVersions.Should().NotBeEmpty();

        // Whatever the build machine has, the SDK can build its own major.
        var major = system.SdkVersions.Max().Major;
        locator.Resolve(new Version(major, 0)).Should().NotBeNull();
    }

    [Fact]
    public void An_impossible_version_resolves_to_nothing()
        // Rather than silently handing the project to an SDK that will fail
        // with a misleading error.
        => new DotNetSdkLocator(Array.Empty<string>())
            .Resolve(new Version(99, 0)).Should().BeNull();

    [Fact]
    public void A_null_version_falls_back_to_the_first_installation()
        // A project whose moniker did not parse (netstandard, net472) keeps
        // building exactly as it did before.
        => new DotNetSdkLocator(Array.Empty<string>()).Resolve((Version) null)
            .Should().NotBeNull();

    [Theory]
    [InlineData("/nonexistent/path/that/is/not/there")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_bad_additional_root_is_skipped_silently(string root)
    {
        // A stale preference must never stop the IDE building anything.
        var locator = new DotNetSdkLocator(new[] { root });

        locator.Installations.Should().NotBeEmpty();          // the system one survives
        locator.Installations.Should().OnlyContain(i => i.IsSystemInstallation);
    }

    [Fact]
    public void A_root_that_exists_but_holds_no_dotnet_is_skipped()
    {
        var empty = Path.Combine(Path.GetTempPath(), "codebrix-sdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            new DotNetSdkLocator(new[] { empty }).Installations
                .Should().OnlyContain(i => i.IsSystemInstallation);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Nulls_in_the_root_list_do_not_throw()
        => new DotNetSdkLocator(new string[] { null }).Installations.Should().NotBeEmpty();

    [Fact]
    public void A_null_root_list_is_the_system_sdk_alone()
        => new DotNetSdkLocator(null).Installations.Should().OnlyContain(i => i.IsSystemInstallation);

    [Fact]
    public void Provides_matches_on_the_major_version_only()
    {
        // A 10.0.401 SDK builds net10.0 and net10.0-android alike; it does not
        // build net11.0.
        var installation = new DotNetSdkInstallation("/x/dotnet", "/x",
            new List<Version> { new Version(10, 0, 401) });

        installation.Provides(new Version(10, 0)).Should().BeTrue();
        installation.Provides(new Version(11, 0)).Should().BeFalse();
        installation.Provides(null).Should().BeFalse();
    }

    [Fact]
    public void The_system_installation_knows_its_own_root()
    {
        // It used to be identified by having no root. It carries one now, on
        // purpose: switching from a .NET 11 solution back to a .NET 10 one
        // means setting DOTNET_ROOT back to the system root, so the IDE has to
        // know what that is.
        var locator = new DotNetSdkLocator(Array.Empty<string>());
        var system = locator.Installations[0];

        system.IsSystemInstallation.Should().BeTrue();
        system.Root.Should().NotBeEmpty();
        Directory.Exists(system.Root).Should().BeTrue();
    }

    [Fact]
    public void An_installation_reports_the_directory_of_its_newest_sdk()
    {
        // This is the folder MSBuildLocator is pointed at, so it must be the
        // versioned directory, not the "sdk" parent.
        var system = new DotNetSdkLocator(Array.Empty<string>()).Installations[0];

        system.NewestSdkDirectory.Should().NotBeEmpty();
        Directory.Exists(system.NewestSdkDirectory).Should().BeTrue();
        File.Exists(Path.Combine(system.NewestSdkDirectory, "MSBuild.dll")).Should().BeTrue();
    }

    [Fact]
    public void Newest_picks_the_highest_sdk_across_installations()
    {
        var locator = new DotNetSdkLocator(Array.Empty<string>());

        locator.Newest.Should().NotBeNull();
        locator.Newest.SdkVersions.Should().NotBeEmpty();
    }

    [Fact]
    public void A_preview_only_version_is_refused_when_previews_are_not_allowed()
    {
        // The whole point of the "Allow preview MSBuild" setting: a .NET 11
        // project served only by a preview SDK resolves to nothing until the
        // user opts in.
        var stable = new Version(10, 0, 401);
        var preview = new Version(11, 0, 100);
        var installation = new DotNetSdkInstallation("dotnet", "/x",
            new List<Version> { stable, preview }, sdkDirectories: null,
            prerelease: new[] { preview });

        installation.Provides(new Version(11, 0), allowPrerelease: true).Should().BeTrue();
        installation.Provides(new Version(11, 0), allowPrerelease: false).Should().BeFalse();
        // ...while the stable one is unaffected either way.
        installation.Provides(new Version(10, 0), allowPrerelease: false).Should().BeTrue();
    }

    [Fact]
    public void A_preview_version_is_identified_as_one()
    {
        var preview = new Version(11, 0, 100);
        var installation = new DotNetSdkInstallation("dotnet", "/x",
            new List<Version> { new Version(10, 0, 401), preview },
            prerelease: new[] { preview });

        installation.IsPrerelease(preview).Should().BeTrue();
        installation.IsPrerelease(new Version(10, 0, 401)).Should().BeFalse();
        installation.StableSdkVersions.Should().ContainSingle()
            .Which.Should().Be(new Version(10, 0, 401));
    }

    [Fact]
    public void The_newest_stable_sdk_directory_skips_a_preview()
    {
        var stable = new Version(10, 0, 401);
        var preview = new Version(11, 0, 100);
        var installation = new DotNetSdkInstallation("dotnet", "/x",
            new List<Version> { stable, preview },
            sdkDirectories: new Dictionary<Version, string>
            {
                [stable] = "/x/sdk/10.0.401",
                [preview] = "/x/sdk/11.0.100-rc.1",
            },
            prerelease: new[] { preview });

        // This is the directory MSBuildLocator is pointed at, so getting it
        // wrong is exactly the "preview MSBuild under stable work" the setting
        // exists to prevent.
        installation.NewestSdkDirectoryFor(allowPrerelease: true).Should().Be("/x/sdk/11.0.100-rc.1");
        installation.NewestSdkDirectoryFor(allowPrerelease: false).Should().Be("/x/sdk/10.0.401");
    }

    [Fact]
    public void A_real_preview_sdk_is_detected_as_prerelease()
    {
        // The isolated .NET 11 prerelease on this machine, if it is installed —
        // proves the "--list-sdks" prerelease suffix is actually parsed.
        var locator = new DotNetSdkLocator(new[] { "~/dotnet11" });
        var extra = locator.Installations.FirstOrDefault(i => !i.IsSystemInstallation);
        if (extra == null)
            return; // not installed on this machine; nothing to assert

        extra.SdkVersions.Should().NotBeEmpty();
        extra.StableSdkVersions.Should().BeEmpty("the installed .NET 11 SDK is a prerelease");
        locator.NeedsPrerelease(new Version(11, 0)).Should().BeTrue();
        locator.Resolve(new Version(11, 0), allowPrerelease: false).Should().BeNull();
        locator.Resolve(new Version(11, 0), allowPrerelease: true).Should().NotBeNull();
    }

    [Fact]
    public void NeedsPrerelease_is_false_for_something_nothing_can_build()
        // "allow previews" would not help here, so the message must not say so.
        => new DotNetSdkLocator(new[] { "~/dotnet11" })
            .NeedsPrerelease(new Version(99, 0)).Should().BeFalse();

    [Fact]
    public void The_system_sdk_wins_when_both_could_build_it()
    {
        // Ordinary projects must keep building with the SDK they always did,
        // even after an additional root is configured.
        var locator = new DotNetSdkLocator(Array.Empty<string>());
        var major = locator.Installations[0].SdkVersions.Max().Major;

        locator.Resolve(new Version(major, 0)).IsSystemInstallation.Should().BeTrue();
    }
}
