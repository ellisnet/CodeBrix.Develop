//
// XamlCompletionService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (WinUI-flavor XAML completion over the adapted MonoDevelop.Xml spine
//      parser and the project's XamlMetadataIndex)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Core.Xml.Dom;
using CodeBrix.Develop.Core.Xml.Editor.Completion;
using CodeBrix.Develop.Core.Xml.Parser;
using Microsoft.CodeAnalysis;

namespace CodeBrix.Develop.Core.Xaml;

/// <summary>Why XAML completion is being requested.</summary>
public enum XamlCompletionReason
{
    /// <summary>Explicit request (Ctrl+Space).</summary>
    Invocation,

    /// <summary>A character was just typed.</summary>
    TypedChar,
}

/// <summary>Computes XAML completion items at a caret position.</summary>
public static class XamlCompletionService
{
    static readonly string[] markupExtensions =
    {
        "Binding", "StaticResource", "TemplateBinding", "ThemeResource",
    };

    static readonly string[] xamlLanguageMarkupExtensions = { "Bind", "Null" };

    /// <summary>
    /// Computes completion items at the given UTF-16 <paramref name="offset"/>.
    /// Returns an empty list when the position does not support completion.
    /// Each item carries its own replacement span.
    /// </summary>
    public static IReadOnlyList<CodeCompletionItem> GetCompletions(
        string text, int offset, XamlMetadataIndex index, XamlCompletionReason reason,
        char typedChar, IReadOnlyList<string> resourceKeys = null,
        CancellationToken cancellationToken = default)
    {
        if (index == null || !index.IsXamlPlatformAvailable || offset < 0 || offset > text.Length)
            return Array.Empty<CodeCompletionItem>();

        var spine = new XmlSpineParser(new XmlRootState());
        for (var i = 0; i < offset; i++)
            spine.Push(text[i]);

        var xmlReason = reason == XamlCompletionReason.Invocation
            ? XmlTriggerReason.Invocation
            : XmlTriggerReason.TypedChar;
        var (kind, spanStart, spanLength) = XmlCompletionTriggering.GetTriggerAndSpan(
            spine, xmlReason, typedChar, new StringTextSource(text), cancellationToken: cancellationToken);

        var elements = spine.Spine.OfType<XElement>().ToList(); // innermost first
        var scope = CollectScope(elements);

        switch (kind)
        {
            case XmlCompletionTrigger.Tag:
                // The trigger span includes the '<'; keep it out of the replacement.
                return ElementItems(index, scope, ParentForNewElement(elements, isNamingElement: false),
                    spanStart + 1, Math.Max(0, spanLength - 1));

            case XmlCompletionTrigger.ElementName:
                return ElementItems(index, scope, ParentForNewElement(elements, isNamingElement: true),
                    spanStart, spanLength);

            case XmlCompletionTrigger.AttributeName:
                return AttributeItems(index, scope, elements.FirstOrDefault(), spanStart, spanLength);

            case XmlCompletionTrigger.AttributeValue:
                return AttributeValueItems(text, offset, index, scope, spine, elements.FirstOrDefault(),
                    spanStart, spanLength, resourceKeys);

            default:
                return Array.Empty<CodeCompletionItem>();
        }
    }

    /// <summary>
    /// Whether a typed character can possibly open XAML completion (a cheap
    /// pre-filter before running the spine parser).
    /// </summary>
    public static bool MayTriggerCompletion(char typedChar) =>
        typedChar == '<' || typedChar == ' ' || typedChar == '"' || typedChar == '\''
        || typedChar == '{' || typedChar == '.' || typedChar == ':' || typedChar == '_'
        || char.IsLetter(typedChar);

    // While naming an element the incomplete element itself is the innermost
    // spine entry; its parent is the enclosing element.
    static XElement ParentForNewElement(List<XElement> elements, bool isNamingElement) =>
        isNamingElement
            ? (elements.Count > 1 ? elements[1] : null)
            : elements.FirstOrDefault();

    static Dictionary<string, string> CollectScope(List<XElement> elements)
    {
        var scope = new Dictionary<string, string>(StringComparer.Ordinal);
        // Outermost first, so inner declarations win.
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            foreach (var attribute in elements[i].Attributes)
            {
                if (attribute.Value == null)
                    continue;
                if (attribute.Name.Prefix == "xmlns")
                    scope[attribute.Name.Name] = attribute.Value;
                else if (!attribute.Name.HasPrefix && attribute.Name.Name == "xmlns")
                    scope[""] = attribute.Value;
            }
        }
        return scope;
    }

    static string XamlLanguagePrefix(Dictionary<string, string> scope)
    {
        foreach (var pair in scope)
        {
            if (pair.Value == XamlNamespaces.XamlLanguageUri && pair.Key.Length > 0)
                return pair.Key;
        }
        return "x";
    }

    static IReadOnlyList<CodeCompletionItem> ElementItems(
        XamlMetadataIndex index, Dictionary<string, string> scope, XElement parent,
        int replacementStart, int replacementLength)
    {
        var items = new List<CodeCompletionItem>();

        foreach (var name in index.ElementNames)
            items.Add(Item(name, "Class", replacementStart, replacementLength));

        // Types from "using:" xmlns mappings, offered with their prefix.
        foreach (var pair in scope)
        {
            if (pair.Key.Length == 0)
                continue;
            var namespaceInfo = XamlNamespaces.Resolve(pair.Value);
            if (namespaceInfo.Kind != XamlNamespaceKind.Clr)
                continue;
            foreach (var name in index.GetClrNamespaceElementNames(namespaceInfo.ClrNamespace))
                items.Add(Item(pair.Key + ":" + name, "Class", replacementStart, replacementLength));
        }

        // Property elements of the enclosing element: <Grid.RowDefinitions>.
        if (parent != null && parent.IsNamed && parent.Name.Name.IndexOf('.') < 0)
        {
            var parentType = index.ResolveType(
                ResolveNamespace(parent.Name.Prefix, scope), parent.Name.Name);
            if (parentType != null)
            {
                foreach (var member in index.GetMembers(parentType))
                {
                    if (!member.IsEvent)
                        items.Add(Item(parent.Name.Name + "." + member.Name, "Property",
                            replacementStart, replacementLength));
                }
                foreach (var attached in index.GetAttachedProperties(parentType))
                    items.Add(Item(parent.Name.Name + "." + attached.Name, "Property",
                        replacementStart, replacementLength));
            }
        }

        return items;
    }

    static IReadOnlyList<CodeCompletionItem> AttributeItems(
        XamlMetadataIndex index, Dictionary<string, string> scope, XElement element,
        int replacementStart, int replacementLength)
    {
        var items = new List<CodeCompletionItem>();
        if (element == null || !element.IsNamed)
            return items;

        var alreadySet = new HashSet<string>(
            element.Attributes.Select(a => a.Name.FullName ?? ""), StringComparer.Ordinal);

        var elementType = index.ResolveType(
            ResolveNamespace(element.Name.Prefix, scope), element.Name.Name);
        if (elementType != null)
        {
            foreach (var member in index.GetMembers(elementType))
            {
                if (alreadySet.Contains(member.Name))
                    continue;
                if (member.IsEvent)
                    items.Add(AttributeItem(member.Name, "Event", replacementStart, replacementLength));
                else if (member.IsSettable)
                    items.Add(AttributeItem(member.Name, "Property", replacementStart, replacementLength));
            }
        }

        foreach (var attached in index.AttachedProperties)
        {
            if (!alreadySet.Contains(attached.QualifiedName))
                items.Add(AttributeItem(attached.QualifiedName, "Property", replacementStart, replacementLength));
        }

        var xamlPrefix = XamlLanguagePrefix(scope);
        foreach (var directive in XamlNamespaces.AttributeDirectives)
        {
            var full = xamlPrefix + ":" + directive;
            if (!alreadySet.Contains(full))
                items.Add(AttributeItem(full, "Keyword", replacementStart, replacementLength));
        }

        return items;
    }

    static IReadOnlyList<CodeCompletionItem> AttributeValueItems(
        string text, int offset, XamlMetadataIndex index, Dictionary<string, string> scope,
        XmlSpineParser spine, XElement element, int replacementStart, int replacementLength,
        IReadOnlyList<string> resourceKeys)
    {
        var items = new List<CodeCompletionItem>();
        var attribute = spine.Spine.OfType<XAttribute>().FirstOrDefault();
        if (attribute == null || !attribute.IsNamed || element == null || !element.IsNamed)
            return items;

        // Inside a markup extension the interesting token starts after the
        // last '{' or space, not at the attribute value start.
        var valueStart = Math.Min(replacementStart, offset);
        var typedValue = text.Substring(valueStart, offset - valueStart);
        if (typedValue.StartsWith("{", StringComparison.Ordinal))
            return MarkupExtensionItems(typedValue, valueStart, offset, scope, resourceKeys);

        var valueType = ResolveAttributeValueType(index, scope, element, attribute);
        if (valueType != null && valueType.TypeKind == TypeKind.Enum)
        {
            foreach (var field in valueType.GetMembers().OfType<IFieldSymbol>())
                items.Add(Item(field.Name, "EnumMember", replacementStart, replacementLength));
        }
        else if (valueType != null && valueType.SpecialType == SpecialType.System_Boolean)
        {
            items.Add(Item("True", "Keyword", replacementStart, replacementLength));
            items.Add(Item("False", "Keyword", replacementStart, replacementLength));
        }
        else if (IsEventAttribute(index, scope, element, attribute))
        {
            items.Add(Item("On" + attribute.Name.Name, "Method", replacementStart, replacementLength));
        }
        return items;
    }

    static IReadOnlyList<CodeCompletionItem> MarkupExtensionItems(
        string typedValue, int valueStart, int offset, Dictionary<string, string> scope,
        IReadOnlyList<string> resourceKeys)
    {
        var items = new List<CodeCompletionItem>();
        var content = typedValue.Substring(1);

        var lastSpace = content.LastIndexOf(' ');
        if (lastSpace < 0)
        {
            // Completing the extension name itself: "{Stat".
            var nameStart = valueStart + 1;
            var nameLength = offset - nameStart;
            foreach (var extension in markupExtensions)
                items.Add(Item(extension, "Class", nameStart, nameLength));
            var xamlPrefix = XamlLanguagePrefix(scope);
            foreach (var extension in xamlLanguageMarkupExtensions)
                items.Add(Item(xamlPrefix + ":" + extension, "Class", nameStart, nameLength));
            return items;
        }

        // Completing an argument: "{StaticResource MyKe".
        var extensionName = content.Substring(0, lastSpace).Trim();
        if ((extensionName == "StaticResource" || extensionName == "ThemeResource") && resourceKeys != null)
        {
            var keyStart = valueStart + 1 + lastSpace + 1;
            var keyLength = offset - keyStart;
            foreach (var key in resourceKeys)
                items.Add(Item(key, "Constant", keyStart, Math.Max(0, keyLength)));
        }
        return items;
    }

    static ITypeSymbol ResolveAttributeValueType(
        XamlMetadataIndex index, Dictionary<string, string> scope, XElement element, XAttribute attribute)
    {
        var name = attribute.Name;
        if (name.HasPrefix && name.Prefix != "xmlns")
        {
            var namespaceInfo = ResolveNamespace(name.Prefix, scope);
            if (namespaceInfo == null || namespaceInfo.Kind == XamlNamespaceKind.XamlLanguage)
                return null;
        }
        else if (name.HasPrefix)
        {
            return null;
        }

        var dot = name.Name.IndexOf('.');
        if (dot > 0)
        {
            var ownerType = index.ResolveType(
                new XamlNamespaceInfo { Kind = XamlNamespaceKind.Presentation },
                name.Name.Substring(0, dot));
            var attached = index.FindAttachedProperty(ownerType, name.Name.Substring(dot + 1));
            return attached?.ValueType;
        }

        var elementType = index.ResolveType(ResolveNamespace(element.Name.Prefix, scope), element.Name.Name);
        var member = index.FindMember(elementType, name.Name);
        return member != null && !member.IsEvent ? member.Type : null;
    }

    static bool IsEventAttribute(
        XamlMetadataIndex index, Dictionary<string, string> scope, XElement element, XAttribute attribute)
    {
        if (attribute.Name.HasPrefix)
            return false;
        var elementType = index.ResolveType(ResolveNamespace(element.Name.Prefix, scope), element.Name.Name);
        var member = index.FindMember(elementType, attribute.Name.Name);
        return member != null && member.IsEvent;
    }

    // With no default xmlns declared yet (a fragment still being typed),
    // fall back to the presentation namespace to keep completion useful.
    static XamlNamespaceInfo ResolveNamespace(string prefix, Dictionary<string, string> scope)
    {
        if (!scope.TryGetValue(prefix ?? "", out var uri))
            return string.IsNullOrEmpty(prefix)
                ? new XamlNamespaceInfo { Kind = XamlNamespaceKind.Presentation }
                : null;
        return XamlNamespaces.Resolve(uri);
    }

    static CodeCompletionItem Item(string name, string tag, int replacementStart, int replacementLength) =>
        new CodeCompletionItem
        {
            DisplayText = name,
            SortText = name,
            InsertionText = name,
            Tags = new[] { tag },
            ReplacementStart = replacementStart,
            ReplacementLength = replacementLength,
        };

    static CodeCompletionItem AttributeItem(string name, string tag, int replacementStart, int replacementLength)
    {
        var item = Item(name, tag, replacementStart, replacementLength);
        item.InsertionText = name + "=\"\"";
        item.CaretBack = 1;
        return item;
    }
}
