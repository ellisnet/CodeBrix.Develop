using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    public void Solution_with_the_platform_package_is_a_codebrix_platform_application()
    {
        //Arrange — the package id casing differs to prove the comparison is
        //case-insensitive, as NuGet ids are.
        WriteFile("App.Core/App.Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="codebrix.platform.apachelicenseforever" Version="1.0.186.1273" />
                <PackageReference Include="SkiaSharp.Skottie" Version="4.150.1" />
              </ItemGroup>
            </Project>
            """);
        var slnPath = WriteFile("App.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App.Core", "App.Core\App.Core.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);

        //Act
        var solution = Solution.Load(slnPath);

        //Assert
        solution.Projects[0].PackageReferences.Count.Should().Be(2);
        solution.Projects[0].PackageReferences[0].Id.Should().Be("codebrix.platform.apachelicenseforever");
        solution.Projects[0].PackageReferences[0].Version.Should().Be("1.0.186.1273");
        solution.Projects[0].HasPackageReference("CodeBrix.Platform.ApacheLicenseForever").Should().BeTrue();
        solution.IsCodeBrixPlatformApplication.Should().BeTrue();
    }

    [Fact]
    public void Solution_without_the_platform_package_is_not_a_codebrix_platform_application()
    {
        //Arrange
        WriteFile("Lib/Lib.csproj", LibraryCsproj);
        var slnPath = WriteFile("Plain.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);

        //Act
        var solution = Solution.Load(slnPath);

        //Assert
        solution.IsCodeBrixPlatformApplication.Should().BeFalse();
    }

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

    const string SharedShproj = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup Label="Globals">
            <ProjectGuid>d1e2f3a4-b5c6-4d7e-8f90-123456789abc</ProjectGuid>
          </PropertyGroup>
          <Import Project="Alpha.UI.projitems" Label="Shared" />
        </Project>
        """;

    [Fact]
    public void Load_includes_shproj_shared_projects_from_slnx()
    {
        //Arrange
        WriteFile("src/Alpha.UI/Alpha.UI.shproj", SharedShproj);
        WriteFile("src/Beta/Beta.csproj", ExeCsproj);
        var slnxPath = WriteFile("Sample.slnx", """
            <Solution>
              <Folder Name="/CodeBrixPlatform/">
                <Project Path="src/Alpha.UI/Alpha.UI.shproj" />
                <Project Path="src/Beta/Beta.csproj" />
              </Folder>
            </Solution>
            """);

        //Act
        var solution = Solution.Load(slnxPath);

        //Assert
        solution.Projects.Count.Should().Be(2);
        solution.Projects[0].Name.Should().Be("Alpha.UI");
        solution.Projects[0].IsSharedProject.Should().BeTrue();
        solution.Projects[1].IsSharedProject.Should().BeFalse();
    }

    [Fact]
    public void Load_includes_shproj_shared_projects_from_classic_sln()
    {
        //Arrange
        WriteFile("src/Alpha.UI/Alpha.UI.shproj", SharedShproj);
        var slnPath = WriteFile("Sample.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{D954291E-2A0B-460D-934E-DC6B0785DB48}") = "Alpha.UI", "src\Alpha.UI\Alpha.UI.shproj", "{13903639-2FEE-43D3-A920-4FB073E6702B}"
            EndProject
            Global
            EndGlobal
            """);

        //Act
        var solution = Solution.Load(slnPath);

        //Assert
        solution.Projects.Count.Should().Be(1);
        solution.Projects[0].IsSharedProject.Should().BeTrue();
    }

    [Fact]
    public void Load_reads_slnx_solution_folders()
    {
        //Arrange
        WriteFile("src/Alpha/Alpha.csproj", LibraryCsproj);
        WriteFile("src/libs/Beta/Beta.csproj", LibraryCsproj);
        WriteFile("tests/libs/Beta.Tests/Beta.Tests.csproj", LibraryCsproj);
        var slnxPath = WriteFile("Sample.slnx", """
            <Solution>
              <Project Path="src/Alpha/Alpha.csproj" />
              <Folder Name="/Libraries/">
                <Project Path="src/libs/Beta/Beta.csproj" />
              </Folder>
              <Folder Name="/Tests/">
                <Project Path="tests/libs/Beta.Tests/Beta.Tests.csproj" />
              </Folder>
            </Solution>
            """);

        //Act
        var solution = Solution.Load(slnxPath);

        //Assert
        solution.Projects[0].SolutionFolder.Should().Be("");
        solution.Projects[1].SolutionFolder.Should().Be("Libraries");
        solution.Projects[2].SolutionFolder.Should().Be("Tests");
        solution.SolutionFolderNames.Should().Equal(new[] { "Libraries", "Tests" });
    }

    [Fact]
    public void Shared_projects_are_never_the_startup_project()
    {
        //Arrange — the shared project listed first, an executable second.
        WriteFile("src/Alpha.UI/Alpha.UI.shproj", SharedShproj);
        WriteFile("src/Beta/Beta.csproj", ExeCsproj);
        var slnxPath = WriteFile("Sample.slnx", """
            <Solution>
              <Project Path="src/Alpha.UI/Alpha.UI.shproj" />
              <Project Path="src/Beta/Beta.csproj" />
            </Solution>
            """);

        //Act
        var solution = Solution.Load(slnxPath);

        //Assert
        solution.StartupProject.Name.Should().Be("Beta");
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
    public async Task A_single_property_evaluation_reads_msbuilds_raw_output()
    {
        //Arrange — MSBuild prints a single -getProperty as a RAW value, no
        //JSON envelope; the JSON only appears from two properties up. This is
        //the regression test for the frame-buffer swap generator's
        //CustomAfterMicrosoftCommonTargets probe, which asks for exactly one.
        var project = WriteProbeProject();

        //Act
        var properties = await project.EvaluatePropertiesAsync(
            "Debug", TestContext.Current.CancellationToken, "CodeBrixProbeValue");

        //Assert
        properties["CodeBrixProbeValue"].Should().Be("from-the-project");
    }

    [Fact]
    public async Task A_multi_property_evaluation_reads_msbuilds_json_output()
    {
        //Arrange
        var project = WriteProbeProject();

        //Act
        var properties = await project.EvaluatePropertiesAsync(
            "Debug", TestContext.Current.CancellationToken, "CodeBrixProbeValue", "Configuration");

        //Assert
        properties["CodeBrixProbeValue"].Should().Be("from-the-project");
        properties["Configuration"].Should().Be("Debug");
    }

    DotNetProject WriteProbeProject()
    {
        var csprojPath = Path.Combine(tempDirectory, "Probe.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <CodeBrixProbeValue>from-the-project</CodeBrixProbeValue>
              </PropertyGroup>
            </Project>
            """);
        return DotNetProject.Load(csprojPath);
    }

    [Fact]
    public void Load_reads_linked_files_and_declared_folders()
    {
        //Arrange — Link both as attribute and as child element, plus a
        //declared (possibly virtual) folder.
        var projectDirectory = Path.Combine(tempDirectory, "App");
        Directory.CreateDirectory(projectDirectory);
        var csprojPath = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\Shared\ViewModels\MainViewModel.cs" Link="ViewModels\MainViewModel.cs" />
                <EmbeddedResource Include="..\Shared\Assets\star.svg">
                  <Link>Assets\star.svg</Link>
                </EmbeddedResource>
                <Folder Include="ViewModels\" />
              </ItemGroup>
            </Project>
            """);

        //Act
        var project = DotNetProject.Load(csprojPath);

        //Assert
        project.LinkedFiles.Count.Should().Be(2);
        ((string) project.LinkedFiles[0].RealPath).Should().Be(Path.Combine(tempDirectory, "Shared", "ViewModels", "MainViewModel.cs"));
        project.LinkedFiles[0].LinkPath.Should().Be(Path.Combine("ViewModels", "MainViewModel.cs"));
        project.LinkedFiles[1].LinkPath.Should().Be(Path.Combine("Assets", "star.svg"));
        project.DeclaredFolders.Count.Should().Be(1);
        project.DeclaredFolders[0].Should().Be("ViewModels");
    }

    [Fact]
    public void Linked_files_and_virtual_folders_resolve_per_directory()
    {
        //Arrange
        var projectDirectory = Path.Combine(tempDirectory, "App");
        Directory.CreateDirectory(projectDirectory);
        var csprojPath = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\Shared\ViewModels\MainViewModel.cs" Link="ViewModels\MainViewModel.cs" />
                <Compile Include="..\Shared\Helpers\Deep\HostHelper.cs" Link="Helpers\Deep\HostHelper.cs" />
                <Compile Include="..\Shared\GlobalUsings.cs" Link="GlobalUsings.cs" />
                <Folder Include="Assets\" />
              </ItemGroup>
            </Project>
            """);
        var project = DotNetProject.Load(csprojPath);

        //Act
        var rootFolders = new List<string>(project.GetVirtualFolderNamesIn(""));
        var helpersFolders = new List<string>(project.GetVirtualFolderNamesIn("Helpers"));
        var rootFiles = new List<LinkedProjectFile>(project.GetLinkedFilesIn(""));
        var viewModelFiles = new List<LinkedProjectFile>(project.GetLinkedFilesIn("ViewModels"));

        //Assert — nothing exists on disk, so every implied folder is virtual.
        string.Join(",", rootFolders).Should().Be("Assets,Helpers,ViewModels");
        string.Join(",", helpersFolders).Should().Be("Deep");
        rootFiles.Count.Should().Be(1);
        rootFiles[0].LinkPath.Should().Be("GlobalUsings.cs");
        viewModelFiles.Count.Should().Be(1);
        Path.GetFileName(viewModelFiles[0].LinkPath).Should().Be("MainViewModel.cs");
    }

    [Fact]
    public void Virtual_folders_exclude_directories_existing_on_disk()
    {
        //Arrange — the ViewModels folder really exists, Assets does not.
        var projectDirectory = Path.Combine(tempDirectory, "App");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "ViewModels"));
        var csprojPath = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\Shared\MainViewModel.cs" Link="ViewModels\MainViewModel.cs" />
                <Folder Include="Assets\" />
              </ItemGroup>
            </Project>
            """);
        var project = DotNetProject.Load(csprojPath);

        //Act
        var rootFolders = new List<string>(project.GetVirtualFolderNamesIn(""));

        //Assert
        string.Join(",", rootFolders).Should().Be("Assets");
    }

    [Fact]
    public async Task GetOutputExecutableAsync_resolves_the_default_layout()
    {
        //Arrange
        var csprojPath = Path.Combine(tempDirectory, "App.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var project = DotNetProject.Load(csprojPath);

        //Act — nothing is built, so the resolved path is the evaluated
        //TargetPath (the managed assembly) rather than the apphost.
        var executable = await project.GetOutputExecutableAsync(cancellationToken: TestContext.Current.CancellationToken);

        //Assert
        ((string) executable).Should().Contain(Path.Combine(tempDirectory, "bin", "Debug", "net10.0", "App.dll"));
    }

    [Fact]
    public async Task GetOutputExecutableAsync_honors_customized_output_layout()
    {
        //Arrange — the Pinta pattern: pooled output folder, no framework
        //segment, and a renamed assembly.
        var projectDirectory = Path.Combine(tempDirectory, "src");
        Directory.CreateDirectory(projectDirectory);
        var csprojPath = Path.Combine(projectDirectory, "Painty.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <OutputPath>..\build\bin</OutputPath>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
                <AssemblyName>PaintyApp</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        var project = DotNetProject.Load(csprojPath);

        //Act
        var executable = await project.GetOutputExecutableAsync(cancellationToken: TestContext.Current.CancellationToken);

        //Assert
        ((string) executable).Should().Contain(Path.Combine(tempDirectory, "build", "bin", "PaintyApp.dll"));
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
