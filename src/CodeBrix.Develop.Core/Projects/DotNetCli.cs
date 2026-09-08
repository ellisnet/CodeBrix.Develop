//
// DotNetCli.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// Prepares every child "dotnet" process the IDE starts: the SDK that must
/// run it, and an environment the IDE's own MSBuild registration has not
/// reached into.
/// </summary>
/// <remarks>
/// THE ONE PLACE a child dotnet is prepared. Two facts make it necessary,
/// both measured rather than assumed:
/// <list type="bullet">
/// <item>The type system registers ONE MSBuild per process — the newest
/// installed, so that a .NET 11 solution can be loaded at all — and
/// Microsoft.Build.Locator does that by writing MSBUILD_EXE_PATH,
/// MSBuildExtensionsPath and MSBuildSDKsPath into the process environment.</item>
/// <item>Every child process inherits that environment, and the dotnet CLI
/// HONORS those variables over its own SDK. A .NET 10 CLI handed a .NET 11
/// preview's MSBuild loads that preview's SDK resolvers into a .NET 10
/// runtime and dies with "SDK Resolver Failure ... Could not load file or
/// assembly 'System.Runtime, Version=11.0.0.0'" — on a net10.0-android
/// project the .NET 10 SDK builds perfectly well on its own. Any ONE of the
/// three variables is enough to break the build; leaking only MSBuildSDKsPath
/// fails later, with MSB4216 from tasks of the wrong SDK.</item>
/// </list>
/// So the registration stays inside the IDE process, where it belongs, and
/// each child gets exactly the SDK chosen for its solution: the executable
/// to run and DOTNET_ROOT for its packs, nothing more. That is what makes a
/// net10.0 solution build with the system SDK and a net11.0 solution with the
/// side-by-side one, in the same IDE session, whichever was opened first.
/// </remarks>
public static class DotNetCli
{
    /// <summary>
    /// The variables Microsoft.Build.Locator writes into this process when it
    /// registers an SDK's MSBuild (measured against Locator 1.11.2, which
    /// sets exactly these three). A child dotnet must see none of them.
    /// </summary>
    public static readonly IReadOnlyList<string> MSBuildRegistrationVariables = new[]
    {
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
    };

    /// <summary>
    /// Chooses the SDK for a build or evaluation target, or returns null to
    /// use whatever "dotnet" resolves to on PATH. Set once by the IDE from the
    /// solution's highest .NET version and the user's SDK preferences; every
    /// service that starts a dotnet — build, run, restore, the test runner's
    /// builds, project-property evaluation — consults it.
    /// </summary>
    public static Func<FilePath, DotNetSdkInstallation> SdkForTarget { get; set; }

    /// <summary>
    /// Creates the start info for a dotnet invocation against
    /// <paramref name="target"/>, with the chosen SDK applied and output
    /// redirected. Arguments are the caller's to add.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(FilePath target, FilePath workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ApplySdk(startInfo, SdkForTarget?.Invoke(target));
        return startInfo;
    }

    /// <summary>
    /// Applies an SDK to a start info — the executable and DOTNET_ROOT — and
    /// removes this process's MSBuild registration from the child's
    /// environment. A null <paramref name="sdk"/> keeps "dotnet" from PATH;
    /// the removal happens regardless, because it is the removal, not the SDK
    /// choice, that keeps the child on its own MSBuild. Returns the command
    /// name for an echoed command line.
    /// </summary>
    public static string ApplySdk(ProcessStartInfo startInfo, DotNetSdkInstallation sdk)
    {
        if (startInfo == null)
            throw new ArgumentNullException(nameof(startInfo));

        foreach (var name in MSBuildRegistrationVariables)
            startInfo.Environment.Remove(name);

        if (sdk == null)
            return startInfo.FileName;

        startInfo.FileName = sdk.DotnetPath;
        if (sdk.Root.Length > 0)
            startInfo.Environment["DOTNET_ROOT"] = sdk.Root;
        return sdk.DotnetPath;
    }
}
