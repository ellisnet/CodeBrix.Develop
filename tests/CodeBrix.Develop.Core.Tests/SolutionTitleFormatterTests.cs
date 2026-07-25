using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class SolutionTitleFormatterTests
{
    [Theory]
    [InlineData("WikipediaPublisher", "Wikipedia Publisher")]
    [InlineData("Doom.Brix", "Doom Brix")]
    [InlineData("This.That", "This That")]
    [InlineData("CodeBrix.Develop", "Code Brix Develop")]
    [InlineData("Doom", "Doom")]
    public void WithSpaces_separates_title_case_words_and_dots(string name, string expected)
        => SolutionTitleFormatter.WithSpaces(name).Should().Be(expected);

    [Fact]
    public void WithSpaces_keeps_an_acronym_together()
        => SolutionTitleFormatter.WithSpaces("HTTPServer").Should().Be("HTTP Server");

    [Fact]
    public void WithSpaces_keeps_digits_with_the_word_they_follow()
        => SolutionTitleFormatter.WithSpaces("PricingTests01").Should().Be("Pricing Tests01");

    [Fact]
    public void WithSpaces_starts_a_word_after_a_digit()
        => SolutionTitleFormatter.WithSpaces("Test01Runner").Should().Be("Test01 Runner");

    [Fact]
    public void WithSpaces_leaves_other_separators_alone()
        => SolutionTitleFormatter.WithSpaces("Pricing-Tests_One").Should().Be("Pricing-Tests_One");

    [Fact]
    public void WithSpaces_does_not_double_up_existing_spacing()
        => SolutionTitleFormatter.WithSpaces("Doom. Brix").Should().Be("Doom Brix");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithSpaces_passes_a_blank_name_through(string name)
        => SolutionTitleFormatter.WithSpaces(name).Should().Be(name);
}
