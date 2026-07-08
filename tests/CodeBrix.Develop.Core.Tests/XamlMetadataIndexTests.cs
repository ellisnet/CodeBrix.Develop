using System.Linq;
using CodeBrix.Develop.Core.Xaml;
using Microsoft.CodeAnalysis;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class XamlMetadataIndexTests
{
    static readonly XamlMetadataIndex index = XamlMetadataIndex.Build(XamlTestCompilation.Instance);

    static readonly XamlNamespaceInfo presentation = new XamlNamespaceInfo
    {
        Kind = XamlNamespaceKind.Presentation,
    };

    [Fact]
    public void Build_finds_the_xaml_platform()
        => index.IsXamlPlatformAvailable.Should().BeTrue();

    [Fact]
    public void ElementNames_contain_instantiable_controls()
    {
        index.ElementNames.Should().Contain("Grid");
        index.ElementNames.Should().Contain("Button");
    }

    [Fact]
    public void ElementNames_exclude_abstract_types()
        => index.ElementNames.Should().NotContain("PanelBase");

    [Fact]
    public void ResolveType_resolves_presentation_namespace_names()
        => index.ResolveType(presentation, "Button").Should().NotBeNull();

    [Fact]
    public void ResolveType_resolves_clr_namespaces()
    {
        //Arrange
        var clr = new XamlNamespaceInfo { Kind = XamlNamespaceKind.Clr, ClrNamespace = "MyApp.Controls" };

        //Act & Assert
        index.ResolveType(clr, "FancyControl").Should().NotBeNull();
        index.ResolveType(clr, "MissingControl").Should().BeNull();
    }

    [Fact]
    public void GetMembers_include_inherited_settable_properties_and_events()
    {
        //Act
        var button = index.ResolveType(presentation, "Button");
        var members = index.GetMembers(button);

        //Assert
        members.First(m => m.Name == "Content").IsSettable.Should().BeTrue();
        members.First(m => m.Name == "Width").IsSettable.Should().BeTrue();
        members.First(m => m.Name == "Click").IsEvent.Should().BeTrue();
        members.First(m => m.Name == "Loaded").IsEvent.Should().BeTrue();
    }

    [Fact]
    public void GetMembers_flag_read_only_collections()
    {
        //Act
        var grid = index.ResolveType(presentation, "Grid");
        var children = index.FindMember(grid, "Children");

        //Assert
        children.IsSettable.Should().BeFalse();
        children.IsCollection.Should().BeTrue();
    }

    [Fact]
    public void AttachedProperties_are_discovered_from_get_set_pairs()
    {
        index.AttachedProperties.Select(a => a.QualifiedName).Should().Contain("Grid.Row");
        index.AttachedProperties.Select(a => a.QualifiedName).Should().Contain("Grid.Column");
    }

    [Fact]
    public void FindAttachedProperty_resolves_by_owner_and_name()
    {
        //Act
        var grid = index.ResolveType(presentation, "Grid");

        //Assert
        index.FindAttachedProperty(grid, "Row").Should().NotBeNull();
        index.FindAttachedProperty(grid, "Rowboat").Should().BeNull();
    }

    [Fact]
    public void GetClrNamespaceElementNames_lists_local_controls()
        => index.GetClrNamespaceElementNames("MyApp.Controls").Should().Contain("FancyControl");

    // On Uno/CodeBrix.Platform Skia targets DependencyObject is an
    // INTERFACE implemented via source generation, not a base class.
    [Fact]
    public void Build_supports_interface_flavored_dependency_object()
    {
        //Arrange
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "UnoShaped",
            new[]
            {
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
                    namespace Microsoft.UI.Xaml
                    {
                        public interface DependencyObject { }
                        public class FrameworkElement : DependencyObject { public double Width { get; set; } }
                    }
                    namespace Microsoft.UI.Xaml.Controls
                    {
                        public class Grid : Microsoft.UI.Xaml.FrameworkElement { }
                    }
                    """, cancellationToken: TestContext.Current.CancellationToken),
            },
            new[]
            {
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            },
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        //Act
        var unoIndex = XamlMetadataIndex.Build(compilation);

        //Assert
        unoIndex.IsXamlPlatformAvailable.Should().BeTrue();
        unoIndex.ElementNames.Should().Contain("Grid");
        unoIndex.ElementNames.Should().Contain("FrameworkElement");
    }
}
