//
// DeviceProvisioning.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// The read-only checks and the (sudo) commands that prepare an SSH device to
/// run and debug a LinuxFrameBuffer application: the user must be in the
/// <c>video</c> and <c>input</c> groups (to reach the framebuffer and the
/// touchscreen), and the on-screen text login (getty on tty1) should be masked
/// so the application owns the display. Running the app as root is never an
/// option, so group membership is the route.
/// </summary>
public static class DeviceProvisioning
{
    /// <summary>The console tty whose getty is masked so the app owns the screen.</summary>
    public const string ConsoleTty = "tty1";

    /// <summary>The read-only command listing a user's persistent group names (from /etc/group).</summary>
    public static string PersistentGroupsCommand(string user) => $"id -nG {user}";

    /// <summary>The read-only command listing the CURRENT session's effective group names.</summary>
    public const string CurrentSessionGroupsCommand = "id -nG";

    /// <summary>The read-only command reporting the enablement state of the console getty.</summary>
    public static string GettyEnabledCommand => $"systemctl is-enabled getty@{ConsoleTty}";

    /// <summary>The (sudo) command adding the user to the video and input groups.</summary>
    public static string AddVideoInputGroupsCommand(string user) => $"sudo usermod -aG video,input {user}";

    /// <summary>The (sudo) command masking and stopping the console getty.</summary>
    public static string MaskGettyCommand => $"sudo systemctl mask --now getty@{ConsoleTty}";

    /// <summary>
    /// Whether an <c>id -nG</c> listing (space-separated group names) contains
    /// both the <c>video</c> and <c>input</c> groups.
    /// </summary>
    public static bool HasVideoAndInputGroups(string groupListing)
    {
        if (string.IsNullOrWhiteSpace(groupListing))
            return false;
        var video = false;
        var input = false;
        foreach (var group in groupListing.Split(
            new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (group == "video")
                video = true;
            else if (group == "input")
                input = true;
        }
        return video && input;
    }

    /// <summary>
    /// Whether a <c>systemctl is-enabled</c> result reports the unit as
    /// <c>masked</c> (masking is the state the console getty ends up in).
    /// </summary>
    public static bool IsServiceMasked(string isEnabledOutput) =>
        string.Equals(isEnabledOutput?.Trim(), "masked", StringComparison.Ordinal);

    /// <summary>A native shared library the app needs, and its Debian package.</summary>
    public sealed record NativeDependency(string SharedObject, string AptPackage, bool Fatal);

    /// <summary>
    /// The native libraries a fresh minimal Debian lacks but a FrameBuffer app
    /// needs, discovered on the first live device run. libfontconfig (SkiaSharp
    /// cannot render without it) and libicu (the .NET runtime's globalization)
    /// are fatal; libinput (touch) and libxkbcommon (keyboard keysyms) are not.
    /// libxkbcommon and libicu use their -dev packages because the head loads
    /// the UNVERSIONED libxkbcommon.so (only -dev ships that symlink) and
    /// -dev is the version-proof way to pull the current libicu runtime.
    /// </summary>
    public static readonly IReadOnlyList<NativeDependency> FrameBufferNativeDependencies = new[]
    {
        new NativeDependency("libfontconfig.so.1", "libfontconfig1", Fatal: true),
        new NativeDependency("libicuuc.so", "libicu-dev", Fatal: true),
        new NativeDependency("libinput.so.10", "libinput10", Fatal: false),
        new NativeDependency("libxkbcommon.so", "libxkbcommon-dev", Fatal: false),
    };

    /// <summary>The read-only command that prints the loader's shared-library cache.</summary>
    public static string SharedLibraryCacheCommand => "/sbin/ldconfig -p";

    /// <summary>
    /// The FrameBuffer native dependencies absent from an <c>ldconfig -p</c>
    /// listing — those the device still needs installed.
    /// </summary>
    public static IReadOnlyList<NativeDependency> MissingNativeDependencies(string ldconfigOutput)
    {
        var missing = new List<NativeDependency>();
        foreach (var dependency in FrameBufferNativeDependencies)
        {
            if (string.IsNullOrEmpty(ldconfigOutput)
                || !ldconfigOutput.Contains(dependency.SharedObject, StringComparison.Ordinal))
                missing.Add(dependency);
        }
        return missing;
    }

    /// <summary>The (sudo) command installing the given packages non-interactively.</summary>
    public static string AptInstallCommand(IEnumerable<string> packages) =>
        $"sudo apt install -y {string.Join(' ', packages)}";
}
