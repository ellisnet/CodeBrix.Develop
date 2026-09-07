using System;
using System.IO;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

/// <summary>
/// The rule that decides whether the Debug button is offered. Getting this
/// wrong either hides a working feature or promises one that does not exist.
/// </summary>
public class LaunchCapabilityTests : IDisposable
{
    readonly string tempDirectory;

    public LaunchCapabilityTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(),
            "codebrix-launchcap-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose() => Directory.Delete(tempDirectory, recursive: true);

    DotNetProject ProjectTargeting(string targetFramework)
    {
        var path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".csproj");
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        return DotNetProject.Load(path);
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net11.0")]
    [InlineData("net10.0-windows")]
    public void A_non_android_project_can_be_debugged(string targetFramework)
    {
        var capability = LaunchCapability.Debugging(ProjectTargeting(targetFramework));

        capability.CanDebug.Should().BeTrue();
        capability.Reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("net10.0-android36.1")]
    [InlineData("net10.0-android35.0")]
    [InlineData("net9.0-android35.0")]
    public void A_dotnet_10_or_older_android_project_cannot_be_debugged(string targetFramework)
    {
        // These run on MonoVM, whose soft-debugger protocol the IDE has no
        // client for — and is not getting one, since the runtime is retiring.
        var capability = LaunchCapability.Debugging(ProjectTargeting(targetFramework));

        capability.CanDebug.Should().BeFalse();
        capability.Reason.Should().Contain("MonoVM");
    }

    [Theory]
    [InlineData("net11.0-android37.0")]
    [InlineData("net12.0-android38.0")]
    public void A_dotnet_11_or_newer_android_project_can_be_debugged(string targetFramework)
    {
        // The app runs on CoreCLR, and the IDE places its own debugger in the
        // app's sandbox on the device and drives it over DAP through an adb
        // port forward. Nothing is left to explain.
        var capability = LaunchCapability.Debugging(ProjectTargeting(targetFramework));

        capability.CanDebug.Should().BeTrue();
        capability.Reason.Should().BeEmpty();
    }

    [Fact]
    public void The_android_runtime_version_is_the_whole_of_the_android_rule()
    {
        //Act — the same project shape either side of the CoreCLR boundary
        var ten = LaunchCapability.Debugging(ProjectTargeting("net10.0-android36.1"));
        var eleven = LaunchCapability.Debugging(ProjectTargeting("net11.0-android37.0"));

        //Assert — one refusal is left, and it is about the RUNTIME
        ten.CanDebug.Should().BeFalse();
        ten.Reason.Should().Contain("MonoVM");
        eleven.CanDebug.Should().BeTrue();
        eleven.Reason.Should().NotBe(ten.Reason);
    }

    [Fact]
    public void No_project_is_not_debuggable()
        => LaunchCapability.Debugging(null).CanDebug.Should().BeFalse();

    [Fact]
    public void An_android_project_with_an_unreadable_framework_is_refused_with_a_reason()
    {
        // "netstandard2.0-android" is nonsense, but the IDE must not crash or
        // silently offer a session it cannot start.
        var path = Path.Combine(tempDirectory, "odd.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>netstandard2.0-android</TargetFramework></PropertyGroup>
            </Project>
            """);

        var capability = LaunchCapability.Debugging(DotNetProject.Load(path));

        capability.CanDebug.Should().BeFalse();
        capability.Reason.Should().NotBeEmpty();
    }
}
