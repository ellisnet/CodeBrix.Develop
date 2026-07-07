//
// XamlMetadataIndex.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the XAML "vocabulary" is derived from assembly metadata via Roslyn,
//      the same way the Visual Studio XAML editor derives it — there is no
//      official schema document for WinUI-flavor XAML)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CodeBrix.Develop.Core.Xaml;

/// <summary>One XAML-settable member (property or event) of an element type.</summary>
public class XamlMemberInfo
{
    /// <summary>The member name.</summary>
    public string Name { get; set; }

    /// <summary>The property/event handler type.</summary>
    public ITypeSymbol Type { get; set; }

    /// <summary>Whether the member is an event.</summary>
    public bool IsEvent { get; set; }

    /// <summary>Whether the property has a public setter.</summary>
    public bool IsSettable { get; set; }

    /// <summary>Whether the property type is a collection (usable read-only via property elements).</summary>
    public bool IsCollection { get; set; }
}

/// <summary>One attached property ("Grid.Row") discovered from static Get/Set pairs.</summary>
public class XamlAttachedPropertyInfo
{
    /// <summary>The declaring type ("Grid").</summary>
    public INamedTypeSymbol Owner { get; set; }

    /// <summary>The property name ("Row").</summary>
    public string Name { get; set; }

    /// <summary>The property's value type.</summary>
    public ITypeSymbol ValueType { get; set; }

    /// <summary>"Owner.Name", as written in XAML attributes.</summary>
    public string QualifiedName => Owner.Name + "." + Name;
}

/// <summary>
/// The XAML vocabulary of one project: every element type, settable member,
/// and attached property visible to its compilation. Built once per project
/// and cached; all lookups are pure metadata operations.
/// </summary>
public sealed class XamlMetadataIndex
{
    readonly Compilation compilation;
    readonly Dictionary<string, INamedTypeSymbol> defaultNamespaceTypes;
    readonly List<string> elementNames;
    readonly List<XamlAttachedPropertyInfo> attachedProperties;
    readonly Dictionary<INamedTypeSymbol, List<XamlMemberInfo>> memberCache;
    readonly Dictionary<INamedTypeSymbol, List<XamlAttachedPropertyInfo>> attachedCache;
    readonly INamedTypeSymbol dependencyObjectType;
    readonly INamedTypeSymbol frameworkElementType;
    readonly INamedTypeSymbol enumerableType;

    /// <summary>
    /// Whether the compilation references a XAML platform (CodeBrix.Platform /
    /// WinUI types were found). When false, XAML validation and completion
    /// stay silent rather than reporting everything as unknown.
    /// </summary>
    public bool IsXamlPlatformAvailable => dependencyObjectType != null;

    /// <summary>Sorted names of all instantiable default-namespace element types.</summary>
    public IReadOnlyList<string> ElementNames => elementNames;

    /// <summary>All discovered attached properties of default-namespace types.</summary>
    public IReadOnlyList<XamlAttachedPropertyInfo> AttachedProperties => attachedProperties;

    XamlMetadataIndex(Compilation compilation)
    {
        this.compilation = compilation;
        defaultNamespaceTypes = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        elementNames = new List<string>();
        attachedProperties = new List<XamlAttachedPropertyInfo>();
        memberCache = new Dictionary<INamedTypeSymbol, List<XamlMemberInfo>>(SymbolEqualityComparer.Default);
        attachedCache = new Dictionary<INamedTypeSymbol, List<XamlAttachedPropertyInfo>>(SymbolEqualityComparer.Default);

        dependencyObjectType = compilation.GetTypeByMetadataName("Microsoft.UI.Xaml.DependencyObject")
            ?? compilation.GetTypeByMetadataName("Windows.UI.Xaml.DependencyObject");
        frameworkElementType = compilation.GetTypeByMetadataName("Microsoft.UI.Xaml.FrameworkElement")
            ?? compilation.GetTypeByMetadataName("Windows.UI.Xaml.FrameworkElement");
        enumerableType = compilation.GetTypeByMetadataName("System.Collections.IEnumerable");
    }

    /// <summary>Builds the index for the given compilation.</summary>
    public static XamlMetadataIndex Build(Compilation compilation)
    {
        var index = new XamlMetadataIndex(compilation);
        if (index.IsXamlPlatformAvailable)
            index.Populate();
        return index;
    }

    void Populate()
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.GlobalNamespace);
        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var child in ns.GetNamespaceMembers())
                stack.Push(child);

            var namespaceName = ns.ToDisplayString();
            if (!IsDefaultXamlNamespace(namespaceName))
                continue;

            foreach (var type in ns.GetTypeMembers())
            {
                if (type.DeclaredAccessibility != Accessibility.Public || type.TypeKind != TypeKind.Class)
                    continue;
                if (!DerivesFromDependencyObject(type))
                    continue;

                RegisterDefaultNamespaceType(type);
                CollectAttachedProperties(type, attachedProperties);
            }
        }

        foreach (var pair in defaultNamespaceTypes)
        {
            if (IsInstantiable(pair.Value))
                elementNames.Add(pair.Key);
        }
        elementNames.Sort(StringComparer.Ordinal);
        attachedProperties.Sort((a, b) => string.CompareOrdinal(a.QualifiedName, b.QualifiedName));
    }

    static bool IsDefaultXamlNamespace(string namespaceName) =>
        namespaceName == "Microsoft.UI.Xaml"
        || namespaceName.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal)
        || namespaceName == "Windows.UI.Xaml"
        || namespaceName.StartsWith("Windows.UI.Xaml.", StringComparison.Ordinal);

    void RegisterDefaultNamespaceType(INamedTypeSymbol type)
    {
        if (defaultNamespaceTypes.TryGetValue(type.Name, out var existing))
        {
            // Prefer Microsoft.UI.Xaml over legacy Windows.UI.Xaml duplicates.
            var existingIsMicrosoft = existing.ContainingNamespace.ToDisplayString()
                .StartsWith("Microsoft.", StringComparison.Ordinal);
            if (existingIsMicrosoft)
                return;
        }
        defaultNamespaceTypes[type.Name] = type;
    }

    bool DerivesFromDependencyObject(INamedTypeSymbol type)
    {
        if (SymbolEqualityComparer.Default.Equals(type, dependencyObjectType))
            return true;
        // On Uno/CodeBrix.Platform Skia targets DependencyObject is an
        // INTERFACE (implemented via source generation), not a base class.
        if (dependencyObjectType.TypeKind == TypeKind.Interface)
        {
            foreach (var implemented in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(implemented, dependencyObjectType))
                    return true;
            }
            return false;
        }
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, dependencyObjectType))
                return true;
        }
        return false;
    }

    static bool IsInstantiable(INamedTypeSymbol type) =>
        !type.IsAbstract
        && type.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);

    /// <summary>
    /// Resolves an element or attached-property owner name to its type, for
    /// the given namespace context. Returns null when unknown.
    /// </summary>
    public INamedTypeSymbol ResolveType(XamlNamespaceInfo namespaceInfo, string name)
    {
        if (namespaceInfo == null || string.IsNullOrEmpty(name))
            return null;
        switch (namespaceInfo.Kind)
        {
            case XamlNamespaceKind.Presentation:
                return defaultNamespaceTypes.TryGetValue(name, out var type) ? type : null;
            case XamlNamespaceKind.Clr:
                return compilation.GetTypeByMetadataName(namespaceInfo.ClrNamespace + "." + name);
            default:
                return null;
        }
    }

    /// <summary>All public instantiable class names of a CLR namespace ("using:" xmlns).</summary>
    public IReadOnlyList<string> GetClrNamespaceElementNames(string clrNamespace)
    {
        var results = new List<string>();
        var ns = FindNamespace(clrNamespace);
        if (ns == null)
            return results;
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.DeclaredAccessibility == Accessibility.Public
                && type.TypeKind == TypeKind.Class
                && IsInstantiable(type))
                results.Add(type.Name);
        }
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    INamespaceSymbol FindNamespace(string clrNamespace)
    {
        var current = (INamespaceSymbol) compilation.GlobalNamespace;
        foreach (var part in clrNamespace.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current == null)
                return null;
        }
        return current;
    }

    /// <summary>
    /// The XAML-relevant members (settable properties, collection
    /// properties, events) of the given element type, base types included.
    /// </summary>
    public IReadOnlyList<XamlMemberInfo> GetMembers(INamedTypeSymbol elementType)
    {
        if (elementType == null)
            return Array.Empty<XamlMemberInfo>();
        if (memberCache.TryGetValue(elementType, out var cached))
            return cached;

        var members = new List<XamlMemberInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = elementType; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (member is IPropertySymbol property && !property.IsIndexer)
                {
                    if (!seen.Add(property.Name))
                        continue;
                    members.Add(new XamlMemberInfo
                    {
                        Name = property.Name,
                        Type = property.Type,
                        IsSettable = property.SetMethod != null
                            && property.SetMethod.DeclaredAccessibility == Accessibility.Public,
                        IsCollection = IsCollectionType(property.Type),
                    });
                }
                else if (member is IEventSymbol eventSymbol)
                {
                    if (!seen.Add(eventSymbol.Name))
                        continue;
                    members.Add(new XamlMemberInfo
                    {
                        Name = eventSymbol.Name,
                        Type = eventSymbol.Type,
                        IsEvent = true,
                    });
                }
            }
        }
        members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        memberCache[elementType] = members;
        return members;
    }

    /// <summary>Finds one member by name on the element type (or null).</summary>
    public XamlMemberInfo FindMember(INamedTypeSymbol elementType, string name)
    {
        foreach (var member in GetMembers(elementType))
        {
            if (string.Equals(member.Name, name, StringComparison.Ordinal))
                return member;
        }
        return null;
    }

    /// <summary>The attached properties declared by the given type (base types included).</summary>
    public IReadOnlyList<XamlAttachedPropertyInfo> GetAttachedProperties(INamedTypeSymbol ownerType)
    {
        if (ownerType == null)
            return Array.Empty<XamlAttachedPropertyInfo>();
        if (attachedCache.TryGetValue(ownerType, out var cached))
            return cached;
        var results = new List<XamlAttachedPropertyInfo>();
        for (var current = ownerType; current != null; current = current.BaseType)
            CollectAttachedProperties(current, results);
        attachedCache[ownerType] = results;
        return results;
    }

    /// <summary>Finds one attached property by name on the owner type (or null).</summary>
    public XamlAttachedPropertyInfo FindAttachedProperty(INamedTypeSymbol ownerType, string name)
    {
        foreach (var attached in GetAttachedProperties(ownerType))
        {
            if (string.Equals(attached.Name, name, StringComparison.Ordinal))
                return attached;
        }
        return null;
    }

    // An attached property is a public static Set<Name>(target, value) with
    // a matching public static Get<Name>(target).
    static void CollectAttachedProperties(INamedTypeSymbol type, List<XamlAttachedPropertyInfo> results)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol setter
                || !setter.IsStatic
                || setter.DeclaredAccessibility != Accessibility.Public
                || setter.MethodKind != MethodKind.Ordinary
                || setter.Parameters.Length != 2
                || !setter.ReturnsVoid
                || !setter.Name.StartsWith("Set", StringComparison.Ordinal)
                || setter.Name.Length <= 3)
                continue;

            var name = setter.Name.Substring(3);
            var hasGetter = type.GetMembers("Get" + name).OfType<IMethodSymbol>().Any(g =>
                g.IsStatic
                && g.DeclaredAccessibility == Accessibility.Public
                && g.Parameters.Length == 1
                && !g.ReturnsVoid);
            if (!hasGetter)
                continue;
            if (results.Any(r => r.Name == name && SymbolEqualityComparer.Default.Equals(r.Owner, type)))
                continue;

            results.Add(new XamlAttachedPropertyInfo
            {
                Owner = type,
                Name = name,
                ValueType = setter.Parameters[1].Type,
            });
        }
    }

    bool IsCollectionType(ITypeSymbol type)
    {
        if (type == null || type.SpecialType == SpecialType.System_String || enumerableType == null)
            return false;
        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, enumerableType))
            || SymbolEqualityComparer.Default.Equals(type, enumerableType);
    }
}
