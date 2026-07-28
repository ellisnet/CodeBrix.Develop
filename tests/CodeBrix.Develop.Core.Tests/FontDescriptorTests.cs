//
// FontDescriptorTests.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using CodeBrix.Develop.Core.Templates;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class FontDescriptorTests
{
    const string Minimal = """
        {
          "schemaVersion": 1,
          "packageId": "Some.Font.Package",
          "displayName": "Some Font",
          "fontFamilyUri": "ms-appx:///Some.Font/Fonts/Some.ttf",
          "resourceKey": "SomeFontFont"
        }
        """;

    [Fact]
    public void A_descriptor_round_trips_every_field()
    {
        //Act
        var descriptor = FontDescriptor.Parse(ApplicationTemplateTests.RobotoDescriptorJson);

        //Assert
        descriptor.SchemaVersion.Should().Be(1);
        descriptor.PackageId.Should().Be("CodeBrix.Platform.Fonts.Roboto.OflLicenseForever");
        descriptor.DisplayName.Should().Be("Roboto");
        descriptor.FontFamilyUri.Should().Be("ms-appx:///CodeBrix.Platform.Fonts.Roboto/Fonts/Roboto.ttf");
        descriptor.ResourceKey.Should().Be("RobotoFont");
        descriptor.FallbackFontUris.Count.Should().Be(2);
        descriptor.KeyboardLayouts.Should().Contain("ka");
    }

    [Fact]
    public void Companion_fonts_are_optional()
    {
        //Arrange/Act — a package with no companions simply omits the property.
        var descriptor = FontDescriptor.Parse(Minimal);

        //Assert
        descriptor.FallbackFontUris.Should().BeEmpty();
        descriptor.KeyboardLayouts.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_schema_version_is_refused()
    {
        //Arrange — a descriptor from a future CodeBrix.Develop. Guessing at it
        //would risk generating a subtly wrong application.
        var json = Minimal.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        //Act
        var act = () => FontDescriptor.Parse(json);

        //Assert
        act.Should().Throw<InvalidDataException>().WithMessage("*schemaVersion 2*");
    }

    [Theory]
    [InlineData("packageId")]
    [InlineData("displayName")]
    [InlineData("fontFamilyUri")]
    [InlineData("resourceKey")]
    public void A_missing_required_field_is_refused(string field)
    {
        //Arrange
        var json = Minimal.Replace($"\"{field}\"", "\"ignored\"");

        //Act
        var act = () => FontDescriptor.Parse(json);

        //Assert
        act.Should().Throw<InvalidDataException>().WithMessage($"*{field}*");
    }

    [Fact]
    public void A_family_fragment_on_the_font_uri_is_refused()
    {
        //Arrange — a "#FamilyName" fragment disables the startup font-manifest
        //preload in CodeBrix.Platform, so it must never reach a generated app.
        var json = Minimal.Replace("Some.ttf", "Some.ttf#Some Font");

        //Act
        var act = () => FontDescriptor.Parse(json);

        //Assert
        act.Should().Throw<InvalidDataException>().WithMessage("*fragment*");
    }

    [Fact]
    public void Malformed_json_is_refused()
    {
        //Act
        var act = () => FontDescriptor.Parse("{ not json");

        //Assert
        act.Should().Throw<InvalidDataException>();
    }
}
