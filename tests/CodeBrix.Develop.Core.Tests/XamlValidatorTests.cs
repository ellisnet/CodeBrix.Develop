using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Core.Xaml;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class XamlValidatorTests
{
    static readonly XamlMetadataIndex index = XamlMetadataIndex.Build(XamlTestCompilation.Instance);

    const string Header = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
              xmlns:local="using:MyApp.Controls"
        """;

    static IReadOnlyList<DiagnosticInfo> Validate(string body) =>
        XamlValidator.Validate(Header + body, index);

    [Fact]
    public void Validate_accepts_a_correct_document()
        => Validate(@">
              <Button Content=""Hello"" IsEnabled=""True"" Grid.Row=""1"" x:Name=""TheButton"" Click=""OnClick""/>
              <local:FancyControl Fanciness=""max""/>
            </Grid>").Should().BeEmpty();

    [Fact]
    public void Validate_reports_unknown_elements()
    {
        //Act
        var diagnostics = Validate("><Buttton/></Grid>");

        //Assert
        diagnostics.Count.Should().Be(1);
        diagnostics[0].Id.Should().Be("XAML0002");
        diagnostics[0].Message.Should().Contain("Buttton");
    }

    [Fact]
    public void Validate_reports_unknown_properties()
    {
        //Act
        var diagnostics = Validate("><Button Contnet=\"oops\"/></Grid>");

        //Assert
        diagnostics.Count.Should().Be(1);
        diagnostics[0].Id.Should().Be("XAML0003");
        diagnostics[0].Message.Should().Contain("Contnet");
    }

    [Fact]
    public void Validate_reports_undefined_prefixes()
    {
        //Act
        var diagnostics = Validate("><nope:Thing/></Grid>");

        //Assert
        diagnostics.Any(d => d.Id == "XAML0001").Should().BeTrue();
    }

    [Fact]
    public void Validate_reports_invalid_enum_values()
    {
        //Act
        var diagnostics = Validate("><Button Visibility=\"Visibel\"/></Grid>");

        //Assert
        diagnostics.Count.Should().Be(1);
        diagnostics[0].Id.Should().Be("XAML0006");
        diagnostics[0].Message.Should().Contain("Visibel");
    }

    [Fact]
    public void Validate_accepts_valid_enum_and_markup_extension_values()
        => Validate("><Button Visibility=\"Collapsed\" Content=\"{StaticResource Whatever}\"/></Grid>")
            .Should().BeEmpty();

    [Fact]
    public void Validate_reports_read_only_properties_set_as_attributes()
    {
        //Act
        var diagnostics = Validate("><ActualSizeHolder ActualWidth=\"10\"/></Grid>");

        //Assert
        diagnostics.Count.Should().Be(1);
        diagnostics[0].Id.Should().Be("XAML0005");
    }

    [Fact]
    public void Validate_accepts_property_elements_and_attached_ones()
        => Validate("><Grid.Children><Button/></Grid.Children></Grid>").Should().BeEmpty();

    [Fact]
    public void Validate_reports_unknown_property_elements()
    {
        //Act
        var diagnostics = Validate("><Grid.Kids/></Grid>");

        //Assert
        diagnostics.Count.Should().Be(1);
        diagnostics[0].Id.Should().Be("XAML0003");
    }

    [Fact]
    public void Validate_accepts_designer_attributes()
        => Validate(" d:CustomThing=\"1\" mc:Ignorable=\"d\"></Grid>").Should().BeEmpty();

    [Fact]
    public void Validate_reports_unknown_x_directives()
    {
        //Act
        var diagnostics = Validate("><Button x:Nmae=\"b\"/></Grid>");

        //Assert
        diagnostics.Count.Should().Be(1);
        diagnostics[0].Id.Should().Be("XAML0004");
    }

    [Fact]
    public void Validate_skips_semantic_checks_without_xmlns_declarations()
        => XamlValidator.Validate("<Buttton NotAThing=\"1\"/>", index, TestContext.Current.CancellationToken).Should().BeEmpty();

    [Fact]
    public void Validate_reports_xml_parse_errors()
        => (XamlValidator.Validate("<Grid xmlns=\"x\"><Button></Grid>", index, TestContext.Current.CancellationToken).Count > 0).Should().BeTrue();
}
