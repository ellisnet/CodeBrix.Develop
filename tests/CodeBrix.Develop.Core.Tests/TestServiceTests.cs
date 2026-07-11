using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.Projects;
using CodeBrix.Develop.Core.Testing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class TestServiceTests : IDisposable
{
    readonly string tempDirectory;

    public TestServiceTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose()
    {
        TestService.Clear();
        try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
    }

    DotNetProject MakeTestProject(bool mtpRunner, string name = "My.Tests")
    {
        var directory = Path.Combine(tempDirectory, name);
        Directory.CreateDirectory(directory);
        var runnerProperty = mtpRunner
            ? "\n    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>"
            : "";
        File.WriteAllText(Path.Combine(directory, $"{name}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>{runnerProperty}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """);
        return DotNetProject.Load(Path.Combine(directory, $"{name}.csproj"));
    }

    [Fact]
    public void IsTestProject_requires_the_xunit_v3_package()
    {
        //Arrange
        var testProject = MakeTestProject(mtpRunner: false);
        var directory = Path.Combine(tempDirectory, "Plain");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Plain.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var plainProject = DotNetProject.Load(Path.Combine(directory, "Plain.csproj"));

        //Assert
        testProject.IsTestProject.Should().BeTrue();
        plainProject.IsTestProject.Should().BeFalse();
    }

    [Fact]
    public void UsesMicrosoftTestingPlatformRunner_reads_the_project_property()
    {
        //Assert
        MakeTestProject(mtpRunner: true, name: "Mtp.Tests").UsesMicrosoftTestingPlatformRunner.Should().BeTrue();
        MakeTestProject(mtpRunner: false, name: "Native.Tests").UsesMicrosoftTestingPlatformRunner.Should().BeFalse();
    }

    [Fact]
    public void GetFilterArguments_speaks_the_native_dialect()
    {
        //Arrange
        var project = MakeTestProject(mtpRunner: false);
        var root = new TestNode(TestNodeKind.Project, project.Name, project.Name, project);
        var ns = new TestNode(TestNodeKind.Namespace, "My.Tests", "My.Tests", project);
        root.AddChild(ns);
        var cls = new TestNode(TestNodeKind.Class, "Fixture", "My.Tests.Fixture", project);
        ns.AddChild(cls);
        var method = new TestNode(TestNodeKind.Method, "works", "My.Tests.Fixture.works", project);
        cls.AddChild(method);

        //Act + Assert
        TestService.GetFilterArguments(root).Count.Should().Be(0);
        TestService.GetFilterArguments(ns).Should().Equal(
            new[] { "-namespace", "My.Tests", "-namespace", "My.Tests.*" });
        TestService.GetFilterArguments(cls).Should().Equal(new[] { "-class", "My.Tests.Fixture" });
        TestService.GetFilterArguments(method).Should().Equal(new[] { "-method", "My.Tests.Fixture.works" });
    }

    [Fact]
    public void GetFilterArguments_speaks_the_mtp_dialect()
    {
        //Arrange
        var project = MakeTestProject(mtpRunner: true, name: "Mtp2.Tests");
        var root = new TestNode(TestNodeKind.Project, project.Name, project.Name, project);
        var cls = new TestNode(TestNodeKind.Class, "Fixture", "Ns.Fixture", project);
        root.AddChild(cls);
        var method = new TestNode(TestNodeKind.Method, "works", "Ns.Fixture.works", project);
        cls.AddChild(method);

        //Act + Assert
        TestService.GetFilterArguments(cls).Should().Equal(new[] { "--filter-class", "Ns.Fixture" });
        TestService.GetFilterArguments(method).Should().Equal(new[] { "--filter-method", "Ns.Fixture.works" });
    }

    [Fact]
    public async Task RefreshAsync_builds_the_forest_and_resolves_tests_at_lines()
    {
        //Arrange
        var project = MakeTestProject(mtpRunner: true, name: "Scan.Tests");
        var sourceFile = Path.Combine(project.BaseDirectory, "FixtureTests.cs");
        File.WriteAllText(sourceFile, """
            using Xunit;

            namespace Scan.Tests;

            public class FixtureTests
            {
                [Fact]
                public void first() { }

                [Fact]
                public void second() { }
            }
            """);
        var solution = Solution.Load(project.FileName);

        //Act
        await TestService.RefreshAsync(solution);

        //Assert
        var root = TestService.Roots.Single();
        root.Kind.Should().Be(TestNodeKind.Project);
        root.Name.Should().Be("Scan.Tests");
        var ns = root.Children.Single();
        ns.Kind.Should().Be(TestNodeKind.Namespace);
        ns.Name.Should().Be("Scan.Tests");
        var cls = ns.Children.Single();
        cls.FullName.Should().Be("Scan.Tests.FixtureTests");
        cls.Children.Count.Should().Be(2);

        var tests = TestService.GetTestsInFile(new CodeBrix.Develop.Core.FilePath(sourceFile));
        tests.Count.Should().Be(2);
        tests[0].Name.Should().Be("first");

        // Caret inside second()'s body resolves to second; a line above
        // every test resolves to nothing.
        TestService.FindTestAtLine(new CodeBrix.Develop.Core.FilePath(sourceFile), 11).Name.Should().Be("second");
        TestService.FindTestAtLine(new CodeBrix.Develop.Core.FilePath(sourceFile), 1).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_preserves_results_for_unchanged_tests()
    {
        //Arrange
        var project = MakeTestProject(mtpRunner: true, name: "Keep.Tests");
        File.WriteAllText(Path.Combine(project.BaseDirectory, "KeepTests.cs"), """
            using Xunit;
            namespace Keep.Tests;
            public class KeepTests
            {
                [Fact]
                public void stays() { }
            }
            """);
        var solution = Solution.Load(project.FileName);
        await TestService.RefreshAsync(solution);
        var method = TestService.Roots.Single().EnumerateMethods().Single();
        method.Status = TestStatus.Passed;
        method.LastResult = new TestRunResult { Status = TestStatus.Passed, DurationSeconds = 0.5 };

        //Act
        await TestService.RefreshAsync(solution);

        //Assert
        var rescanned = TestService.Roots.Single().EnumerateMethods().Single();
        rescanned.Status.Should().Be(TestStatus.Passed);
        rescanned.LastResult.Should().NotBeNull();
        rescanned.LastResult.DurationSeconds.Should().Be(0.5);
    }

    [Fact]
    public void SolutionHasTests_is_false_for_a_null_solution()
        => TestService.SolutionHasTests(null).Should().BeFalse();
}
