//
// DotnetRuntimeProbe.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// The "does this device have .NET?" probe: the command to run over SSH and
/// the reading of its result. The command tries the PATH first and then the
/// places a dotnet lands that an SSH exec channel's non-login shell cannot
/// see (the apt install is on PATH at /usr/bin/dotnet; a dotnet-install.sh
/// install sits in ~/.dotnet with its PATH export in a login-shell profile).
/// </summary>
public static class DotnetRuntimeProbe
{
    /// <summary>The probe command for an SSH exec channel.</summary>
    public const string Command =
        "dotnet --list-runtimes 2>/dev/null"
        + " || /usr/bin/dotnet --list-runtimes 2>/dev/null"
        + " || \"$HOME/.dotnet/dotnet\" --list-runtimes 2>/dev/null";

    /// <summary>
    /// Whether the probe output shows a usable .NET on the device: a zero
    /// exit and at least one Microsoft.NETCore.App runtime listed. A dotnet
    /// host with no runtimes cannot run an application, so it counts as not
    /// installed. A null exit status (the channel yielded none) counts as
    /// failure.
    /// </summary>
    public static bool IndicatesInstalled(int? exitStatus, string output)
    {
        if (exitStatus != 0 || string.IsNullOrWhiteSpace(output))
            return false;
        foreach (var line in output.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The versions of the shared runtime (Microsoft.NETCore.App) the probe
    /// output lists, in the order they appear — e.g. ["10.0.10"]. Empty when
    /// none are present.
    /// </summary>
    public static IReadOnlyList<string> GetRuntimeVersions(string output)
    {
        const string prefix = "Microsoft.NETCore.App ";
        var versions = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
            return versions;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            // "Microsoft.NETCore.App 10.0.10 [/path]" → the token after the name.
            var rest = trimmed.Substring(prefix.Length).TrimStart();
            var space = rest.IndexOf(' ');
            var version = space >= 0 ? rest.Substring(0, space) : rest;
            if (version.Length > 0)
                versions.Add(version);
        }
        return versions;
    }
}
