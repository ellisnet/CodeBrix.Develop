using System;
using System.Linq;
using CodeBrix.Develop.Core.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class SignatureHelpServiceTests
{
    // Compiles the source and computes signature help at the position of
    // the marker string (the marker itself is removed first).
    static SignatureHelpResult ComputeAt(string source, string marker)
    {
        var offset = source.IndexOf(marker, StringComparison.Ordinal);
        (offset >= 0).Should().BeTrue();
        var code = source.Remove(offset, marker.Length);
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "SignatureHelpTests",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return SignatureHelpService.Compute(compilation.GetSemanticModel(tree), offset);
    }

    const string TwoOverloads = """
        class C
        {
            void M(int first, string second) { }
            void M(int first) { }
            void Test() { M($$ }
        }
        """;

    [Fact]
    public void Compute_finds_all_overloads_inside_argument_list()
        => ComputeAt(TwoOverloads, "$$").Signatures.Count.Should().Be(2);

    [Fact]
    public void Compute_reports_first_parameter_active_after_open_paren()
        => ComputeAt(TwoOverloads, "$$").ActiveParameter.Should().Be(0);

    [Fact]
    public void Compute_reports_second_parameter_active_after_comma()
    {
        //Arrange
        var source = TwoOverloads.Replace("M($$", "M(1, $$");

        //Act & Assert
        ComputeAt(source, "$$").ActiveParameter.Should().Be(1);
    }

    [Fact]
    public void Compute_renders_parameter_types_and_names()
    {
        //Act
        var result = ComputeAt(TwoOverloads, "$$");

        //Assert
        var withTwo = result.Signatures.First(s => s.Parameters.Count == 2);
        withTwo.Parameters[0].Should().Be("int first");
        withTwo.Parameters[1].Should().Be("string second");
        withTwo.Prefix.Should().Contain("M(");
        withTwo.Suffix.Should().Be(")");
    }

    [Fact]
    public void Compute_returns_null_outside_any_argument_list()
        => ComputeAt("class C { void Test() { int x = 1$$; } }", "$$").Should().BeNull();

    [Fact]
    public void Compute_handles_object_creation()
    {
        //Act
        var result = ComputeAt("class C { C(int value) { } void Test() { var c = new C($$ } }", "$$");

        //Assert
        result.Should().NotBeNull();
        result.Signatures.Count.Should().Be(1);
        result.Signatures[0].Prefix.Should().Be("C(");
        result.Signatures[0].Parameters[0].Should().Be("int value");
    }

    [Fact]
    public void Compute_prefers_overload_with_room_for_active_parameter()
    {
        //Arrange
        var source = TwoOverloads.Replace("M($$", "M(1, $$");

        //Act
        var result = ComputeAt(source, "$$");

        //Assert
        result.Signatures[result.ActiveSignature].Parameters.Count.Should().Be(2);
    }
}
