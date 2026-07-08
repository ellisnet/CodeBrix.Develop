using System.IO;
using CodeBrix.Develop.Core.Xml.Parser;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

// Smoke tests for the adapted MonoDevelop.Xml parser (upstream carries its
// own extensive suite; these prove the port wiring).
public class XmlTreeParserTests
{
    [Fact]
    public void Parse_builds_the_element_tree()
    {
        //Act
        var (document, diagnostics) = new XmlTreeParser(new XmlRootState())
            .Parse(new StringReader("<a x=\"1\"><b/></a>"), TestContext.Current.CancellationToken);

        //Assert
        document.RootElement.Should().NotBeNull();
        document.RootElement.Name.Name.Should().Be("a");
        document.RootElement.Attributes.First.Value.Should().Be("1");
        document.RootElement.IsClosed.Should().BeTrue();
        (diagnostics == null || diagnostics.Count == 0).Should().BeTrue();
    }

    [Fact]
    public void Parse_reports_unclosed_elements()
    {
        //Act
        var (document, diagnostics) = new XmlTreeParser(new XmlRootState())
            .Parse(new StringReader("<a><b></a>"), TestContext.Current.CancellationToken);

        //Assert
        document.RootElement.Should().NotBeNull();
        (diagnostics.Count > 0).Should().BeTrue();
    }

    [Fact]
    public void Parse_tolerates_wildly_incomplete_markup()
    {
        //Act
        var (document, _) = new XmlTreeParser(new XmlRootState())
            .Parse(new StringReader("<Grid <Button Conte"), TestContext.Current.CancellationToken);

        //Assert
        document.Should().NotBeNull();
    }
}
