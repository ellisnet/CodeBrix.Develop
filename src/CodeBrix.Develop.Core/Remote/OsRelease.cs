//
// OsRelease.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// The distribution facts an SSH device carries in /etc/os-release: the
/// distro <see cref="Id"/>, its <see cref="IdLike"/> family, and the release
/// <see cref="VersionId"/> and <see cref="VersionCodename"/>. Every field a
/// device did not report is the literal <see cref="UnknownValue"/> — the same
/// convention as <see cref="SshDeviceIdentity"/>.
/// </summary>
public sealed class OsRelease
{
    /// <summary>The stored stand-in for a field the file did not carry.</summary>
    public const string UnknownValue = "unknown";

    OsRelease(string id, string idLike, string versionId, string versionCodename)
    {
        Id = id;
        IdLike = idLike;
        VersionId = versionId;
        VersionCodename = versionCodename;
    }

    /// <summary>The distribution id, e.g. "debian" or "ubuntu".</summary>
    public string Id { get; }

    /// <summary>
    /// The space-separated family the distribution is "like", e.g. "debian"
    /// on Ubuntu. Debian itself carries no ID_LIKE, so it reads "unknown".
    /// </summary>
    public string IdLike { get; }

    /// <summary>The release version, e.g. "13" (Debian) or "24.04" (Ubuntu).</summary>
    public string VersionId { get; }

    /// <summary>The release codename, e.g. "trixie" or "noble".</summary>
    public string VersionCodename { get; }

    /// <summary>
    /// Parses the contents of /etc/os-release (the shell-style
    /// <c>KEY=value</c> / <c>KEY="value"</c> format). Blank lines, comments,
    /// and unrelated keys are ignored; missing keys become
    /// <see cref="UnknownValue"/>. Null or empty input yields all-unknown.
    /// </summary>
    public static OsRelease Parse(string contents)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(contents))
        {
            foreach (var rawLine in contents.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                var key = line.Substring(0, equals).Trim();
                if (key.Length > 0)
                    map[key] = Unquote(line.Substring(equals + 1).Trim());
            }
        }

        return new OsRelease(
            Pick(map, "ID"),
            Pick(map, "ID_LIKE"),
            Pick(map, "VERSION_ID"),
            Pick(map, "VERSION_CODENAME"));
    }

    static string Pick(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : UnknownValue;

    // os-release values may be wrapped in matching single or double quotes.
    static string Unquote(string value)
    {
        if (value.Length >= 2 && value[^1] == value[0] && value[0] is '"' or '\'')
            return value.Substring(1, value.Length - 2);
        return value;
    }
}
