//
// PackageReferenceRewriterTests.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class PackageReferenceRewriterTests
{
    [Fact]
    public void UpdateVersion_rewrites_the_version_attribute()
    {
        //Arrange
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <!-- the platform -->
                <PackageReference Include="CodeBrix.Platform.ApacheLicenseForever" Version="1.0.186.1273" />
                <PackageReference Include="SkiaSharp.Skottie" Version="4.150.1" />
              </ItemGroup>
            </Project>
            """;

        //Act
        var result = PackageReferenceRewriter.UpdateVersion(csproj, "CodeBrix.Platform.ApacheLicenseForever", "1.0.187.116", out var updated);

        //Assert — surgical: only the version string changed, comments and
        //formatting intact, the non-target package untouched.
        updated.Should().BeTrue();
        result.Should().Be(csproj.Replace("1.0.186.1273", "1.0.187.116"));
    }

    [Fact]
    public void UpdateVersion_rewrites_the_version_child_element()
    {
        //Arrange
        const string csproj = """
            <ItemGroup>
              <PackageReference Include="CodeBrix.SkiaSvg.MitLicenseForever">
                <Version>1.0.180.239</Version>
              </PackageReference>
            </ItemGroup>
            """;

        //Act
        var result = PackageReferenceRewriter.UpdateVersion(csproj, "CodeBrix.SkiaSvg.MitLicenseForever", "1.0.181.5", out var updated);

        //Assert
        updated.Should().BeTrue();
        result.Should().Be(csproj.Replace("1.0.180.239", "1.0.181.5"));
    }

    [Fact]
    public void UpdateVersion_handles_attribute_order_and_id_casing()
    {
        //Arrange — Version before Include, and a differently-cased id.
        const string csproj = """<PackageReference Version="1.0.48" Include="silverassertions.apachelicenseforever" />""";

        //Act
        var result = PackageReferenceRewriter.UpdateVersion(csproj, "SilverAssertions.ApacheLicenseForever", "1.0.164.180", out var updated);

        //Assert
        updated.Should().BeTrue();
        result.Should().Be("""<PackageReference Version="1.0.164.180" Include="silverassertions.apachelicenseforever" />""");
    }

    [Fact]
    public void UpdateVersion_updates_every_occurrence_of_the_package()
    {
        //Arrange — the same package in two conditioned ItemGroups.
        const string csproj = """
            <ItemGroup Condition="'$(A)' == 'true'">
              <PackageReference Include="FreePPlus" Version="1.0.10" />
            </ItemGroup>
            <ItemGroup Condition="'$(A)' != 'true'">
              <PackageReference Include="FreePPlus" Version="1.0.10" />
            </ItemGroup>
            """;

        //Act
        var result = PackageReferenceRewriter.UpdateVersion(csproj, "FreePPlus", "1.0.20", out var updated);

        //Assert
        updated.Should().BeTrue();
        result.Should().Be(csproj.Replace("1.0.10", "1.0.20"));
    }

    [Fact]
    public void UpdateVersion_leaves_versionless_references_alone()
    {
        //Arrange — central package management style, no Version anywhere.
        const string csproj = """<PackageReference Include="CodeBrix.Imaging" />""";

        //Act
        var result = PackageReferenceRewriter.UpdateVersion(csproj, "CodeBrix.Imaging", "2.0.0", out var updated);

        //Assert
        updated.Should().BeFalse();
        result.Should().Be(csproj);
    }

    [Fact]
    public void UpdateVersion_ignores_other_packages()
    {
        //Arrange
        const string csproj = """<PackageReference Include="CodeBrix.Imaging.Drawing" Version="1.0.0" />""";

        //Act
        var result = PackageReferenceRewriter.UpdateVersion(csproj, "CodeBrix.Imaging", "2.0.0", out var updated);

        //Assert — "CodeBrix.Imaging" must not match "CodeBrix.Imaging.Drawing".
        updated.Should().BeFalse();
        result.Should().Be(csproj);
    }
}
