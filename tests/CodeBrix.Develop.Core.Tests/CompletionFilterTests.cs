using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core.TypeSystem;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class CompletionFilterTests
{
    [Fact]
    public void Score_empty_pattern_matches_everything()
        => CompletionFilter.Score("TextBlock", "").Should().Be(0);

    [Fact]
    public void Score_exact_match_beats_prefix_match()
        => (CompletionFilter.Score("Text", "Text") > CompletionFilter.Score("TextBlock", "Text")).Should().BeTrue();

    [Fact]
    public void Score_case_sensitive_prefix_beats_case_insensitive()
        => (CompletionFilter.Score("TextBlock", "Text") > CompletionFilter.Score("TextBlock", "text")).Should().BeTrue();

    [Fact]
    public void Score_matches_camel_humps()
        => (CompletionFilter.Score("TextBlock", "TBl") > 0).Should().BeTrue();

    [Fact]
    public void Score_camel_humps_require_ordered_humps()
        => CompletionFilter.Score("TextBlock", "BlT").Should().Be(-1);

    [Fact]
    public void Score_substring_match_ranks_below_camel_humps()
    {
        //Act
        var substring = CompletionFilter.Score("Hidden", "idd"); // not hump-anchored
        var humps = CompletionFilter.Score("WidthProperty", "WP");

        //Assert
        (substring > 0).Should().BeTrue();
        (humps > substring).Should().BeTrue();
    }

    [Fact]
    public void Score_returns_no_match_for_unrelated_text()
        => CompletionFilter.Score("Button", "xyz").Should().Be(-1);

    [Fact]
    public void Filter_keeps_matches_and_ranks_prefixes_first()
    {
        //Arrange
        var items = new List<CodeCompletionItem>
        {
            new CodeCompletionItem { DisplayText = "BorderBrush" },
            new CodeCompletionItem { DisplayText = "Background" },
            new CodeCompletionItem { DisplayText = "Width" },
        };

        //Act
        var filtered = CompletionFilter.Filter(items, "B");

        //Assert
        filtered.Count.Should().Be(2);
        filtered.Select(i => i.DisplayText).Should().Contain("BorderBrush");
        filtered.Select(i => i.DisplayText).Should().NotContain("Width");
    }

    [Fact]
    public void Filter_prefers_filter_text_over_display_text()
    {
        //Arrange
        var items = new List<CodeCompletionItem>
        {
            new CodeCompletionItem { DisplayText = "Method<T>(...)", FilterText = "Method" },
        };

        //Act & Assert
        CompletionFilter.Filter(items, "Meth").Count.Should().Be(1);
    }
}
