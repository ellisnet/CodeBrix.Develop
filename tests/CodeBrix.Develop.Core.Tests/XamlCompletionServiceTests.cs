using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Core.Xaml;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class XamlCompletionServiceTests
{
    static readonly XamlMetadataIndex index = XamlMetadataIndex.Build(XamlTestCompilation.Instance);

    // Completion at the position of the "$$" marker, explicit invocation.
    static IReadOnlyList<CodeCompletionItem> CompleteAt(string source, IReadOnlyList<string> resourceKeys = null)
    {
        var offset = source.IndexOf("$$", StringComparison.Ordinal);
        (offset >= 0).Should().BeTrue();
        var text = source.Remove(offset, 2);
        return XamlCompletionService.GetCompletions(
            text, offset, index, XamlCompletionReason.Invocation, '\0', resourceKeys);
    }

    [Fact]
    public void Element_completion_offers_platform_controls()
    {
        //Act
        var items = CompleteAt("<Gri$$");

        //Assert
        items.Select(i => i.DisplayText).Should().Contain("Grid");
        items.Select(i => i.DisplayText).Should().Contain("Button");
    }

    [Fact]
    public void Element_completion_spans_the_partial_name()
    {
        //Act
        var items = CompleteAt("<Gri$$");

        //Assert
        var grid = items.First(i => i.DisplayText == "Grid");
        grid.ReplacementStart.Should().Be(1);
        grid.ReplacementLength.Should().Be(3);
    }

    [Fact]
    public void Element_completion_offers_property_elements_of_the_parent()
    {
        //Act
        var items = CompleteAt("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><$$");

        //Assert
        items.Select(i => i.DisplayText).Should().Contain("Grid.Children");
        items.Select(i => i.DisplayText).Should().Contain("Grid.Row");
    }

    [Fact]
    public void Element_completion_offers_clr_namespace_types_with_prefix()
    {
        //Act
        var items = CompleteAt("<Grid xmlns:local=\"using:MyApp.Controls\"><loc$$");

        //Assert
        items.Select(i => i.DisplayText).Should().Contain("local:FancyControl");
    }

    [Fact]
    public void Attribute_completion_offers_properties_events_attached_and_directives()
    {
        //Act
        var items = CompleteAt("<Button $$");
        var names = items.Select(i => i.DisplayText).ToList();

        //Assert
        names.Should().Contain("Content");
        names.Should().Contain("Width");
        names.Should().Contain("Click");
        names.Should().Contain("Grid.Row");
        names.Should().Contain("x:Name");
    }

    [Fact]
    public void Attribute_completion_inserts_the_value_quotes()
    {
        //Act
        var content = CompleteAt("<Button $$").First(i => i.DisplayText == "Content");

        //Assert
        content.InsertionText.Should().Be("Content=\"\"");
        content.CaretBack.Should().Be(1);
    }

    [Fact]
    public void Attribute_completion_skips_attributes_already_set()
        => CompleteAt("<Button Content=\"x\" $$").Select(i => i.DisplayText)
            .Should().NotContain("Content");

    [Fact]
    public void Attribute_value_completion_offers_enum_members()
    {
        //Act
        var items = CompleteAt("<Button Visibility=\"$$\"");
        var names = items.Select(i => i.DisplayText).ToList();

        //Assert
        names.Should().Contain("Visible");
        names.Should().Contain("Collapsed");
    }

    [Fact]
    public void Attribute_value_completion_offers_booleans()
    {
        //Act
        var names = CompleteAt("<Button IsEnabled=\"$$\"").Select(i => i.DisplayText).ToList();

        //Assert
        names.Should().Contain("True");
        names.Should().Contain("False");
    }

    [Fact]
    public void Attribute_value_completion_offers_markup_extensions_after_brace()
    {
        //Act
        var names = CompleteAt("<Button Content=\"{$$\"").Select(i => i.DisplayText).ToList();

        //Assert
        names.Should().Contain("StaticResource");
        names.Should().Contain("Binding");
        names.Should().Contain("x:Bind");
    }

    [Fact]
    public void Attribute_value_completion_offers_resource_keys()
    {
        //Act
        var items = CompleteAt("<Button Content=\"{StaticResource $$\"", new[] { "AccentBrush", "TitleStyle" });
        var names = items.Select(i => i.DisplayText).ToList();

        //Assert
        names.Should().Contain("AccentBrush");
        names.Should().Contain("TitleStyle");
    }

    [Fact]
    public void Attribute_value_completion_suggests_event_handler_names()
        => CompleteAt("<Button Click=\"$$\"").Select(i => i.DisplayText).Should().Contain("OnClick");

    [Fact]
    public void MayTriggerCompletion_accepts_xaml_trigger_characters()
    {
        XamlCompletionService.MayTriggerCompletion('<').Should().BeTrue();
        XamlCompletionService.MayTriggerCompletion('{').Should().BeTrue();
        XamlCompletionService.MayTriggerCompletion('a').Should().BeTrue();
        XamlCompletionService.MayTriggerCompletion('>').Should().BeFalse();
    }
}
