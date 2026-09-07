//
// DotNetSdkLocator.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// One .NET SDK installation the IDE can build with.
/// </summary>
public sealed class DotNetSdkInstallation
{
    /// <summary>Creates an installation record.</summary>
    readonly HashSet<Version> prereleaseVersions;

    public DotNetSdkInstallation(string dotnetPath, string root, IReadOnlyList<Version> sdkVersions,
        bool isSystemInstallation = false, IReadOnlyDictionary<Version, string> sdkDirectories = null,
        IEnumerable<Version> prerelease = null)
    {
        DotnetPath = dotnetPath;
        Root = root;
        SdkVersions = sdkVersions ?? Array.Empty<Version>();
        IsSystemInstallation = isSystemInstallation;
        SdkDirectories = sdkDirectories ?? new Dictionary<Version, string>();
        prereleaseVersions = new HashSet<Version>(prerelease ?? Array.Empty<Version>());
    }

    /// <summary>
    /// Whether that SDK version is a PREVIEW ("11.0.100-preview.7.26381.103").
    /// Previews are used only when the user has allowed them, because
    /// registering a preview MSBuild affects every solution the IDE opens
    /// afterwards, stable ones included.
    /// </summary>
    public bool IsPrerelease(Version sdkVersion) => prereleaseVersions.Contains(sdkVersion);

    /// <summary>The versions here that are not previews, ascending.</summary>
    public IReadOnlyList<Version> StableSdkVersions
        => SdkVersions.Where(v => !prereleaseVersions.Contains(v)).ToList();

    /// <summary>The full path of the dotnet executable to invoke.</summary>
    public string DotnetPath { get; }

    /// <summary>
    /// The installation root — the folder holding "dotnet", "sdk" and "packs".
    /// Set as DOTNET_ROOT, which is what decides where TARGETING PACKS are
    /// resolved from, and therefore which target frameworks can be loaded at
    /// all. Two installations have disjoint pack sets, so this is the setting
    /// that matters, far more than which MSBuild is registered.
    /// </summary>
    public string Root { get; }

    /// <summary>Each SDK version's own directory, e.g. "/usr/share/dotnet/sdk/10.0.400".</summary>
    public IReadOnlyDictionary<Version, string> SdkDirectories { get; }

    /// <summary>
    /// The directory of the newest SDK here — the MSBuild to register when
    /// this installation is the newest available. Registering the NEWEST is
    /// deliberate: MSBuild can only be registered once per process, and the
    /// newer one serves both old and new solutions once DOTNET_ROOT selects
    /// the packs.
    /// </summary>
    public string NewestSdkDirectory => NewestSdkDirectoryFor(allowPrerelease: true);

    /// <summary>
    /// The directory of the newest SDK here, previews included only when
    /// <paramref name="allowPrerelease"/>; "" when there is none.
    /// </summary>
    public string NewestSdkDirectoryFor(bool allowPrerelease)
    {
        var candidates = allowPrerelease ? SdkVersions : StableSdkVersions;
        if (candidates.Count == 0)
            return "";
        return SdkDirectories.TryGetValue(candidates[candidates.Count - 1], out var dir) ? dir : "";
    }

    /// <summary>The SDK versions this installation provides, ascending.</summary>
    public IReadOnlyList<Version> SdkVersions { get; }

    /// <summary>Whether this installation can build for the given .NET version.</summary>
    public bool Provides(Version frameworkVersion) => Provides(frameworkVersion, allowPrerelease: true);

    /// <summary>
    /// Whether this installation can build for the given .NET version, with
    /// preview SDKs counted only when <paramref name="allowPrerelease"/>.
    /// </summary>
    public bool Provides(Version frameworkVersion, bool allowPrerelease)
    {
        if (frameworkVersion == null)
            return false;
        var candidates = allowPrerelease ? SdkVersions : StableSdkVersions;
        return candidates.Any(sdk => sdk.Major == frameworkVersion.Major);
    }

    /// <summary>Whether this is the SDK found on PATH rather than a configured root.</summary>
    public bool IsSystemInstallation { get; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{DotnetPath} ({string.Join(", ", SdkVersions)})";
}

/// <summary>
/// Finds which .NET SDK installation can build a given target framework.
/// </summary>
/// <remarks>
/// A project's .NET version decides which SDK must build it, and an SDK cannot
/// build for a NEWER .NET than itself. Handing a net11.0-android project to a
/// .NET 10 SDK fails with "NETSDK1139: The target platform identifier android
/// was not recognized", which reads as "Android is unsupported" and sends the
/// reader off in entirely the wrong direction — so the IDE picks the SDK
/// deliberately rather than letting that happen.
/// <para>
/// Installations beyond the system one are CONFIGURED, never guessed: a
/// side-by-side SDK (a preview kept out of the system tree so it cannot become
/// the default for every other project on the machine) lives wherever its owner
/// put it.
/// </para>
/// </remarks>
public sealed class DotNetSdkLocator
{
    // "10.0.400 [/usr/share/dotnet/sdk]" and
    // "11.0.100-preview.7.26381.103 [/home/jeremy/dotnet11/sdk]"
    static readonly Regex sdkLine = new Regex(
        @"^(?<version>\d+\.\d+\.\d+)(?<prerelease>-[^\s]+)?\s+\[(?<dir>[^\]]+)\]",
        RegexOptions.Compiled);

    readonly List<DotNetSdkInstallation> installations = new List<DotNetSdkInstallation>();

    /// <summary>
    /// Builds a locator over the system SDK plus the given additional roots
    /// (each the folder holding a "dotnet" executable). Roots that do not
    /// exist, or hold no SDK, are skipped silently — a stale preference must
    /// not stop the IDE building anything.
    /// </summary>
    public DotNetSdkLocator(IEnumerable<string> additionalRoots)
    {
        var system = ProbeSystem();
        if (system != null)
            installations.Add(system);

        if (additionalRoots == null)
            return;
        foreach (var root in additionalRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var expanded = ExpandHome(root.Trim());
            var executable = Path.Combine(expanded, "dotnet");
            if (!File.Exists(executable))
                continue;
            var directories = new Dictionary<Version, string>();
            var prerelease = new HashSet<Version>();
            var versions = ListSdkVersions(executable, expanded, directories, prerelease);
            if (versions.Count > 0)
                installations.Add(new DotNetSdkInstallation(executable, expanded, versions,
                    isSystemInstallation: false, sdkDirectories: directories, prerelease: prerelease));
        }
    }

    /// <summary>Every installation found, the system one first.</summary>
    public IReadOnlyList<DotNetSdkInstallation> Installations => installations;

    /// <summary>
    /// The installation with the highest SDK version, or null when none were
    /// found. This is the MSBuild to register: registration happens ONCE per
    /// process, and the newest serves solutions of every vintage once
    /// DOTNET_ROOT points at the right packs.
    /// </summary>
    public DotNetSdkInstallation Newest => NewestFor(allowPrerelease: true);

    /// <summary>
    /// The installation with the highest SDK version, previews counted only
    /// when <paramref name="allowPrerelease"/>. This is the MSBuild that gets
    /// registered, so with previews disallowed the IDE never loads a preview
    /// MSBuild — which is the whole point of the setting, because that
    /// registration outlives the solution that prompted it and applies to
    /// every solution opened afterwards.
    /// </summary>
    public DotNetSdkInstallation NewestFor(bool allowPrerelease)
        => installations
            .Where(i => (allowPrerelease ? i.SdkVersions : i.StableSdkVersions).Count > 0)
            .OrderByDescending(i =>
            {
                var versions = allowPrerelease ? i.SdkVersions : i.StableSdkVersions;
                return versions[versions.Count - 1];
            })
            .FirstOrDefault();

    /// <summary>
    /// The installation to build the given framework version with, or null
    /// when nothing installed can. The SYSTEM SDK WINS when it qualifies, so
    /// ordinary projects keep building exactly as they did before any extra
    /// root was configured; an additional root is only reached for a version
    /// the system SDK cannot handle.
    /// </summary>
    public DotNetSdkInstallation Resolve(Version frameworkVersion)
        => Resolve(frameworkVersion, allowPrerelease: true);

    /// <summary>
    /// The installation to build the given framework version with, counting
    /// preview SDKs only when <paramref name="allowPrerelease"/>. Returns null
    /// when nothing eligible can build it — which, with previews disallowed,
    /// is how a .NET 11 project is refused rather than mis-built.
    /// </summary>
    public DotNetSdkInstallation Resolve(Version frameworkVersion, bool allowPrerelease)
    {
        if (frameworkVersion == null)
            return installations.FirstOrDefault();
        return installations.FirstOrDefault(i => i.Provides(frameworkVersion, allowPrerelease));
    }

    /// <summary>
    /// Whether some installation COULD build this framework version, but only
    /// with a preview SDK. This is what separates "you need to allow previews"
    /// from "nothing installed can build this at all" — two very different
    /// things to tell the user.
    /// </summary>
    public bool NeedsPrerelease(Version frameworkVersion)
        => frameworkVersion != null
        && Resolve(frameworkVersion, allowPrerelease: false) == null
        && Resolve(frameworkVersion, allowPrerelease: true) != null;

    /// <summary>
    /// The installation for a project, or null when none can build it.
    /// A project whose moniker does not parse (netstandard, net472) resolves
    /// to the system SDK, which is what built it before.
    /// </summary>
    public DotNetSdkInstallation Resolve(DotNetProject project)
        => Resolve(project?.TargetFramework?.FrameworkVersion);

    static DotNetSdkInstallation ProbeSystem()
    {
        // "dotnet" on PATH is the normal case; the explicit paths cover an IDE
        // started from a desktop launcher with a threadbare PATH.
        foreach (var candidate in new[] { "dotnet", "/usr/bin/dotnet", "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet" })
        {
            var directories = new Dictionary<Version, string>();
            var prerelease = new HashSet<Version>();
            var versions = ListSdkVersions(candidate, "", directories, prerelease);
            if (versions.Count == 0)
                continue;
            // The root is the grandparent of an SDK directory
            // (/usr/share/dotnet/sdk/10.0.400 -> /usr/share/dotnet). Knowing it
            // matters because switching solutions sets DOTNET_ROOT explicitly,
            // including back to the system one.
            var root = "";
            if (directories.Count > 0)
            {
                var sdkDirectory = directories.Values.First();
                root = Path.GetDirectoryName(Path.GetDirectoryName(sdkDirectory)) ?? "";
            }
            return new DotNetSdkInstallation(candidate, root, versions,
                isSystemInstallation: true, sdkDirectories: directories, prerelease: prerelease);
        }
        return null;
    }

    static string ExpandHome(string path)
    {
        if (!path.StartsWith("~", StringComparison.Ordinal))
            return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.Substring(2));
    }

    static IReadOnlyList<Version> ListSdkVersions(string dotnetPath, string root,
        Dictionary<Version, string> directories = null, HashSet<Version> prerelease = null)
    {
        directories ??= new Dictionary<Version, string>();
        var startInfo = new ProcessStartInfo(dotnetPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--list-sdks");
        if (root.Length > 0)
            startInfo.EnvironmentVariables["DOTNET_ROOT"] = root;

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return Array.Empty<Version>();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(20000);
            var versions = new List<Version>();
            foreach (var line in output.Split('\n'))
            {
                var match = sdkLine.Match(line.Trim());
                // The prerelease suffix is dropped deliberately: 11.0.100-preview.7
                // provides .NET 11 for our purposes, and Version cannot parse it.
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
                    continue;
                versions.Add(version);
                if (match.Groups["prerelease"].Success && match.Groups["prerelease"].Value.Length > 0)
                    prerelease?.Add(version);
                // "--list-sdks" prints the sdk PARENT folder; each version lives
                // in its own directory beneath it, and that is the folder
                // MSBuildLocator wants.
                directories[version] = Path.Combine(match.Groups["dir"].Value.Trim(),
                    match.Groups["version"].Value + match.Groups["prerelease"].Value);
            }
            versions.Sort();
            return versions;
        }
        catch (Exception)
        {
            // A missing or unrunnable dotnet is simply not an installation.
            return Array.Empty<Version>();
        }
    }
}
