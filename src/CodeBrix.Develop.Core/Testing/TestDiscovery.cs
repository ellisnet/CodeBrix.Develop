//
// TestDiscovery.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the CodeBrix.Develop analogue of MonoDevelop.UnitTesting's
//      ITestProvider discovery, rebuilt as a fast Roslyn syntax scan)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core.Projects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeBrix.Develop.Core.Testing;

/// <summary>
/// Builds a project's test tree by syntactically scanning its C# sources
/// for [Fact]/[Theory] methods. Purely syntactic on purpose: it needs no
/// build and no loaded Roslyn workspace, so the Tests pad populates the
/// moment a solution opens.
/// </summary>
static class TestDiscovery
{
    /// <summary>
    /// Scans the given test project and returns its tree root, or null when
    /// the project contains no discoverable tests.
    /// </summary>
    public static TestNode ScanProject(DotNetProject project)
    {
        var methods = new List<DiscoveredMethod>();
        foreach (var file in EnumerateSourceFiles(project))
        {
            try
            {
                ScanFile(file, File.ReadAllText(file), methods);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"Test discovery could not scan {file}: {ex.Message}");
            }
        }
        return methods.Count == 0 ? null : BuildTree(project, methods);
    }

    static IEnumerable<FilePath> EnumerateSourceFiles(DotNetProject project)
    {
        foreach (var file in project.GetFiles())
        {
            if (file.HasExtension(".cs"))
                yield return file;
        }
        foreach (var linked in project.LinkedFiles)
        {
            if (linked.RealPath.HasExtension(".cs") && File.Exists(linked.RealPath))
                yield return linked.RealPath;
        }
    }

    // Also used directly by TestService to rescan a single (possibly
    // unsaved) buffer for the editor gutter markers.
    internal static void ScanFile(FilePath file, string text, List<DiscoveredMethod> methods)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var isTheory = false;
            var skipReason = "";
            var isTest = false;
            foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
            {
                var name = AttributeName(attribute);
                if (name != "Fact" && name != "Theory")
                    continue;
                isTest = true;
                isTheory = name == "Theory";
                skipReason = SkipReason(attribute);
                break;
            }
            if (!isTest)
                continue;

            var classChain = ClassChain(method);
            if (classChain.Count == 0)
                continue;
            var line = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1;
            methods.Add(new DiscoveredMethod
            {
                Namespace = NamespaceOf(method),
                ClassChain = classChain,
                MethodName = method.Identifier.Text,
                File = file,
                Line = line,
                IsTheory = isTheory,
                SkipReason = skipReason,
            });
        }
    }

    // The rightmost identifier of the attribute name, with any explicit
    // "Attribute" suffix stripped — matches [Fact], [Xunit.Fact], and
    // [FactAttribute] alike.
    static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name;
        while (name is QualifiedNameSyntax qualified)
            name = qualified.Right;
        var text = (name as IdentifierNameSyntax)?.Identifier.Text ?? (name as GenericNameSyntax)?.Identifier.Text ?? "";
        return text.EndsWith("Attribute", StringComparison.Ordinal) ? text[..^"Attribute".Length] : text;
    }

    static string SkipReason(AttributeSyntax attribute)
    {
        var arguments = attribute.ArgumentList?.Arguments;
        if (arguments == null)
            return "";
        foreach (var argument in arguments)
        {
            if (argument.NameEquals?.Name.Identifier.Text != "Skip")
                continue;
            if (argument.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                return literal.Token.ValueText;
            return "(skipped)";
        }
        return "";
    }

    // The enclosing type names, outermost first — nested classes join with
    // '+' in runner type names.
    static List<string> ClassChain(MethodDeclarationSyntax method)
    {
        var chain = new List<string>();
        for (SyntaxNode node = method.Parent; node != null; node = node.Parent)
        {
            if (node is TypeDeclarationSyntax type)
                chain.Insert(0, type.Identifier.Text);
        }
        return chain;
    }

    static string NamespaceOf(SyntaxNode node)
    {
        var parts = new List<string>();
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is BaseNamespaceDeclarationSyntax ns)
                parts.Insert(0, ns.Name.ToString());
        }
        return string.Join(".", parts);
    }

    static TestNode BuildTree(DotNetProject project, List<DiscoveredMethod> methods)
    {
        var root = new TestNode(TestNodeKind.Project, project.Name, project.Name, project);

        // Group namespace → class FQN → methods, everything alphabetical.
        foreach (var namespaceGroup in methods.GroupBy(m => m.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Every namespace becomes ONE dotted node ("A.B.C") — the
            // collapsed-chain presentation MonoDevelop used for single-child
            // namespace chains, applied unconditionally for a flatter tree.
            var parent = root;
            if (namespaceGroup.Key.Length > 0)
            {
                var namespaceNode = new TestNode(TestNodeKind.Namespace, namespaceGroup.Key, namespaceGroup.Key, project);
                root.AddChild(namespaceNode);
                parent = namespaceNode;
            }

            foreach (var classGroup in namespaceGroup.GroupBy(m => string.Join("+", m.ClassChain)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var classFullName = namespaceGroup.Key.Length > 0 ? $"{namespaceGroup.Key}.{classGroup.Key}" : classGroup.Key;
                var classNode = new TestNode(TestNodeKind.Class, classGroup.Key, classFullName, project);
                parent.AddChild(classNode);
                foreach (var method in classGroup.OrderBy(m => m.MethodName, StringComparer.Ordinal))
                {
                    var methodNode = new TestNode(TestNodeKind.Method, method.MethodName, $"{classFullName}.{method.MethodName}", project)
                    {
                        SourceFile = method.File,
                        SourceLine = method.Line,
                        IsTheory = method.IsTheory,
                        SkipReason = method.SkipReason,
                    };
                    classNode.AddChild(methodNode);
                }
            }
        }
        return root;
    }

    internal class DiscoveredMethod
    {
        public string Namespace = "";
        public List<string> ClassChain = new List<string>();
        public string MethodName = "";
        public FilePath File;
        public int Line;
        public bool IsTheory;
        public string SkipReason = "";
    }
}
