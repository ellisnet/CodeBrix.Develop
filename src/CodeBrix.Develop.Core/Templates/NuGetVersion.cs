//
// NuGetVersion.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Version-string handling for the NuGet package version policy: what counts
/// as a preview, how two published versions order, and which entry of a
/// version list is the latest release. CodeBrix.Develop never auto-updates a
/// package to a preview version, so throughout this namespace "the latest
/// version of a package" means "the latest non-preview version".
/// </summary>
public static class NuGetVersion
{
    /// <summary>
    /// Whether the version is a prerelease/preview — a SemVer '-' suffix.
    /// Build metadata ('+…') is not a prerelease and is ignored.
    /// </summary>
    public static bool IsPreview(string version)
    {
        if (string.IsNullOrEmpty(version))
            return false;
        return StripBuildMetadata(version).IndexOf('-') >= 0;
    }

    /// <summary>
    /// Whether the version is a single pinned version rather than a version
    /// range or floating version — "4.150.1" is pinned; "[4.150.1,5.0.0)",
    /// "[4.150.1]", and "4.*" are not.
    /// </summary>
    public static bool IsPinned(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        if (version.IndexOfAny(new[] { '[', ']', '(', ')', ',', '*', ' ' }) >= 0)
            return false;
        return SplitNumericParts(version).Length > 0;
    }

    /// <summary>
    /// The latest non-preview version in the list, or null when the list is
    /// empty or holds nothing but previews (which means "leave this package
    /// alone", never "take the preview").
    /// </summary>
    public static string SelectLatestRelease(IEnumerable<string> versions)
    {
        if (versions == null)
            return null;
        string latest = null;
        foreach (var version in versions)
        {
            if (string.IsNullOrWhiteSpace(version) || IsPreview(version))
                continue;
            if (latest == null || Compare(version, latest) > 0)
                latest = version;
        }
        return latest;
    }

    /// <summary>
    /// Orders two versions: dotted numeric parts compare left to right with
    /// missing parts read as zero, and a release sorts above the same numbers
    /// carrying a prerelease tag. Build metadata is ignored.
    /// </summary>
    public static int Compare(string x, string y)
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

    /// <summary>The lower/older of two versions; a null argument yields the other.</summary>
    public static string Lower(string x, string y)
    {
        if (x == null)
            return y;
        if (y == null)
            return x;
        return Compare(x, y) <= 0 ? x : y;
    }

    static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version.Substring(0, plus);
    }

    static (int[] Parts, string Prerelease) Parse(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return (Array.Empty<int>(), null);
        var text = StripBuildMetadata(version);
        var dash = text.IndexOf('-');
        var prerelease = dash < 0 ? null : text.Substring(dash + 1);
        var release = dash < 0 ? text : text.Substring(0, dash);
        return (SplitNumericParts(release), prerelease);
    }

    static int[] SplitNumericParts(string release)
    {
        var segments = StripBuildMetadata(release).Split('-')[0].Split('.');
        if (segments.Length == 0 || !segments.All(segment => int.TryParse(segment, out _)))
            return Array.Empty<int>();
        return segments.Select(int.Parse).ToArray();
    }
}
