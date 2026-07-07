//
// XamlValidator.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (validates WinUI-flavor XAML against the project's metadata index,
//      using the adapted MonoDevelop.Xml error-tolerant parser)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Core.Xml.Analysis;
using CodeBrix.Develop.Core.Xml.Dom;
using CodeBrix.Develop.Core.Xml.Parser;
using Microsoft.CodeAnalysis;

namespace CodeBrix.Develop.Core.Xaml;

/// <summary>
/// Produces live diagnostics for a XAML document: XML well-formedness
/// problems from the parser plus unknown elements/properties and invalid
/// enum values resolved against the project's <see cref="XamlMetadataIndex"/>.
/// </summary>
public static class XamlValidator
{
    /// <summary>Validates the document text and returns its diagnostics.</summary>
    public static IReadOnlyList<DiagnosticInfo> Validate(string text, XamlMetadataIndex index, CancellationToken cancellationToken = default)
    {
        var results = new List<DiagnosticInfo>();
        var parser = new XmlTreeParser(new XmlRootState());
        var (document, parseDiagnostics) = parser.Parse(new System.IO.StringReader(text), cancellationToken);

        if (parseDiagnostics != null)
        {
            foreach (var diagnostic in parseDiagnostics)
            {
                results.Add(new DiagnosticInfo
                {
                    Id = diagnostic.Descriptor.Id,
                    Message = diagnostic.GetFormattedMessage(),
                    Severity = diagnostic.Descriptor.Severity switch
                    {
                        XmlDiagnosticSeverity.Error => DiagnosticInfoSeverity.Error,
                        XmlDiagnosticSeverity.Warning => DiagnosticInfoSeverity.Warning,
                        _ => DiagnosticInfoSeverity.Info,
                    },
                    Start = diagnostic.Span.Start,
                    Length = diagnostic.Span.Length,
                });
            }
        }

        if (document == null || index == null || !index.IsXamlPlatformAvailable)
            return results;

        var root = document.RootElement;
        if (root == null)
            return results;

        // A document that declares no namespaces at all is treated as a
        // fragment still being typed; semantic validation would be noise.
        if (!root.Attributes.Any(a => IsXmlnsDeclaration(a.Name)))
            return results;

        ValidateElement(root, EmptyScope, index, results, cancellationToken);
        return results;
    }

    static readonly Dictionary<string, string> EmptyScope = new Dictionary<string, string>(StringComparer.Ordinal);

    static bool IsXmlnsDeclaration(XName name) =>
        name.Prefix == "xmlns" || (!name.HasPrefix && name.Name == "xmlns");

    static Dictionary<string, string> CollectScope(XElement element, Dictionary<string, string> parentScope)
    {
        Dictionary<string, string> scope = null;
        foreach (var attribute in element.Attributes)
        {
            if (!IsXmlnsDeclaration(attribute.Name) || attribute.Value == null)
                continue;
            scope ??= new Dictionary<string, string>(parentScope, StringComparer.Ordinal);
            // xmlns="..." declares the default prefix (empty key).
            var prefix = attribute.Name.Prefix == "xmlns" ? attribute.Name.Name : "";
            scope[prefix] = attribute.Value;
        }
        return scope ?? parentScope;
    }

    static XamlNamespaceInfo ResolveNamespace(string prefix, Dictionary<string, string> scope)
    {
        if (!scope.TryGetValue(prefix ?? "", out var uri))
            return null;
        return XamlNamespaces.Resolve(uri);
    }

    static void ValidateElement(XElement element, Dictionary<string, string> parentScope, XamlMetadataIndex index, List<DiagnosticInfo> results, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!element.IsNamed)
            return;

        var scope = CollectScope(element, parentScope);
        var name = element.Name;
        var namespaceInfo = ResolveNamespace(name.Prefix, scope);
        INamedTypeSymbol elementType = null;
        var validateAttributes = false;

        if (namespaceInfo == null)
        {
            if (name.HasPrefix)
                AddError(results, element.NameSpan, "XAML0001",
                    $"The namespace prefix '{name.Prefix}' is not defined.");
            // No default xmlns in scope: leave the element unvalidated.
        }
        else if (namespaceInfo.Kind == XamlNamespaceKind.MarkupCompatibility
            || namespaceInfo.Kind == XamlNamespaceKind.Designer
            || namespaceInfo.Kind == XamlNamespaceKind.XamlLanguage
            || namespaceInfo.Kind == XamlNamespaceKind.Unknown)
        {
            // mc:/d: subtrees are design-time; x: elements (x:String, ...) and
            // unrecognized namespaces are accepted without member validation.
        }
        else
        {
            var dot = name.Name.IndexOf('.');
            if (dot > 0)
            {
                // Property element: <Grid.RowDefinitions> — the owner type and
                // the (regular or attached) property must both exist.
                var ownerName = name.Name.Substring(0, dot);
                var propertyName = name.Name.Substring(dot + 1);
                var ownerType = index.ResolveType(namespaceInfo, ownerName);
                if (ownerType == null)
                {
                    AddError(results, element.NameSpan, "XAML0002",
                        $"The type '{ownerName}' was not found.");
                }
                else if (index.FindMember(ownerType, propertyName) == null
                    && index.FindAttachedProperty(ownerType, propertyName) == null)
                {
                    AddError(results, element.NameSpan, "XAML0003",
                        $"The property '{propertyName}' was not found on type '{ownerName}'.");
                }
            }
            else
            {
                elementType = index.ResolveType(namespaceInfo, name.Name);
                if (elementType == null)
                    AddError(results, element.NameSpan, "XAML0002",
                        $"The type '{name.Name}' was not found.");
                else
                    validateAttributes = true;
            }
        }

        if (validateAttributes)
        {
            foreach (var attribute in element.Attributes)
                ValidateAttribute(attribute, elementType, scope, index, results);
        }

        foreach (var child in element.Elements)
            ValidateElement(child, scope, index, results, cancellationToken);
    }

    static void ValidateAttribute(XAttribute attribute, INamedTypeSymbol elementType, Dictionary<string, string> scope, XamlMetadataIndex index, List<DiagnosticInfo> results)
    {
        if (!attribute.IsNamed || IsXmlnsDeclaration(attribute.Name))
            return;
        var name = attribute.Name;

        if (name.HasPrefix)
        {
            var namespaceInfo = ResolveNamespace(name.Prefix, scope);
            if (namespaceInfo == null)
            {
                AddError(results, attribute.NameSpan, "XAML0001",
                    $"The namespace prefix '{name.Prefix}' is not defined.");
                return;
            }
            switch (namespaceInfo.Kind)
            {
                case XamlNamespaceKind.XamlLanguage:
                    if (!XamlNamespaces.AttributeDirectives.Contains(name.Name, StringComparer.Ordinal))
                        AddError(results, attribute.NameSpan, "XAML0004",
                            $"'{name.Prefix}:{name.Name}' is not a valid XAML directive.");
                    return;
                case XamlNamespaceKind.MarkupCompatibility:
                case XamlNamespaceKind.Designer:
                case XamlNamespaceKind.Unknown:
                    return;
                case XamlNamespaceKind.Presentation:
                case XamlNamespaceKind.Clr:
                    ValidateMemberAttribute(attribute, elementType, namespaceInfo, index, results);
                    return;
            }
            return;
        }

        ValidateMemberAttribute(attribute, elementType,
            new XamlNamespaceInfo { Kind = XamlNamespaceKind.Presentation }, index, results);
    }

    static void ValidateMemberAttribute(XAttribute attribute, INamedTypeSymbol elementType, XamlNamespaceInfo namespaceInfo, XamlMetadataIndex index, List<DiagnosticInfo> results)
    {
        var name = attribute.Name.Name;
        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            // Attached property usage: Grid.Row="1".
            var ownerName = name.Substring(0, dot);
            var propertyName = name.Substring(dot + 1);
            var ownerType = index.ResolveType(namespaceInfo, ownerName);
            if (ownerType == null)
            {
                AddError(results, attribute.NameSpan, "XAML0002",
                    $"The type '{ownerName}' was not found.");
                return;
            }
            var attached = index.FindAttachedProperty(ownerType, propertyName);
            if (attached == null)
            {
                // <Button Button.Content="..."> style also reaches regular members.
                if (index.FindMember(ownerType, propertyName) == null)
                    AddError(results, attribute.NameSpan, "XAML0003",
                        $"The property '{propertyName}' was not found on type '{ownerName}'.");
                return;
            }
            ValidateLiteralValue(attribute, attached.ValueType, results);
            return;
        }

        var member = index.FindMember(elementType, name);
        if (member == null)
        {
            AddError(results, attribute.NameSpan, "XAML0003",
                $"The property '{name}' was not found on type '{elementType.Name}'.");
            return;
        }
        if (member.IsEvent)
            return;
        if (!member.IsSettable)
        {
            AddError(results, attribute.NameSpan, "XAML0005",
                $"The property '{name}' on type '{elementType.Name}' is read-only and cannot be set from an attribute.");
            return;
        }
        ValidateLiteralValue(attribute, member.Type, results);
    }

    // Enum and bool literals are cheap to verify; everything else (markup
    // extensions, type-converted strings) is accepted.
    static void ValidateLiteralValue(XAttribute attribute, ITypeSymbol valueType, List<DiagnosticInfo> results)
    {
        var value = attribute.Value;
        if (string.IsNullOrEmpty(value) || value.IndexOf('{') >= 0 || valueType == null)
            return;

        if (valueType.TypeKind == TypeKind.Enum)
        {
            var members = new HashSet<string>(
                valueType.GetMembers().OfType<IFieldSymbol>().Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var part in value.Split(','))
            {
                var token = part.Trim();
                if (token.Length > 0 && !members.Contains(token))
                    AddError(results, ValueSpan(attribute), "XAML0006",
                        $"'{token}' is not a valid value for '{attribute.Name.Name}' (expected one of: {string.Join(", ", valueType.GetMembers().OfType<IFieldSymbol>().Select(f => f.Name))}).");
            }
        }
        else if (valueType.SpecialType == SpecialType.System_Boolean)
        {
            if (!value.Equals("True", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("False", StringComparison.OrdinalIgnoreCase))
                AddError(results, ValueSpan(attribute), "XAML0006",
                    $"'{value}' is not a valid value for '{attribute.Name.Name}' (expected True or False).");
        }
    }

    static TextSpan ValueSpan(XAttribute attribute) =>
        attribute.ValueOffset is int offset && attribute.Value != null
            ? new TextSpan(offset, attribute.Value.Length)
            : attribute.NameSpan;

    static void AddError(List<DiagnosticInfo> results, TextSpan span, string id, string message)
    {
        results.Add(new DiagnosticInfo
        {
            Id = id,
            Message = message,
            Severity = DiagnosticInfoSeverity.Error,
            Start = span.Start,
            Length = span.Length,
        });
    }
}
