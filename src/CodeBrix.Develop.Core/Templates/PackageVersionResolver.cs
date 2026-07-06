//
// PackageVersionResolver.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Resolves the latest published version of NuGet packages so generated
/// project files can pin explicit Version attributes. Tries nuget.org
/// first, falls back to the newest version present in the local NuGet
/// cache (~/.nuget/packages), and reports null when neither knows the
/// package — the caller then emits the reference unversioned and lets the
/// first restore resolve it.
/// </summary>
public class PackageVersionResolver
{
    static readonly HttpClient httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    readonly string localCacheRoot;

    /// <summary>Creates a resolver using the default local NuGet cache location.</summary>
    public PackageVersionResolver() : this(null)
    {
    }

    internal PackageVersionResolver(string localCacheRootOverride)
    {
        localCacheRoot = localCacheRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    /// <summary>
    /// Resolves the latest stable version for each package id, querying
    /// nuget.org concurrently. Entries whose version could not be
    /// determined map to null.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveLatestVersionsAsync(
        IEnumerable<string> packageIds, CancellationToken cancellationToken = default)
    {
        var ids = packageIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lookups = ids.Select(id => ResolveOneAsync(id, cancellationToken)).ToList();
        var versions = await Task.WhenAll(lookups).ConfigureAwait(false);

        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ids.Count; i++)
            results[ids[i]] = versions[i];
        return results;
    }

    async Task<string> ResolveOneAsync(string packageId, CancellationToken cancellationToken)
    {
        var fromNuget = await TryGetNugetOrgVersionAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (fromNuget != null)
            return fromNuget;

        var fromCache = GetNewestLocalCacheVersion(packageId);
        if (fromCache != null)
        {
            LoggingService.LogInfo($"Package {packageId}: using version {fromCache} from the local NuGet cache (nuget.org unavailable)");
            return fromCache;
        }

        LoggingService.LogWarning($"Package {packageId}: latest version unknown; the reference will be generated unversioned");
        return null;
    }

    async Task<string> TryGetNugetOrgVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
            using var response = await httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var versions = document.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(element => element.GetString())
                .Where(version => !string.IsNullOrEmpty(version))
                .ToList();
            return PickNewest(versions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Package {packageId}: nuget.org lookup failed: {ex.Message}");
            return null;
        }
    }

    internal string GetNewestLocalCacheVersion(string packageId)
    {
        try
        {
            var packageFolder = Path.Combine(localCacheRoot, packageId.ToLowerInvariant());
            if (!Directory.Exists(packageFolder))
                return null;
            var versions = Directory.EnumerateDirectories(packageFolder)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
            return PickNewest(versions);
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Package {packageId}: local NuGet cache lookup failed: {ex.Message}");
            return null;
        }
    }

    // The newest version, preferring stable releases over prereleases.
    internal static string PickNewest(IReadOnlyList<string> versions)
    {
        if (versions == null || versions.Count == 0)
            return null;
        var stable = versions.Where(version => !version.Contains('-')).ToList();
        var pool = stable.Count > 0 ? stable : versions;
        return pool.OrderBy(version => version, VersionComparer.Instance).Last();
    }

    // Compares dotted numeric versions (with any prerelease tag stripped);
    // a stable version sorts above the same numbers with a prerelease tag.
    sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new VersionComparer();

        public int Compare(string x, string y)
        {
            var (xParts, xPrerelease) = Parse(x);
            var (yParts, yPrerelease) = Parse(y);
            for (var i = 0; i < Math.Max(xParts.Length, yParts.Length); i++)
            {
                var xValue = i < xParts.Length ? xParts[i] : 0;
                var yValue = i < yParts.Length ? yParts[i] : 0;
                if (xValue != yValue)
                    return xValue.CompareTo(yValue);
            }
            if (xPrerelease == null && yPrerelease == null)
                return 0;
            if (xPrerelease == null)
                return 1;
            if (yPrerelease == null)
                return -1;
            return string.CompareOrdinal(xPrerelease, yPrerelease);
        }

        static (int[] Parts, string Prerelease) Parse(string version)
        {
            var dash = version.IndexOf('-');
            var prerelease = dash < 0 ? null : version.Substring(dash + 1);
            var release = dash < 0 ? version : version.Substring(0, dash);
            var parts = release.Split('.')
                .Select(part => int.TryParse(part, out var value) ? value : 0)
                .ToArray();
            return (parts, prerelease);
        }
    }
}
