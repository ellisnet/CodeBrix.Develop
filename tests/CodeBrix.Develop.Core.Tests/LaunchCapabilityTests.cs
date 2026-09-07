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
    public void A_dotnet_11_or_newer_android_project_is_blocked_only_by_the_missing_debugger(
        string targetFramework)
    {
        // The runtime is right (CoreCLR) and the diagnostics transport was
        // verified on device, but nothing drives it yet. Saying "not
        // implemented" is the honest reason; when it IS implemented this
        // expectation flips to CanDebug, in one place.
        var capability = LaunchCapability.Debugging(ProjectTargeting(targetFramework));

        capability.CanDebug.Should().BeFalse();
        capability.Reason.Should().Contain("not implemented");
        capability.Reason.Should().NotContain("MonoVM");
    }

    [Fact]
    public void The_two_android_refusals_give_different_reasons()
    {
        // The whole point of the split: one says "your runtime cannot be
        // debugged", the other "we have not written it yet".
        var ten = LaunchCapability.Debugging(ProjectTargeting("net10.0-android36.1")).Reason;
        var eleven = LaunchCapability.Debugging(ProjectTargeting("net11.0-android37.0")).Reason;

        ten.Should().NotBe(eleven);
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
