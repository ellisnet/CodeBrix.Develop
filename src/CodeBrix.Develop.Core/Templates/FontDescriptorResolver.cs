//
// FontDescriptorResolver.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Finds a font package's <see cref="FontDescriptor"/>.
/// <para>
/// The local NuGet cache is consulted first, and it is a real cache rather than
/// a copy of one: a package already restored on this machine has its descriptor
/// sitting on disk, already version-keyed by the folder it lives in, so there is
/// nothing to invalidate and nothing to download. Only a package this machine
/// has never restored costs a network round trip.
/// </para>
/// <para>
/// The consequence worth knowing: generating an application with the template's
/// own font never touches the network at all, and a machine that has built with
/// a font once can keep offering it offline forever.
/// </para>
/// </summary>
public class FontDescriptorResolver
{
    static readonly HttpClient httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    readonly string packagesRoot;

    /// <summary>
    /// Creates a resolver over the machine's NuGet global-packages folder,
    /// honoring NUGET_PACKAGES when set.
    /// </summary>
    public FontDescriptorResolver()
        : this(DefaultPackagesRoot())
    {
    }

    /// <summary>Creates a resolver over a specific global-packages folder (tests).</summary>
    public FontDescriptorResolver(string packagesRoot)
    {
        this.packagesRoot = packagesRoot;
    }

    /// <summary>The NuGet global-packages folder this resolver reads.</summary>
    public string PackagesRoot => packagesRoot;

    /// <summary>
    /// The descriptor for a font package: from the local NuGet cache when any
    /// version of the package is already restored there (newest first), else
    /// downloaded from nuget.org.
    /// </summary>
    /// <exception cref="InvalidDataException">The descriptor exists but cannot be understood.</exception>
    /// <exception cref="FontDescriptorUnavailableException">The descriptor could not be obtained at all.</exception>
    public async Task<FontDescriptor> ResolveAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ArgumentException("A package id is required.", nameof(packageId));

        if (TryReadFromCache(packageId) is { } cached)
            return cached;

        return await DownloadAsync(packageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The descriptor from the local NuGet cache, or null when no restored
    /// version of the package carries one. Versions are tried newest-first by
    /// folder name; an unreadable descriptor in an older folder does not mask a
    /// good one in a newer.
    /// </summary>
    public FontDescriptor TryReadFromCache(string packageId)
    {
        var packageFolder = Path.Combine(packagesRoot, packageId.ToLowerInvariant());
        if (!Directory.Exists(packageFolder))
            return null;

        foreach (var versionFolder in EnumerateVersionFoldersNewestFirst(packageFolder))
        {
            var path = Path.Combine(versionFolder, FontDescriptor.FileName);
            if (File.Exists(path))
                return FontDescriptor.Load(path);
        }
        return null;
    }

    async Task<FontDescriptor> DownloadAsync(string packageId, CancellationToken cancellationToken)
    {
        var lowerId = packageId.ToLowerInvariant();
        string version;
        try
        {
            version = await LatestVersionAsync(lowerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new FontDescriptorUnavailableException(
                $"Could not reach nuget.org to look up {packageId}: {ex.Message}", ex);
        }

        if (version == null)
            throw new FontDescriptorUnavailableException(
                $"nuget.org published no non-preview version of {packageId}.");

        var url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{version}/{lowerId}.{version}.nupkg";
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new FontDescriptorUnavailableException(
                    $"nuget.org returned {(int) response.StatusCode} for {packageId} {version}.");

            using var stream = new MemoryStream(
                await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, FontDescriptor.FileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new FontDescriptorUnavailableException(
                    $"{packageId} {version} does not contain a {FontDescriptor.FileName} file, so " +
                    $"CodeBrix.Develop cannot tell how to reference it. It may predate the descriptor.");

            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return FontDescriptor.Parse(json, $"{FontDescriptor.FileName} in {packageId} {version}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException
                                       && ex is not FontDescriptorUnavailableException)
        {
            throw new FontDescriptorUnavailableException(
                $"Could not read {FontDescriptor.FileName} from {packageId} {version}: {ex.Message}", ex);
        }
    }

    static async Task<string> LatestVersionAsync(string lowerId, CancellationToken cancellationToken)
    {
        var url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/index.json";
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("versions", out var versions))
            return null;

        return versions.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrEmpty(v) && !v.Contains('-'))
            .OrderBy(v => v, VersionFolderComparer.Instance)
            .LastOrDefault();
    }

    static IEnumerable<string> EnumerateVersionFoldersNewestFirst(string packageFolder) =>
        Directory.EnumerateDirectories(packageFolder)
            .OrderByDescending(Path.GetFileName, VersionFolderComparer.Instance);

    static string DefaultPackagesRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    // Orders version strings numerically per dot-separated segment, so
    // 1.0.209.x sorts after 1.0.99.x rather than before it.
    sealed class VersionFolderComparer : IComparer<string>
    {
        public static readonly VersionFolderComparer Instance = new VersionFolderComparer();

        public int Compare(string x, string y)
        {
            var left = (x ?? string.Empty).Split('.');
            var right = (y ?? string.Empty).Split('.');
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var a = i < left.Length && int.TryParse(left[i], out var parsedLeft) ? parsedLeft : -1;
                var b = i < right.Length && int.TryParse(right[i], out var parsedRight) ? parsedRight : -1;
                if (a != b)
                    return a.CompareTo(b);
            }
            return string.CompareOrdinal(x, y);
        }
    }
}

/// <summary>
/// Raised when a font package's descriptor could not be obtained — the package
/// is unknown to nuget.org, the network is unavailable and the package has never
/// been restored on this machine, or the package carries no descriptor.
/// </summary>
public class FontDescriptorUnavailableException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public FontDescriptorUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public FontDescriptorUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
