//
// TargetFrameworkMoniker.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Globalization;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// A parsed target framework moniker such as "net10.0", "net11.0-android37.0"
/// or "net10.0-windows10.0.19041.0".
/// </summary>
/// <remarks>
/// The IDE needs the PARTS, not the string: the framework version decides
/// which .NET SDK can build the project (a .NET 10 SDK cannot build
/// net11.0-android — it fails with the misleading "target platform identifier
/// android was not recognized"), and the platform and its version decide what
/// the project can do once built.
/// </remarks>
public sealed class TargetFrameworkMoniker
{
    TargetFrameworkMoniker(string moniker, Version frameworkVersion,
        string platformIdentifier, Version platformVersion)
    {
        Moniker = moniker;
        FrameworkVersion = frameworkVersion;
        PlatformIdentifier = platformIdentifier;
        PlatformVersion = platformVersion;
    }

    /// <summary>The moniker exactly as the project declared it.</summary>
    public string Moniker { get; }

    /// <summary>The .NET version, e.g. 10.0 for "net10.0-android36.1".</summary>
    public Version FrameworkVersion { get; }

    /// <summary>
    /// The target platform in lower case, e.g. "android"; "" for a plain
    /// framework moniker such as "net10.0".
    /// </summary>
    public string PlatformIdentifier { get; }

    /// <summary>
    /// The target platform version, e.g. 37.0 for "net11.0-android37.0", or
    /// null when the moniker names a platform without a version, or no
    /// platform at all.
    /// </summary>
    public Version PlatformVersion { get; }

    /// <summary>Whether this moniker targets Android.</summary>
    public bool IsAndroid
        => string.Equals(PlatformIdentifier, "android", StringComparison.Ordinal);

    /// <summary>
    /// Parses a moniker, returning null when it is not a ".NET 5 and later"
    /// style moniker. Everything older ("netstandard2.0", "net472") parses as
    /// null: those never target Android and never need a different SDK, so the
    /// IDE has nothing to decide from them.
    /// </summary>
    public static TargetFrameworkMoniker Parse(string moniker)
    {
        if (string.IsNullOrWhiteSpace(moniker))
            return null;
        var text = moniker.Trim();
        if (!text.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = text.Substring(3);
        // The framework version runs until the '-' that introduces a platform.
        var dash = rest.IndexOf('-');
        var versionText = dash < 0 ? rest : rest.Substring(0, dash);
        // "net472" and "netstandard2.0" both fail here, which is what we want:
        // the first has no dot, the second does not start with a digit.
        if (versionText.Length == 0 || !char.IsDigit(versionText[0])
            || versionText.IndexOf('.') < 0
            || !Version.TryParse(versionText, out var frameworkVersion))
            return null;

        if (dash < 0)
            return new TargetFrameworkMoniker(text, frameworkVersion, "", null);

        var platform = rest.Substring(dash + 1);
        var split = 0;
        while (split < platform.Length && !char.IsDigit(platform[split]))
            split++;

        var identifier = platform.Substring(0, split).ToLowerInvariant();
        Version platformVersion = null;
        if (split < platform.Length)
            Version.TryParse(platform.Substring(split), out platformVersion);

        return new TargetFrameworkMoniker(text, frameworkVersion, identifier, platformVersion);
    }

    /// <inheritdoc/>
    public override string ToString() => Moniker;

    /// <summary>
    /// The framework version as "major.minor", the form the SDK band is
    /// usually spoken of in ("10.0", "11.0").
    /// </summary>
    public string FrameworkVersionText
        => FrameworkVersion.ToString(2);

    /// <summary>
    /// The platform version as the project spelled it, or "" when there is
    /// none. Trailing zero components are kept because "37.0" and "36.1" are
    /// how the Android target frameworks are named.
    /// </summary>
    public string PlatformVersionText
        => PlatformVersion == null
            ? ""
            : PlatformVersion.ToString(Math.Max(2, FieldCount(PlatformVersion)));

    static int FieldCount(Version version)
        => version.Revision >= 0 ? 4 : version.Build >= 0 ? 3 : 2;

    /// <summary>
    /// Parses a moniker into an invariant-culture display string of its parts,
    /// used in messages: "net11.0-android37.0" reads as ".NET 11.0 / android 37.0".
    /// </summary>
    public string Describe()
        => PlatformIdentifier.Length == 0
            ? string.Format(CultureInfo.InvariantCulture, ".NET {0}", FrameworkVersionText)
            : string.Format(CultureInfo.InvariantCulture, ".NET {0} / {1} {2}",
                FrameworkVersionText, PlatformIdentifier, PlatformVersionText).TrimEnd();
}
