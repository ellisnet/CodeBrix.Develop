using System.Linq;
using CodeBrix.Develop.Core.Testing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class TestNodeTests
{
    static TestNode MakeTree(params TestStatus[] methodStatuses)
    {
        var root = new TestNode(TestNodeKind.Project, "Proj", "Proj", null);
        var ns = new TestNode(TestNodeKind.Namespace, "Ns", "Ns", null);
        root.AddChild(ns);
        var cls = new TestNode(TestNodeKind.Class, "Cls", "Ns.Cls", null);
        ns.AddChild(cls);
        for (var i = 0; i < methodStatuses.Length; i++)
        {
            var method = new TestNode(TestNodeKind.Method, $"m{i}", $"Ns.Cls.m{i}", null)
            {
                Status = methodStatuses[i],
            };
            cls.AddChild(method);
        }
        return root;
    }

    [Fact]
    public void Status_running_wins_over_everything()
        => MakeTree(TestStatus.Passed, TestStatus.Failed, TestStatus.Running).Status.Should().Be(TestStatus.Running);

    [Fact]
    public void Status_failed_wins_over_passed_and_skipped()
        => MakeTree(TestStatus.Passed, TestStatus.Skipped, TestStatus.Failed).Status.Should().Be(TestStatus.Failed);

    [Fact]
    public void Status_all_passed_is_passed()
        => MakeTree(TestStatus.Passed, TestStatus.Passed).Status.Should().Be(TestStatus.Passed);

    [Fact]
    public void Status_passed_with_skips_is_passed()
        => MakeTree(TestStatus.Passed, TestStatus.Skipped).Status.Should().Be(TestStatus.Passed);

    [Fact]
    public void Status_partial_run_is_mixed()
        => MakeTree(TestStatus.Passed, TestStatus.NotRun).Status.Should().Be(TestStatus.Mixed);

    [Fact]
    public void Status_all_skipped_is_skipped()
        => MakeTree(TestStatus.Skipped, TestStatus.Skipped).Status.Should().Be(TestStatus.Skipped);

    [Fact]
    public void Status_nothing_run_is_not_run()
        => MakeTree(TestStatus.NotRun, TestStatus.NotRun).Status.Should().Be(TestStatus.NotRun);

    [Fact]
    public void EnumerateMethods_returns_the_leaves()
    {
        //Arrange
        var root = MakeTree(TestStatus.NotRun, TestStatus.NotRun, TestStatus.NotRun);

        //Act
        var methods = root.EnumerateMethods().ToList();

        //Assert
        methods.Count.Should().Be(3);
        methods.All(m => m.Kind == TestNodeKind.Method).Should().BeTrue();
    }

    [Fact]
    public void ProjectRoot_walks_up_to_the_project_node()
    {
        //Arrange
        var root = MakeTree(TestStatus.NotRun);
        var method = root.EnumerateMethods().Single();

        //Act + Assert
        method.ProjectRoot.Should().BeSameAs(root);
        root.ProjectRoot.Should().BeSameAs(root);
    }

    [Fact]
    public void AddChild_sets_the_parent()
    {
        //Arrange
        var root = MakeTree(TestStatus.NotRun);

        //Assert
        var ns = root.Children.Single();
        ns.Parent.Should().BeSameAs(root);
        ns.Children.Single().Parent.Should().BeSameAs(ns);
    }
}
