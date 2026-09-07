using System;
using System.IO;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

/// <summary>
/// What <see cref="DotNetProject"/> has to know for an Android run to be
/// stoppable: that the project targets Android at all, and which application
/// id it installs as.
/// </summary>
public class AndroidProjectTests : IDisposable
{
    readonly string tempDirectory;

    public AndroidProjectTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(),
            "codebrix-android-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void An_android_project_is_recognized_and_yields_its_application_id()
    {
        var csproj = WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android36.1</TargetFramework>
                <OutputType>Exe</OutputType>
                <ApplicationId>com.codebrix.simpledebugapp</ApplicationId>
              </PropertyGroup>
            </Project>
            """);

        var project = DotNetProject.Load(csproj);

        project.IsAndroidProject.Should().BeTrue();
        project.ApplicationId.Should().Be("com.codebrix.simpledebugapp");
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net10.0-windows")]
    [InlineData("netstandard2.0")]
    public void A_non_android_project_is_not_mistaken_for_one(string targetFramework)
    {
        var csproj = WriteFile("Lib/Lib.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        DotNetProject.Load(csproj).IsAndroidProject.Should().BeFalse();
    }

    [Fact]
    public void A_multi_targeted_project_counts_as_android_when_any_framework_is()
    {
        var csproj = WriteFile("Multi/Multi.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net10.0-android36.1</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        DotNetProject.Load(csproj).IsAndroidProject.Should().BeTrue();
    }

    [Fact]
    public void The_application_id_falls_back_to_the_manifest_package()
    {
        // The older project shape: no ApplicationId, package on the manifest.
        WriteFile("Old/AndroidManifest.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <manifest xmlns:android="http://schemas.android.com/apk/res/android"
                      package="com.example.legacy">
            </manifest>
            """);
        var csproj = WriteFile("Old/Old.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android36.1</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        DotNetProject.Load(csproj).ApplicationId.Should().Be("com.example.legacy");
    }

    [Fact]
    public void The_project_property_wins_over_the_manifest_package()
    {
        WriteFile("Both/AndroidManifest.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <manifest package="com.example.stale"></manifest>
            """);
        var csproj = WriteFile("Both/Both.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android36.1</TargetFramework>
                <ApplicationId>com.example.current</ApplicationId>
              </PropertyGroup>
            </Project>
            """);

        DotNetProject.Load(csproj).ApplicationId.Should().Be("com.example.current");
    }

    [Fact]
    public void The_application_id_is_empty_when_nothing_declares_one()
    {
        var csproj = WriteFile("Bare/Bare.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android36.1</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        DotNetProject.Load(csproj).ApplicationId.Should().BeEmpty();
    }

    [Fact]
    public void A_malformed_manifest_does_not_stop_the_project_loading()
    {
        WriteFile("Broken/AndroidManifest.xml", "<manifest package=\"oops\"");
        var csproj = WriteFile("Broken/Broken.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android36.1</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var project = DotNetProject.Load(csproj);

        project.IsAndroidProject.Should().BeTrue();
        project.ApplicationId.Should().BeEmpty();
    }

    [Fact]
    public void The_real_sample_project_shape_resolves_both_facts()
    {
        // Mirrors CodeBrix.Android/samples/SimpleDebugApp, the project this
        // whole path was built against.
        var csproj = WriteFile("Sample/SimpleDebugApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android36.1</TargetFramework>
                <SupportedOSPlatformVersion>33</SupportedOSPlatformVersion>
                <OutputType>Exe</OutputType>
                <ApplicationId>com.codebrix.simpledebugapp</ApplicationId>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <EmbedAssembliesIntoApk>false</EmbedAssembliesIntoApk>
              </PropertyGroup>
            </Project>
            """);

        var project = DotNetProject.Load(csproj);

        project.IsAndroidProject.Should().BeTrue();
        project.ApplicationId.Should().Be("com.codebrix.simpledebugapp");
        project.IsExecutable.Should().BeTrue();
    }
}
