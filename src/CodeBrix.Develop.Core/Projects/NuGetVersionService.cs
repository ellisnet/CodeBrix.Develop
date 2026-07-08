//
// NuGetVersionService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// Checks nuget.org for the latest published versions of CodeBrix-family
/// packages, using the NuGet V3 flat-container API.
/// </summary>
public static class NuGetVersionService
{
    static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // FreePPlus and SilverAssertions are CodeBrix-family packages named
    // before the CodeBrix.* convention was standardized.
    static readonly string[] codeBrixIdPrefixes = { "CodeBrix.", "FreePPlus", "SilverAssertions" };

    /// <summary>Whether the given NuGet package id is a CodeBrix-family package.</summary>
    public static bool IsCodeBrixPackageId(string packageId)
        => codeBrixIdPrefixes.Any(prefix => packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Queries nuget.org for the latest stable version of a package.
    /// Returns null when the package is unknown or the query fails.
    /// </summary>
    public static async Task<string> GetLatestVersionAsync(string packageId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("versions", out var versionsElement)
                || versionsElement.ValueKind != JsonValueKind.Array)
                return null;
            var versions = new List<string>();
            foreach (var element in versionsElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } version)
                    versions.Add(version);
            }
            return SelectLatest(versions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggingService.LogWarning($"nuget.org version lookup failed for {packageId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Picks the latest version from a flat-container version list (sorted
    /// ascending by NuGet): the last stable entry, or the last entry when
    /// only prereleases exist. Null for an empty list.
    /// </summary>
    internal static string SelectLatest(IReadOnlyList<string> versions)
    {
        if (versions.Count == 0)
            return null;
        for (var i = versions.Count - 1; i >= 0; i--)
        {
            if (!versions[i].Contains('-'))
                return versions[i];
        }
        return versions[versions.Count - 1];
    }

    /// <summary>
    /// Whether a referenced version is up to date against the latest
    /// published version: equal, or numerically newer (e.g. a local build
    /// not yet pushed). Unparseable versions compare by string equality.
    /// </summary>
    public static bool IsUpToDate(string referencedVersion, string latestVersion)
    {
        if (string.Equals(referencedVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
            return true;
        if (TryParseNumeric(referencedVersion, out var referenced) && TryParseNumeric(latestVersion, out var latest))
            return referenced >= latest;
        return false;
    }

    // Parses the numeric part of a version, tolerating prerelease suffixes
    // ("1.2.3-beta.1" parses as 1.2.3).
    static bool TryParseNumeric(string version, out Version parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(version))
            return false;
        var dash = version.IndexOf('-');
        var numeric = dash < 0 ? version : version[..dash];
        // A bare major ("2") is not Version-parseable; normalize to "2.0".
        if (!numeric.Contains('.'))
            numeric += ".0";
        return Version.TryParse(numeric, out parsed);
    }
}
