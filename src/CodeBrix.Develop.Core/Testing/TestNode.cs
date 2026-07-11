//
// TestNode.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.UnitTesting's UnitTest/UnitTestGroup model,
//      rebuilt UI-free for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using CodeBrix.Develop.Core.Projects;

namespace CodeBrix.Develop.Core.Testing;

/// <summary>What a node of the test tree represents.</summary>
public enum TestNodeKind
{
    /// <summary>A test project (the root of one project's test tree).</summary>
    Project,
    /// <summary>A namespace grouping (single-child namespace chains are collapsed into one dotted node).</summary>
    Namespace,
    /// <summary>A test class (fixture).</summary>
    Class,
    /// <summary>A test method ([Fact] or [Theory]; a theory's data rows aggregate into its method node).</summary>
    Method,
}

/// <summary>The run status shown for a test-tree node.</summary>
public enum TestStatus
{
    /// <summary>The test has not run yet (or its result was invalidated).</summary>
    NotRun,
    /// <summary>The test is part of the run currently in progress.</summary>
    Running,
    /// <summary>The test passed (for groups: every run child passed).</summary>
    Passed,
    /// <summary>The test failed (for groups: at least one child failed).</summary>
    Failed,
    /// <summary>The test was skipped/ignored.</summary>
    Skipped,
    /// <summary>Group only: children with results mixed with children not run.</summary>
    Mixed,
}

/// <summary>
/// One node of the test tree: a test project, namespace, class, or method.
/// The model is deliberately UI-free — pads subscribe to
/// <see cref="TestService"/> events and render from these nodes.
/// </summary>
public class TestNode
{
    readonly List<TestNode> children = new List<TestNode>();
    TestStatus status = TestStatus.NotRun;

    /// <summary>Creates a node; wire it into the tree via <see cref="AddChild"/>.</summary>
    public TestNode(TestNodeKind kind, string name, string fullName, DotNetProject project)
    {
        Kind = kind;
        Name = name;
        FullName = fullName;
        Project = project;
    }

    /// <summary>What the node represents.</summary>
    public TestNodeKind Kind { get; }

    /// <summary>The display name (e.g. the method name, or a dotted collapsed-namespace label).</summary>
    public string Name { get; }

    /// <summary>
    /// The fully qualified name the runner knows the node by: the dotted
    /// namespace, the namespace-qualified class (nested classes joined with
    /// '+'), or the class-qualified method name. The project root uses the
    /// project name.
    /// </summary>
    public string FullName { get; }

    /// <summary>The test project this node belongs to.</summary>
    public DotNetProject Project { get; }

    /// <summary>The parent node, or null on a project root.</summary>
    public TestNode Parent { get; private set; }

    /// <summary>The child nodes (empty for methods).</summary>
    public IReadOnlyList<TestNode> Children => children;

    /// <summary>The source file declaring the test method, or default for group nodes.</summary>
    public FilePath SourceFile { get; set; }

    /// <summary>The 1-based line of the test method declaration, or 0.</summary>
    public int SourceLine { get; set; }

    /// <summary>The Skip reason of a [Fact(Skip = "…")] method, or "".</summary>
    public string SkipReason { get; set; } = "";

    /// <summary>Whether the method is a [Theory] (its data rows aggregate into this node).</summary>
    public bool IsTheory { get; set; }

    /// <summary>The result of the node's most recent run, or null (methods only).</summary>
    public TestRunResult LastResult { get; internal set; }

    /// <summary>
    /// The node's run status: a method's own status, or the aggregate of a
    /// group's descendant methods (any running → Running; else any failed →
    /// Failed; complete-and-partial results mix into Mixed).
    /// </summary>
    public TestStatus Status
    {
        get
        {
            if (Kind == TestNodeKind.Method)
                return status;

            int notRun = 0, running = 0, passed = 0, failed = 0, skipped = 0;
            CountLeafStatuses(this, ref notRun, ref running, ref passed, ref failed, ref skipped);
            if (running > 0)
                return TestStatus.Running;
            if (failed > 0)
                return TestStatus.Failed;
            if (passed > 0)
                return notRun > 0 ? TestStatus.Mixed : TestStatus.Passed;
            if (skipped > 0)
                return notRun > 0 ? TestStatus.Mixed : TestStatus.Skipped;
            return TestStatus.NotRun;
        }
        internal set => status = value;
    }

    /// <summary>Appends a child node and sets its <see cref="Parent"/>.</summary>
    public void AddChild(TestNode child)
    {
        child.Parent = this;
        children.Add(child);
    }

    /// <summary>The project root this node belongs to (itself for project nodes).</summary>
    public TestNode ProjectRoot
    {
        get
        {
            var node = this;
            while (node.Parent != null)
                node = node.Parent;
            return node;
        }
    }

    /// <summary>Enumerates the method nodes of this subtree (itself when a method).</summary>
    public IEnumerable<TestNode> EnumerateMethods()
    {
        if (Kind == TestNodeKind.Method)
        {
            yield return this;
            yield break;
        }
        foreach (var child in children)
        {
            foreach (var method in child.EnumerateMethods())
                yield return method;
        }
    }

    static void CountLeafStatuses(TestNode node, ref int notRun, ref int running, ref int passed, ref int failed, ref int skipped)
    {
        if (node.Kind == TestNodeKind.Method)
        {
            switch (node.status)
            {
                case TestStatus.Running: running++; break;
                case TestStatus.Passed: passed++; break;
                case TestStatus.Failed: failed++; break;
                case TestStatus.Skipped: skipped++; break;
                default: notRun++; break;
            }
            return;
        }
        foreach (var child in node.children)
            CountLeafStatuses(child, ref notRun, ref running, ref passed, ref failed, ref skipped);
    }
}
