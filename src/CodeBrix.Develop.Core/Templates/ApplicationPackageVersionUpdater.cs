//
// ApplicationPackageVersionUpdater.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.Projects;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Brings a freshly generated CodeBrix.Platform application's NuGet package
/// references up to date, within the version ceilings CodeBrix.Platform
/// itself imposes. Runs after the whole solution exists on disk (the
/// generated files always carry working versions of their own), and works in
/// five steps:
/// <list type="number">
///   <item>read the latest non-preview version of
///   CodeBrix.Platform.ApacheLicenseForever — the "CodeBrix.Platform Version
///   Number";</item>
///   <item>survey every .csproj in the solution and read the latest
///   non-preview version of every package any of them reference;</item>
///   <item>from the selected platform heads, read the SkiaSharp and
///   HarfBuzzSharp versions CodeBrix.Platform pins — the lowest of each
///   becomes the "SkiaSharp Version Number" and the "HarfBuzzSharp Version
///   Number";</item>
///   <item>hold back any package that would otherwise move past its
///   ceiling;</item>
///   <item>write the results, never lowering a version the solution was
///   generated with.</item>
/// </list>
/// nuget.org is the only source consulted, and never for a preview version.
/// A lookup that cannot be completed raises
/// <see cref="NuGetUnavailableException"/> before anything is written, so the
/// solution simply keeps the versions it was generated with.
/// </summary>
public static class ApplicationPackageVersionUpdater
{
    /// <summary>The package whose latest version is the CodeBrix.Platform Version Number.</summary>
    public const string PlatformPackageId = "CodeBrix.Platform.ApacheLicenseForever";

    /// <summary>The exact SkiaSharp package id — not the SkiaSharp.* family members.</summary>
    public const string SkiaSharpPackageId = "SkiaSharp";

    /// <summary>The exact HarfBuzzSharp package id — not the HarfBuzzSharp.* family members.</summary>
    public const string HarfBuzzSharpPackageId = "HarfBuzzSharp";

    /// <summary>
    /// The six platform head runtime packages. Which of them a solution
    /// actually references depends on the heads the user chose to generate.
    /// </summary>
    public static IReadOnlyList<string> PrimaryHeadPackageIds { get; } =
        PlatformHeadInfo.All.Select(head => head.PackageId).ToList();

    /// <summary>
    /// Updates every package reference in every .csproj under the
    /// application root, subject to the CodeBrix.Platform, SkiaSharp, and
    /// HarfBuzzSharp ceilings. Returns the number of references changed.
    /// Nothing is written unless every nuget.org lookup succeeded.
    /// </summary>
    /// <exception cref="NuGetUnavailableException">nuget.org could not supply the version data.</exception>
    /// <exception cref="InvalidOperationException">A head package pins SkiaSharp or HarfBuzzSharp in a form this policy cannot honor.</exception>
    public static async Task<int> UpdateAsync(string applicationRoot, CancellationToken cancellationToken = default)
    {
        var projectTexts = ReadProjectFiles(applicationRoot);
        if (projectTexts.Count == 0)
            return 0;

        var referencedIds = projectTexts.Values
            .SelectMany(text => PackageReferenceReader.Read(text).Select(reference => reference.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (referencedIds.Count == 0)
            return 0;

        var resolver = new PackageVersionResolver();

        // Step 1 — the CodeBrix.Platform Version Number.
        var platformLookup = await resolver.ResolveLatestVersionsAsync(
            new[] { PlatformPackageId }, cancellationToken).ConfigureAwait(false);
        if (!platformLookup.TryGetValue(PlatformPackageId, out var platformVersion))
            throw new NuGetUnavailableException(
                $"nuget.org published no non-preview version of {PlatformPackageId}.");
        LoggingService.LogInfo($"CodeBrix.Platform Version Number: {platformVersion}");

        // Step 2 — the latest non-preview version of everything referenced.
        var latestVersions = await resolver.ResolveLatestVersionsAsync(referencedIds, cancellationToken)
            .ConfigureAwait(false);

        // Step 3 — the SkiaSharp and HarfBuzzSharp ceilings, read from the
        // heads this solution actually has, at the CodeBrix.Platform Version
        // Number (the version those heads are being pinned to in step 4).
        var headPackageIds = referencedIds
            .Where(id => PrimaryHeadPackageIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        string skiaVersion = null;
        string harfBuzzVersion = null;
        foreach (var headPackageId in headPackageIds)
        {
            var dependencies = await resolver.ResolveDependencyVersionsAsync(
                headPackageId, platformVersion, cancellationToken).ConfigureAwait(false);
            skiaVersion = NuGetVersion.Lower(skiaVersion,
                PinnedDependencyVersion(dependencies, headPackageId, SkiaSharpPackageId));
            harfBuzzVersion = NuGetVersion.Lower(harfBuzzVersion,
                PinnedDependencyVersion(dependencies, headPackageId, HarfBuzzSharpPackageId));
        }
        LoggingService.LogInfo(
            $"SkiaSharp Version Number: {skiaVersion ?? "(none declared)"}; HarfBuzzSharp Version Number: {harfBuzzVersion ?? "(none declared)"}");

        // Step 4 — hold back anything that would pass its ceiling.
        var targetVersions = ApplyCeilings(latestVersions, platformVersion, skiaVersion, harfBuzzVersion);

        // Step 5 — write.
        return WriteVersions(projectTexts, targetVersions);
    }

    /// <summary>
    /// Applies the three version ceilings to the resolved latest versions:
    /// CodeBrix.Platform.ApacheLicenseForever and the platform head packages
    /// may not pass the CodeBrix.Platform Version Number; SkiaSharp and the
    /// SkiaSharp.* family may not pass the SkiaSharp Version Number;
    /// HarfBuzzSharp and the HarfBuzzSharp.* family may not pass the
    /// HarfBuzzSharp Version Number. A null ceiling imposes no limit, and
    /// every other package keeps its latest version.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ApplyCeilings(
        IReadOnlyDictionary<string, string> latestVersions,
        string platformVersion, string skiaVersion, string harfBuzzVersion)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in latestVersions)
        {
            var ceiling = CeilingFor(pair.Key, platformVersion, skiaVersion, harfBuzzVersion);
            var version = ceiling == null ? pair.Value : NuGetVersion.Lower(pair.Value, ceiling);
            if (ceiling != null && !string.Equals(version, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.LogInfo(
                    $"Package {pair.Key}: held at {version} (latest is {pair.Value}) — pinned by CodeBrix.Platform");
            }
            results[pair.Key] = version;
        }
        return results;
    }

    // The ceiling governing a package id, or null when it is uncapped. Ids
    // are matched case-insensitively, as NuGet ids are.
    static string CeilingFor(string packageId, string platformVersion, string skiaVersion, string harfBuzzVersion)
    {
        if (string.Equals(packageId, PlatformPackageId, StringComparison.OrdinalIgnoreCase)
            || PrimaryHeadPackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase))
            return platformVersion;
        if (IsPackageOrFamily(packageId, SkiaSharpPackageId))
            return skiaVersion;
        if (IsPackageOrFamily(packageId, HarfBuzzSharpPackageId))
            return harfBuzzVersion;
        return null;
    }

    // The package itself, or a member of its dotted family ("SkiaSharp" and
    // "SkiaSharp.NativeAssets.Linux", but not "SkiaSharpener").
    static bool IsPackageOrFamily(string packageId, string familyId) =>
        string.Equals(packageId, familyId, StringComparison.OrdinalIgnoreCase)
        || packageId.StartsWith(familyId + ".", StringComparison.OrdinalIgnoreCase);

    // The version a head package pins a dependency to, or null when it
    // declares no such dependency. CodeBrix.Platform pins SkiaSharp and
    // HarfBuzzSharp to specific versions; anything else — a version range, a
    // floating version — is a policy this updater cannot honor, and fails.
    static string PinnedDependencyVersion(
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies, string headPackageId, string dependencyId)
    {
        if (!dependencies.TryGetValue(dependencyId, out var declaredVersions) || declaredVersions.Count == 0)
            return null;

        string lowest = null;
        foreach (var declared in declaredVersions)
        {
            if (!NuGetVersion.IsPinned(declared))
                throw new InvalidOperationException(
                    $"The {headPackageId} package declares its {dependencyId} dependency as \"{declared}\", which is not a pinned version; the package versions of this application cannot be updated safely.");
            lowest = NuGetVersion.Lower(lowest, declared);
        }
        return lowest;
    }

    // Every .csproj under the application root — src, src/libs, tests/libs,
    // and anywhere else the generated solution puts one.
    static IReadOnlyDictionary<string, string> ReadProjectFiles(string applicationRoot)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(applicationRoot))
            return texts;
        foreach (var path in Directory.EnumerateFiles(applicationRoot, "*.csproj", SearchOption.AllDirectories))
            texts[path] = File.ReadAllText(path);
        return texts;
    }

    // Writes the target versions, never lowering a version the solution was
    // generated with: the generated version is correct unless something newer
    // is available.
    static int WriteVersions(IReadOnlyDictionary<string, string> projectTexts,
        IReadOnlyDictionary<string, string> targetVersions)
    {
        var updatedCount = 0;
        foreach (var pair in projectTexts)
        {
            var text = pair.Value;
            var changed = false;
            foreach (var (id, currentVersion) in PackageReferenceReader.Read(pair.Value))
            {
                if (!targetVersions.TryGetValue(id, out var target) || target == null)
                    continue;
                if (currentVersion != null && NuGetVersion.Compare(target, currentVersion) <= 0)
                    continue;
                text = PackageReferenceRewriter.UpdateVersion(text, id, target, out var updated);
                if (updated)
                {
                    LoggingService.LogInfo(
                        $"Package {id}: {currentVersion} updated to {target} in {Path.GetFileName(pair.Key)}");
                    changed = true;
                    updatedCount++;
                }
            }
            if (changed)
                File.WriteAllText(pair.Key, text);
        }
        return updatedCount;
    }
}
