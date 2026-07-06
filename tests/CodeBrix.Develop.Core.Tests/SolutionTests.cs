using System;
using System.IO;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class SolutionTests : IDisposable
{
    readonly string tempDirectory;

    public SolutionTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose() => Directory.Delete(tempDirectory, recursive: true);

    string WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    const string LibraryCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    const string ExeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void Load_parses_classic_sln_projects()
    {
        //Arrange
        WriteFile("src/Alpha/Alpha.csproj", LibraryCsproj);
        WriteFile("src/Beta/Beta.csproj", ExeCsproj);
        var slnPath = WriteFile("Sample.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Alpha", "src\Alpha\Alpha.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Beta", "src\Beta\Beta.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Global
            EndGlobal
            """);

        //Act
        var solution = Solution.Load(slnPath);

        //Assert
        solution.Name.Should().Be("Sample");
        solution.Projects.Count.Should().Be(2);
        solution.Projects[0].Name.Should().Be("Alpha");
        solution.Projects[1].Name.Should().Be("Beta");
    }

    [Fact]
    public void Load_parses_slnx_projects()
    {
        //Arrange
        WriteFile("src/Alpha/Alpha.csproj", LibraryCsproj);
        WriteFile("src/Beta/Beta.csproj", ExeCsproj);
        var slnxPath = WriteFile("Sample.slnx", """
            <Solution>
              <Folder Name="/Solution Items/">
                <File Path="README.md" />
              </Folder>
              <Project Path="src/Alpha/Alpha.csproj" />
              <Project Path="src/Beta/Beta.csproj" />
            </Solution>
            """);

        //Act
        var solution = Solution.Load(slnxPath);

        //Assert
        solution.Projects.Count.Should().Be(2);
        solution.StartupProject.Name.Should().Be("Beta");
    }

    [Fact]
    public void Load_wraps_a_single_csproj_in_an_implicit_solution()
    {
        //Arrange
        var csprojPath = WriteFile("src/Alpha/Alpha.csproj", LibraryCsproj);

        //Act
        var solution = Solution.Load(csprojPath);

        //Assert
        solution.Projects.Count.Should().Be(1);
        solution.Projects[0].Name.Should().Be("Alpha");
    }

    [Fact]
    public void Load_skips_missing_projects()
    {
        //Arrange
        WriteFile("src/Alpha/Alpha.csproj", LibraryCsproj);
        var slnxPath = WriteFile("Sample.slnx", """
            <Solution>
              <Project Path="src/Alpha/Alpha.csproj" />
              <Project Path="src/Gone/Gone.csproj" />
            </Solution>
            """);

        //Act
        var solution = Solution.Load(slnxPath);

        //Assert
        solution.Projects.Count.Should().Be(1);
    }

    [Fact]
    public void Load_throws_for_missing_solution_file()
    {
        //Act
        var act = () => Solution.Load(Path.Combine(tempDirectory, "absent.sln"));

        //Assert
        act.Should().Throw<FileNotFoundException>();
    }
}

public class DotNetProjectTests : IDisposable
{
    readonly string tempDirectory;

    public DotNetProjectTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose() => Directory.Delete(tempDirectory, recursive: true);

    [Fact]
    public void Load_reads_sdk_output_type_and_target_frameworks()
    {
        //Arrange
        var csprojPath = Path.Combine(tempDirectory, "App.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        //Act
        var project = DotNetProject.Load(csprojPath);

        //Assert
        project.Sdk.Should().Be("Microsoft.NET.Sdk");
        project.OutputType.Should().Be("Exe");
        project.IsExecutable.Should().BeTrue();
        project.TargetFrameworks.Count.Should().Be(2);
        project.TargetFrameworks[0].Should().Be("net10.0");
    }

    [Fact]
    public void Load_defaults_to_library_output_type()
    {
        //Arrange
        var csprojPath = Path.Combine(tempDirectory, "Lib.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        //Act
        var project = DotNetProject.Load(csprojPath);

        //Assert
        project.OutputType.Should().Be("Library");
        project.IsExecutable.Should().BeFalse();
    }

    [Fact]
    public void GetVisibleDirectories_excludes_bin_obj_and_hidden_folders()
    {
        //Arrange
        foreach (var name in new[] { "src", "bin", "obj", ".git", "docs" })
            Directory.CreateDirectory(Path.Combine(tempDirectory, name));

        //Act
        var visible = DotNetProject.GetVisibleDirectories(tempDirectory);

        //Assert
        string.Join(",", visible.ToPathStrings()).Should().Contain("docs").And.Contain("src");
        string.Join(",", visible.ToPathStrings()).Should().NotContain("bin").And.NotContain(".git");
    }

    [Fact]
    public void GetVisibleFiles_excludes_hidden_files()
    {
        //Arrange
        File.WriteAllText(Path.Combine(tempDirectory, "Program.cs"), "//");
        File.WriteAllText(Path.Combine(tempDirectory, ".hidden"), "//");

        //Act
        var visible = DotNetProject.GetVisibleFiles(tempDirectory);

        //Assert
        string.Join(",", visible.ToPathStrings()).Should().Contain("Program.cs").And.NotContain(".hidden");
    }
}
