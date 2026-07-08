//
// PackageReferenceRewriter.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Text.RegularExpressions;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// Rewrites PackageReference versions in project-file TEXT with surgical
/// string replacement, so formatting, comments, and attribute ordering are
/// preserved byte-for-byte except for the changed version strings.
/// </summary>
public static class PackageReferenceRewriter
{
    /// <summary>
    /// Returns <paramref name="csprojText"/> with every PackageReference to
    /// the given package updated to <paramref name="newVersion"/>, handling
    /// both the Version="…" attribute and the &lt;Version&gt; child-element
    /// forms. <paramref name="updated"/> reports whether anything changed.
    /// </summary>
    public static string UpdateVersion(string csprojText, string packageId, string newVersion, out bool updated)
    {
        updated = false;
        var tagRegex = new Regex(
            $@"<PackageReference\b[^>]*\bInclude\s*=\s*""{Regex.Escape(packageId)}""[^>]*>",
            RegexOptions.IgnoreCase);

        // Last match first, so earlier match indexes stay valid as the text
        // changes length.
        var matches = tagRegex.Matches(csprojText);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var tag = match.Value;

            var versionAttribute = Regex.Match(tag, @"\bVersion\s*=\s*""([^""]*)""");
            if (versionAttribute.Success)
            {
                var value = versionAttribute.Groups[1];
                if (string.Equals(value.Value, newVersion, StringComparison.Ordinal))
                    continue;
                var newTag = tag.Remove(value.Index, value.Length).Insert(value.Index, newVersion);
                csprojText = csprojText.Remove(match.Index, tag.Length).Insert(match.Index, newTag);
                updated = true;
                continue;
            }

            // No Version attribute: a self-closing tag has nothing to
            // update; otherwise look for a <Version> element in the body.
            if (tag.EndsWith("/>", StringComparison.Ordinal))
                continue;
            var bodyStart = match.Index + tag.Length;
            var closeIndex = csprojText.IndexOf("</PackageReference>", bodyStart, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
                continue;
            var body = csprojText[bodyStart..closeIndex];
            var versionElement = Regex.Match(body, @"<Version\s*>([^<]*)</Version\s*>", RegexOptions.IgnoreCase);
            if (!versionElement.Success)
                continue;
            var elementValue = versionElement.Groups[1];
            if (string.Equals(elementValue.Value, newVersion, StringComparison.Ordinal))
                continue;
            var absoluteIndex = bodyStart + elementValue.Index;
            csprojText = csprojText.Remove(absoluteIndex, elementValue.Length).Insert(absoluteIndex, newVersion);
            updated = true;
        }
        return csprojText;
    }
}
