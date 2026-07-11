using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Testing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class TestDiscoveryTests
{
    static List<TestDiscovery.DiscoveredMethod> Scan(string source)
    {
        var methods = new List<TestDiscovery.DiscoveredMethod>();
        TestDiscovery.ScanFile(new FilePath("/x/Tests.cs"), source, methods);
        return methods;
    }

    [Fact]
    public void ScanFile_finds_facts_and_theories()
    {
        //Arrange
        const string source = """
            using Xunit;

            namespace My.Tests;

            public class BasicTests
            {
                [Fact]
                public void a_fact() { }

                [Theory]
                [InlineData(1)]
                public void a_theory(int value) { }

                public void not_a_test() { }
            }
            """;

        //Act
        var methods = Scan(source);

        //Assert
        methods.Count.Should().Be(2);
        var fact = methods.Single(m => m.MethodName == "a_fact");
        fact.Namespace.Should().Be("My.Tests");
        fact.ClassChain.Single().Should().Be("BasicTests");
        fact.IsTheory.Should().BeFalse();
        fact.Line.Should().Be(8);
        methods.Single(m => m.MethodName == "a_theory").IsTheory.Should().BeTrue();
    }

    [Fact]
    public void ScanFile_reads_the_skip_reason()
    {
        //Arrange
        const string source = """
            using Xunit;
            namespace N;
            public class C
            {
                [Fact(Skip = "flaky on CI")]
                public void skipped() { }
            }
            """;

        //Act + Assert
        Scan(source).Single().SkipReason.Should().Be("flaky on CI");
    }

    [Fact]
    public void ScanFile_accepts_qualified_and_suffixed_attribute_names()
    {
        //Arrange
        const string source = """
            namespace N;
            public class C
            {
                [Xunit.Fact]
                public void qualified() { }

                [FactAttribute]
                public void suffixed() { }
            }
            """;

        //Act + Assert
        Scan(source).Count.Should().Be(2);
    }

    [Fact]
    public void ScanFile_records_nested_class_chains()
    {
        //Arrange
        const string source = """
            using Xunit;
            namespace N;
            public class Outer
            {
                public class Inner
                {
                    [Fact]
                    public void nested() { }
                }
            }
            """;

        //Act
        var method = Scan(source).Single();

        //Assert
        string.Join("+", method.ClassChain).Should().Be("Outer+Inner");
    }

    [Fact]
    public void ScanFile_handles_block_namespaces()
    {
        //Arrange
        const string source = """
            using Xunit;
            namespace A.B
            {
                public class C
                {
                    [Fact]
                    public void inside() { }
                }
            }
            """;

        //Act + Assert
        Scan(source).Single().Namespace.Should().Be("A.B");
    }

    [Fact]
    public void ScanFile_ignores_other_attributes()
    {
        //Arrange
        const string source = """
            namespace N;
            public class C
            {
                [Obsolete]
                public void plain() { }
            }
            """;

        //Act + Assert
        Scan(source).Should().BeEmpty();
    }
}
