//
// PackageReferenceReader.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// Reads PackageReference ids and versions out of project-file TEXT, the
/// counterpart to <see cref="PackageReferenceRewriter"/>. Both the
/// Version="…" attribute and the &lt;Version&gt; child-element forms are
/// understood; a reference carrying neither reports a null version.
/// </summary>
public static class PackageReferenceReader
{
    static readonly Regex tagPattern = new Regex(
        @"<PackageReference\b[^>]*\bInclude\s*=\s*""(?<id>[^""]+)""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex versionAttributePattern = new Regex(
        @"\bVersion\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex versionElementPattern = new Regex(
        @"<Version\s*>([^<]*)</Version\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every PackageReference in the text, in document order, as
    /// (id, version) pairs. Version is null when the reference declares none.
    /// </summary>
    public static IReadOnlyList<(string Id, string Version)> Read(string csprojText)
    {
        var references = new List<(string, string)>();
        if (string.IsNullOrEmpty(csprojText))
            return references;

        foreach (Match match in tagPattern.Matches(csprojText))
        {
            var tag = match.Value;
            var id = match.Groups["id"].Value;

            var attribute = versionAttributePattern.Match(tag);
            if (attribute.Success)
            {
                references.Add((id, attribute.Groups[1].Value));
                continue;
            }

            // No Version attribute: a self-closing tag declares no version;
            // otherwise look for a <Version> element in the body.
            if (tag.EndsWith("/>", StringComparison.Ordinal))
            {
                references.Add((id, null));
                continue;
            }
            var bodyStart = match.Index + tag.Length;
            var closeIndex = csprojText.IndexOf("</PackageReference>", bodyStart, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
            {
                references.Add((id, null));
                continue;
            }
            var element = versionElementPattern.Match(csprojText[bodyStart..closeIndex]);
            references.Add((id, element.Success ? element.Groups[1].Value : null));
        }
        return references;
    }

    /// <summary>
    /// The version declared for the given package id (matched
    /// case-insensitively, as NuGet ids are), or null when the text does not
    /// reference it or declares no version for it.
    /// </summary>
    public static string ReadVersion(string csprojText, string packageId)
    {
        foreach (var (id, version) in Read(csprojText))
        {
            if (string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase))
                return version;
        }
        return null;
    }
}
