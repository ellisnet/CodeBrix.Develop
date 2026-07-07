using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeBrix.Develop.Core.Tests;

/// <summary>
/// A tiny synthetic WinUI-shaped platform compiled in-memory, so the XAML
/// metadata index / validator / completion tests run without referencing
/// the real CodeBrix.Platform packages.
/// </summary>
static class XamlTestCompilation
{
    const string PlatformSource = """
        namespace Microsoft.UI.Xaml
        {
            public class DependencyObject { }

            public class UIElement : DependencyObject
            {
                public double Opacity { get; set; }
                public Visibility Visibility { get; set; }
            }

            public class FrameworkElement : UIElement
            {
                public double Width { get; set; }
                public double Height { get; set; }
                public string Name { get; set; }
                public object Tag { get; set; }
                public event System.EventHandler Loaded { add { } remove { } }
            }

            public enum Visibility { Visible, Collapsed }

            public class ActualSizeHolder : FrameworkElement
            {
                public double ActualWidth { get; }
            }
        }

        namespace Microsoft.UI.Xaml.Controls
        {
            public class Grid : Microsoft.UI.Xaml.FrameworkElement
            {
                public System.Collections.Generic.List<object> Children { get; } = new System.Collections.Generic.List<object>();
                public static void SetRow(Microsoft.UI.Xaml.FrameworkElement element, int value) { }
                public static int GetRow(Microsoft.UI.Xaml.FrameworkElement element) => 0;
                public static void SetColumn(Microsoft.UI.Xaml.FrameworkElement element, int value) { }
                public static int GetColumn(Microsoft.UI.Xaml.FrameworkElement element) => 0;
            }

            public class Button : Microsoft.UI.Xaml.FrameworkElement
            {
                public object Content { get; set; }
                public bool IsEnabled { get; set; }
                public event System.EventHandler Click { add { } remove { } }
            }

            public abstract class PanelBase : Microsoft.UI.Xaml.FrameworkElement { }
        }

        namespace MyApp.Controls
        {
            public class FancyControl : Microsoft.UI.Xaml.FrameworkElement
            {
                public string Fanciness { get; set; }
            }
        }
        """;

    /// <summary>The shared compilation (built once; Roslyn objects are immutable).</summary>
    public static readonly Compilation Instance = CSharpCompilation.Create(
        "XamlTests",
        new[] { CSharpSyntaxTree.ParseText(PlatformSource) },
        new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Collections").Location),
        },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
