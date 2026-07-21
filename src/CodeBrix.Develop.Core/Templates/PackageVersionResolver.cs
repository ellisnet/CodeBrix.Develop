//
// PackageVersionResolver.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Reads package version facts from nuget.org — the only authoritative
/// source of package version information: the latest non-preview version of
/// a package, and the dependencies a specific published package version
/// declares. No other source (the local NuGet cache included) is consulted,
/// and a lookup that cannot be completed raises
/// <see cref="NuGetUnavailableException"/> so the caller can end the whole
/// version-bump operation rather than proceed on partial data.
/// </summary>
public class PackageVersionResolver
{
    static readonly HttpClient httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// The latest non-preview version of each package id. A package
    /// nuget.org does not know, and a package whose only published versions
    /// are previews, is absent from the result — that means "leave this
    /// package's version alone", not a failure.
    /// </summary>
    /// <exception cref="NuGetUnavailableException">A lookup could not be completed.</exception>
    public async Task<IReadOnlyDictionary<string, string>> ResolveLatestVersionsAsync(
        IEnumerable<string> packageIds, CancellationToken cancellationToken = default)
    {
        var ids = packageIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lookups = ids.Select(id => ResolveOneAsync(id, cancellationToken)).ToList();
        var versions = await Task.WhenAll(lookups).ConfigureAwait(false);

        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ids.Count; i++)
        {
            if (versions[i] != null)
                results[ids[i]] = versions[i];
        }
        return results;
    }

    /// <summary>
    /// The dependencies declared by one published package version, as
    /// dependency id → every version string declared for it (a package may
    /// declare the same dependency in more than one target-framework group).
    /// Ids are compared case-insensitively, as NuGet ids are.
    /// </summary>
    /// <exception cref="NuGetUnavailableException">The .nuspec could not be read.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveDependencyVersionsAsync(
        string packageId, string version, CancellationToken cancellationToken = default)
    {
        var lowerId = packageId.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{version.ToLowerInvariant()}/{lowerId}.nuspec";

        string nuspec;
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new NuGetUnavailableException(
                    $"nuget.org returned {(int) response.StatusCode} for the {packageId} {version} package manifest.");
            nuspec = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NuGetUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NuGetUnavailableException(
                $"The {packageId} {version} package manifest could not be read from nuget.org: {ex.Message}", ex);
        }

        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var document = XDocument.Parse(nuspec);
            // The .nuspec namespace varies by schema version, so match on the
            // local element name rather than a fixed namespace.
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "dependency"))
            {
                var id = (string) element.Attribute("id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var declared = (string) element.Attribute("version");
                if (string.IsNullOrWhiteSpace(declared))
                    continue;
                if (!dependencies.TryGetValue(id, out var declaredVersions))
                    dependencies[id] = declaredVersions = new List<string>();
                declaredVersions.Add(declared);
            }
        }
        catch (Exception ex)
        {
            throw new NuGetUnavailableException(
                $"The {packageId} {version} package manifest from nuget.org could not be parsed: {ex.Message}", ex);
        }

        return dependencies.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>) pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    // The latest non-preview version, or null when nuget.org does not know
    // the package or publishes nothing but previews for it.
    async Task<string> ResolveOneAsync(string packageId, CancellationToken cancellationToken)
    {
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A successful answer of "no such package": nothing to update.
                LoggingService.LogInfo($"Package {packageId}: not published on nuget.org; its version is left as-is");
                return null;
            }
            if (!response.IsSuccessStatusCode)
                throw new NuGetUnavailableException(
                    $"nuget.org returned {(int) response.StatusCode} for {packageId}.");

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("versions", out var versionsElement)
                || versionsElement.ValueKind != JsonValueKind.Array)
                throw new NuGetUnavailableException($"nuget.org returned no version list for {packageId}.");

            var versions = versionsElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(version => !string.IsNullOrEmpty(version))
                .ToList();

            var latest = NuGetVersion.SelectLatestRelease(versions);
            if (latest == null)
                LoggingService.LogInfo($"Package {packageId}: no non-preview version published; its version is left as-is");
            return latest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NuGetUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NuGetUnavailableException(
                $"The latest version of {packageId} could not be read from nuget.org: {ex.Message}", ex);
        }
    }
}
