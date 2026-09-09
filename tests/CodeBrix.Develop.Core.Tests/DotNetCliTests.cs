using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

/// <summary>
/// Tests that change this process's environment or the IDE-wide SDK choice
/// run alone: a sibling test starting a dotnet meanwhile would see them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "process environment";
}

/// <summary>
/// The seam every child dotnet goes through. The MSBuild registration the
/// type system makes in THIS process must never reach a child, or a .NET 10
/// CLI ends up running a .NET 11 prerelease's MSBuild — which is exactly how a
/// net10.0-android project failed to build while a net11.0 one succeeded.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class DotNetCliTests : IDisposable
{
    readonly Dictionary<string, string> savedVariables = new Dictionary<string, string>();
    readonly Func<FilePath, DotNetSdkInstallation> savedChoice;
    readonly string tempDirectory;

    public DotNetCliTests()
    {
        foreach (var name in DotNetCli.MSBuildRegistrationVariables)
            savedVariables[name] = Environment.GetEnvironmentVariable(name);
        savedChoice = DotNetCli.SdkForTarget;
        tempDirectory = Path.Combine(Path.GetTempPath(),
            "codebrix-dotnetcli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose()
    {
        foreach (var pair in savedVariables)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        DotNetCli.SdkForTarget = savedChoice;
        Directory.Delete(tempDirectory, recursive: true);
    }

    static DotNetSdkInstallation Installation(string dotnetPath, string root)
        => new DotNetSdkInstallation(dotnetPath, root, new[] { new Version(11, 0, 100) });

    // What Microsoft.Build.Locator leaves behind once the type system has
    // registered a side-by-side SDK's MSBuild.
    void RegisterForeignMSBuild(string sdkDirectory)
    {
        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdkDirectory, "MSBuild.dll"));
        Environment.SetEnvironmentVariable("MSBuildExtensionsPath", sdkDirectory);
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdkDirectory, "Sdks"));
    }

    [Fact]
    public void The_registration_variables_are_the_three_the_locator_writes()
    {
        DotNetCli.MSBuildRegistrationVariables.Should().Equal(
            new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath" });
    }

    [Fact]
    public void ApplySdk_removes_this_process_MSBuild_registration_from_the_child()
    {
        RegisterForeignMSBuild("/somewhere/dotnet11/sdk/11.0.100-rc.1");
        var startInfo = new ProcessStartInfo("dotnet");
        // A fresh start info begins as a copy of this process's environment.
        startInfo.Environment.ContainsKey("MSBuildSDKsPath").Should().BeTrue();

        DotNetCli.ApplySdk(startInfo, null);

        foreach (var name in DotNetCli.MSBuildRegistrationVariables)
            startInfo.Environment.ContainsKey(name).Should().BeFalse();
    }

    [Fact]
    public void ApplySdk_removes_the_registration_even_when_an_sdk_is_applied()
    {
        RegisterForeignMSBuild("/somewhere/dotnet11/sdk/11.0.100-rc.1");
        var startInfo = new ProcessStartInfo("dotnet");

        DotNetCli.ApplySdk(startInfo, Installation("/opt/dotnet11/dotnet", "/opt/dotnet11"));

        foreach (var name in DotNetCli.MSBuildRegistrationVariables)
            startInfo.Environment.ContainsKey(name).Should().BeFalse();
    }

    [Fact]
    public void ApplySdk_with_no_sdk_keeps_dotnet_from_PATH_and_says_so()
    {
        var startInfo = new ProcessStartInfo("dotnet");

        var command = DotNetCli.ApplySdk(startInfo, null);

        command.Should().Be("dotnet");
        startInfo.FileName.Should().Be("dotnet");
    }

    [Fact]
    public void ApplySdk_runs_the_chosen_installation_from_its_own_root()
    {
        var startInfo = new ProcessStartInfo("dotnet");

        var command = DotNetCli.ApplySdk(startInfo, Installation("/opt/dotnet11/dotnet", "/opt/dotnet11"));

        command.Should().Be("/opt/dotnet11/dotnet");
        startInfo.FileName.Should().Be("/opt/dotnet11/dotnet");
        startInfo.Environment["DOTNET_ROOT"].Should().Be("/opt/dotnet11");
    }

    [Fact]
    public void ApplySdk_leaves_DOTNET_ROOT_alone_for_an_installation_without_a_root()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.Environment["DOTNET_ROOT"] = "/inherited";

        DotNetCli.ApplySdk(startInfo, Installation("dotnet", ""));

        startInfo.Environment["DOTNET_ROOT"].Should().Be("/inherited");
    }

    [Fact]
    public void ApplySdk_rejects_a_missing_start_info()
    {
        Assert.Throws<ArgumentNullException>(() => DotNetCli.ApplySdk(null, null));
    }

    [Fact]
    public void CreateStartInfo_asks_the_shared_choice_about_the_target()
    {
        FilePath asked = null;
        DotNetCli.SdkForTarget = target =>
        {
            asked = target;
            return Installation("/opt/dotnet11/dotnet", "/opt/dotnet11");
        };

        var startInfo = DotNetCli.CreateStartInfo(new FilePath("/work/App.csproj"), new FilePath("/work"));

        asked.FullPath.Should().Be("/work/App.csproj");
        startInfo.FileName.Should().Be("/opt/dotnet11/dotnet");
        startInfo.WorkingDirectory.Should().Be("/work");
        startInfo.Environment["DOTNET_ROOT"].Should().Be("/opt/dotnet11");
        startInfo.RedirectStandardOutput.Should().BeTrue();
        startInfo.RedirectStandardError.Should().BeTrue();
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.ArgumentList.Count.Should().Be(0);
    }

    [Fact]
    public void CreateStartInfo_without_a_choice_uses_dotnet_from_PATH()
    {
        DotNetCli.SdkForTarget = null;
        RegisterForeignMSBuild("/somewhere/dotnet11/sdk/11.0.100-rc.1");

        var startInfo = DotNetCli.CreateStartInfo(new FilePath("/work/App.csproj"), new FilePath("/work"));

        startInfo.FileName.Should().Be("dotnet");
        startInfo.Environment.ContainsKey("MSBUILD_EXE_PATH").Should().BeFalse();
    }

    [Fact]
    public void A_build_service_prefers_its_own_choice_over_the_shared_one()
    {
        DotNetCli.SdkForTarget = _ => Installation("/shared/dotnet", "/shared");
        var service = new BuildService { SdkForTarget = _ => Installation("/own/dotnet", "/own") };
        var startInfo = new ProcessStartInfo("dotnet");

        var command = service.ApplySdk(startInfo, new FilePath("/work/App.csproj"));

        command.Should().Be("/own/dotnet");
        startInfo.Environment["DOTNET_ROOT"].Should().Be("/own");
    }

    [Fact]
    public void A_build_service_without_its_own_choice_follows_the_shared_one()
    {
        // The test runner's builds, restore, and property evaluation all run
        // through services and projects that never had a choice of their
        // own; the shared one is what keeps them on the solution's SDK.
        DotNetCli.SdkForTarget = _ => Installation("/shared/dotnet", "/shared");
        var service = new BuildService();
        var startInfo = new ProcessStartInfo("dotnet");

        var command = service.ApplySdk(startInfo, new FilePath("/work/App.csproj"));

        command.Should().Be("/shared/dotnet");
        startInfo.Environment["DOTNET_ROOT"].Should().Be("/shared");
    }

    [Fact]
    public void A_build_service_with_no_choice_at_all_strips_the_registration_and_uses_PATH()
    {
        DotNetCli.SdkForTarget = null;
        RegisterForeignMSBuild("/somewhere/dotnet11/sdk/11.0.100-rc.1");
        var service = new BuildService();
        var startInfo = new ProcessStartInfo("dotnet");

        var command = service.ApplySdk(startInfo, new FilePath("/work/App.csproj"));

        command.Should().Be("dotnet");
        foreach (var name in DotNetCli.MSBuildRegistrationVariables)
            startInfo.Environment.ContainsKey(name).Should().BeFalse();
    }

    [Fact]
    public async Task A_project_evaluates_with_its_own_SDK_despite_a_foreign_MSBuild_registration()
    {
        // The real thing, end to end: this process carries a registration
        // that points at an SDK folder holding no Sdks at all. Handed on to
        // the child, "dotnet msbuild" cannot even find Microsoft.NET.Sdk
        // (MSB4236); kept out of it, the child's own SDK evaluates the
        // project as it always did.
        var foreignSdk = Path.Combine(tempDirectory, "foreign-sdk");
        Directory.CreateDirectory(Path.Combine(foreignSdk, "Sdks"));
        RegisterForeignMSBuild(foreignSdk);
        DotNetCli.SdkForTarget = null;
        var projectPath = Path.Combine(tempDirectory, "Probe.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        var project = DotNetProject.Load(projectPath);

        var properties = await project.EvaluatePropertiesAsync("Debug",
            TestContext.Current.CancellationToken, "TargetFramework");

        properties["TargetFramework"].Should().Be("net10.0");
    }
}
