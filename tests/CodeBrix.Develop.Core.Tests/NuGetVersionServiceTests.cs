//
// NuGetVersionServiceTests.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class NuGetVersionServiceTests
{
    [Theory]
    [InlineData("CodeBrix.Platform.ApacheLicenseForever", true)]
    [InlineData("codebrix.skiasvg.mitlicenseforever", true)]
    [InlineData("FreePPlus", true)]
    [InlineData("FreePPlus.MitLicenseForever", true)]
    [InlineData("SilverAssertions.ApacheLicenseForever", true)]
    [InlineData("SkiaSharp.Skottie", false)]
    [InlineData("Microsoft.Extensions.Hosting", false)]
    public void IsCodeBrixPackageId_matches_the_codebrix_family(string packageId, bool expected)
        => NuGetVersionService.IsCodeBrixPackageId(packageId).Should().Be(expected);

    [Fact]
    public void SelectLatest_picks_the_last_stable_version()
        => NuGetVersionService.SelectLatest(new[] { "1.0.48", "1.0.117", "1.0.164.180", "1.1.0-beta.1" })
            .Should().Be("1.0.164.180");

    [Fact]
    public void SelectLatest_falls_back_to_prerelease_when_nothing_stable_exists()
        => NuGetVersionService.SelectLatest(new[] { "1.0.0-alpha", "1.0.0-beta.2" }).Should().Be("1.0.0-beta.2");

    [Fact]
    public void SelectLatest_of_an_empty_list_is_null()
        => (NuGetVersionService.SelectLatest(System.Array.Empty<string>()) == null).Should().BeTrue();

    [Theory]
    [InlineData("1.0.164.180", "1.0.164.180", true)]  // exact match
    [InlineData("1.0.165.10", "1.0.164.180", true)]   // referenced is newer (local build)
    [InlineData("1.0.117", "1.0.164.180", false)]     // behind
    [InlineData("1.0.186.1273", "1.0.187.116", false)]
    [InlineData("2.0.0-beta.1", "1.9.0", true)]       // prerelease numeric part compares
    public void IsUpToDate_compares_versions_numerically(string referenced, string latest, bool expected)
        => NuGetVersionService.IsUpToDate(referenced, latest).Should().Be(expected);
}
