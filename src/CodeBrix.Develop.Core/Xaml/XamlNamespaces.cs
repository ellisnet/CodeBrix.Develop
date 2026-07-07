//
// XamlNamespaces.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (WinUI-flavor XAML namespace rules per the [MS-XAML] specification
//      and the WinUI/Uno Platform conventions)
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Core.Xaml;

/// <summary>The kind of XAML namespace an xmlns URI maps to.</summary>
public enum XamlNamespaceKind
{
    /// <summary>The default WinUI presentation namespace (controls, panels, brushes, ...).</summary>
    Presentation,

    /// <summary>The XAML language namespace (x:Name, x:Key, x:Bind, ...).</summary>
    XamlLanguage,

    /// <summary>The markup-compatibility namespace (mc:Ignorable).</summary>
    MarkupCompatibility,

    /// <summary>The Blend designer namespace (d:DesignWidth, ...), ignored at run time.</summary>
    Designer,

    /// <summary>A CLR namespace mapping ("using:MyApp.Controls").</summary>
    Clr,

    /// <summary>An xmlns URI this IDE does not recognize.</summary>
    Unknown,
}

/// <summary>A resolved xmlns declaration.</summary>
public class XamlNamespaceInfo
{
    /// <summary>What the URI maps to.</summary>
    public XamlNamespaceKind Kind { get; set; }

    /// <summary>The CLR namespace, when <see cref="Kind"/> is <see cref="XamlNamespaceKind.Clr"/>.</summary>
    public string ClrNamespace { get; set; }
}

/// <summary>Resolves xmlns URIs to XAML namespace kinds (WinUI flavor).</summary>
public static class XamlNamespaces
{
    /// <summary>The default WinUI presentation xmlns URI.</summary>
    public const string PresentationUri = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The XAML language xmlns URI (conventionally prefixed "x").</summary>
    public const string XamlLanguageUri = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The markup-compatibility xmlns URI (conventionally "mc").</summary>
    public const string MarkupCompatibilityUri = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>The Blend designer xmlns URI (conventionally "d").</summary>
    public const string DesignerUri = "http://schemas.microsoft.com/expression/blend/2008";

    /// <summary>
    /// The XAML language attribute directives valid on elements
    /// (WinUI flavor; used to validate and complete "x:*" attributes).
    /// </summary>
    public static readonly string[] AttributeDirectives =
    {
        "Bind", "Class", "ClassModifier", "DataType", "DefaultBindMode",
        "DeferLoadStrategy", "FieldModifier", "Key", "Load", "Name",
        "Phase", "Uid",
    };

    /// <summary>Resolves an xmlns URI (or using:/clr-namespace: mapping).</summary>
    public static XamlNamespaceInfo Resolve(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.Unknown };
        if (uri == PresentationUri)
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.Presentation };
        if (uri == XamlLanguageUri)
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.XamlLanguage };
        if (uri == MarkupCompatibilityUri)
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.MarkupCompatibility };
        if (uri == DesignerUri)
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.Designer };
        if (uri.StartsWith("using:", StringComparison.Ordinal))
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.Clr, ClrNamespace = uri.Substring("using:".Length) };
        if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var clrNamespace = uri.Substring("clr-namespace:".Length);
            var separator = clrNamespace.IndexOf(';');
            if (separator >= 0)
                clrNamespace = clrNamespace.Substring(0, separator);
            return new XamlNamespaceInfo { Kind = XamlNamespaceKind.Clr, ClrNamespace = clrNamespace };
        }
        return new XamlNamespaceInfo { Kind = XamlNamespaceKind.Unknown };
    }
}
