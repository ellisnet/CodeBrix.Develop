//
// FontSwapTests.cs
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

public class FontSwapTests
{
    static FontDescriptor OpenSans() =>
        FontDescriptor.Parse(ApplicationTemplateTests.OpenSansDescriptorJson);

    static FontDescriptor Roboto() =>
        FontDescriptor.Parse(ApplicationTemplateTests.RobotoDescriptorJson);

    [Fact]
    public void Choosing_the_templates_own_font_is_not_a_swap()
    {
        //Act — the template already says exactly this, so there is nothing to do.
        var swap = FontSwap.For(OpenSans(), OpenSans());

        //Assert
        swap.Should().BeNull();
    }

    [Fact]
    public void A_font_that_reuses_the_template_fonts_resource_key_is_refused()
    {
        //Arrange — the swap replaces these values as raw text, so a collision
        //would produce an application that is subtly wrong rather than broken.
        var colliding = FontDescriptor.Parse(
            ApplicationTemplateTests.RobotoDescriptorJson.Replace("RobotoFont", "OpenSansFont"));

        //Act
        var act = () => FontSwap.For(OpenSans(), colliding);

        //Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*resourceKey*");
    }

    [Fact]
    public void Applying_a_swap_replaces_all_four_values()
    {
        //Arrange
        var swap = FontSwap.For(OpenSans(), Roboto());
        const string text = """
            <!-- Open Sans font -->
            <FontFamily x:Key="OpenSansFont">ms-appx:///CodeBrix.Platform.Fonts.OpenSans/Fonts/OpenSans.ttf</FontFamily>
            <PackageReference Include="CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever" Version="1.0.0.1" />
            """;

        //Act
        var result = swap.Apply(text, "src/App.UI/App.xaml");

        //Assert
        result.Should().NotContain("OpenSans");
        result.Should().NotContain("Open Sans");
        result.Should().Contain("RobotoFont");
        result.Should().Contain("ms-appx:///CodeBrix.Platform.Fonts.Roboto/Fonts/Roboto.ttf");
        result.Should().Contain("CodeBrix.Platform.Fonts.Roboto.OflLicenseForever");
        result.Should().Contain("Version=\"1.0.0.1\"");
    }

    [Fact]
    public void Companion_fonts_are_registered_after_the_default_font()
    {
        //Arrange
        var swap = FontSwap.For(OpenSans(), Roboto());
        const string codeBehind = """
            public App()
            {
                //Set Open Sans as the default font for all text in the application
                global::CodeBrix.Platform.UI.FeatureConfiguration.Font.DefaultTextFontFamily =
                    "ms-appx:///CodeBrix.Platform.Fonts.OpenSans/Fonts/OpenSans.ttf";

                InitializeComponent();
            }
            """;

        //Act
        var result = swap.Apply(codeBehind, "src/App.UI/App.xaml.cs");

        //Assert
        result.Should().Contain("FeatureConfiguration.Font.FallbackFontFamilies");
        result.Should().Contain("NotoSansArmenian.ttf");
        result.Should().Contain("NotoSansGeorgian.ttf");
        result.IndexOf("FallbackFontFamilies", StringComparison.Ordinal)
            .Should().BeGreaterThan(result.IndexOf("DefaultTextFontFamily", StringComparison.Ordinal));
        //...and the rest of the constructor still follows it.
        result.IndexOf("InitializeComponent", StringComparison.Ordinal)
            .Should().BeGreaterThan(result.IndexOf("FallbackFontFamilies", StringComparison.Ordinal));
    }

    [Fact]
    public void The_registration_matches_the_indentation_of_the_line_it_follows()
    {
        //Arrange — generated source that does not line up reads as a bug.
        var swap = FontSwap.For(OpenSans(), Roboto());
        const string codeBehind = """
            public App()
            {
                global::CodeBrix.Platform.UI.FeatureConfiguration.Font.DefaultTextFontFamily =
                    "ms-appx:///CodeBrix.Platform.Fonts.OpenSans/Fonts/OpenSans.ttf";
            }
            """;

        //Act
        var result = swap.Apply(codeBehind, "App.xaml.cs");

        //Assert — the registration lines up with the assignment it follows,
        //whatever that file's indentation happens to be.
        IndentOfLineContaining(result, "FallbackFontFamilies =")
            .Should().Be(IndentOfLineContaining(result, "DefaultTextFontFamily ="));
    }

    static string IndentOfLineContaining(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0);
        var lineStart = text.LastIndexOf('\n', index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var end = lineStart;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
        {
            end++;
        }
        return text.Substring(lineStart, end - lineStart);
    }

    [Fact]
    public void A_font_without_companions_leaves_the_code_behind_alone()
    {
        //Arrange — Open Sans as a non-template font: swapped, but with nothing
        //to register.
        var noCompanions = FontDescriptor.Parse(
            ApplicationTemplateTests.RobotoDescriptorJson
                .Replace("\"fallbackFontUris\"", "\"ignoredFontUris\""));
        var swap = FontSwap.For(OpenSans(), noCompanions);
        const string codeBehind = """
                global::CodeBrix.Platform.UI.FeatureConfiguration.Font.DefaultTextFontFamily =
                    "ms-appx:///CodeBrix.Platform.Fonts.OpenSans/Fonts/OpenSans.ttf";
            """;

        //Act
        var result = swap.Apply(codeBehind, "App.xaml.cs");

        //Assert
        result.Should().NotContain("FallbackFontFamilies");
    }

    [Fact]
    public void Only_the_application_code_behind_gets_the_registration()
    {
        //Arrange — a .csproj that happens to mention the font must not sprout
        //C# code.
        var swap = FontSwap.For(OpenSans(), Roboto());
        const string csproj =
            "<PackageReference Include=\"CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever\" Version=\"1.0.0.1\" />";

        //Act
        var result = swap.Apply(csproj, "src/App.Core/App.Core.csproj");

        //Assert
        result.Should().NotContain("FallbackFontFamilies");
    }
}
