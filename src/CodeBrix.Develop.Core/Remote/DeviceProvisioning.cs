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
/// touchscreen), and the kernel's framebuffer console must let go of the screen
/// so the application owns it alone. Running the app as root is never an option,
/// so group membership is the route.
/// <para>
/// The console is detached per boot rather than the text login being masked
/// permanently: a device whose getty is masked boots to a blank screen with no
/// way to log in at the keyboard, which reads as a broken device. Detaching
/// touches nothing on disk, so the device always comes back normal after a
/// reboot.
/// </para>
/// </summary>
public static class DeviceProvisioning
{
    /// <summary>The read-only command listing a user's persistent group names (from /etc/group).</summary>
    public static string PersistentGroupsCommand(string user) => $"id -nG {user}";

    /// <summary>The read-only command listing the CURRENT session's effective group names.</summary>
    public const string CurrentSessionGroupsCommand = "id -nG";

    /// <summary>The (sudo) command adding the user to the video and input groups.</summary>
    public static string AddVideoInputGroupsCommand(string user) => $"sudo usermod -aG video,input {user}";

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
    /// The read-only command reporting each framebuffer console and whether it
    /// is bound to the screen. The kernel's framebuffer console (fbcon) paints
    /// text — and the blinking cursor — into the very memory the application
    /// draws into, so it has to let go before the application owns the screen.
    /// The vtcon index is not fixed (a dummy console usually takes vtcon0), so
    /// the console is found by name rather than by number.
    /// </summary>
    public const string FrameBufferConsoleStateCommand =
        "for d in /sys/class/vtconsole/vtcon*; do " +
        "grep -qi \"frame buffer\" \"$d/name\" 2>/dev/null && echo \"$d $(cat $d/bind 2>/dev/null)\"; done";

    /// <summary>
    /// The (sudo) command detaching the framebuffer console from the screen,
    /// which removes the blinking cursor and stops kernel messages painting
    /// over the application. It lasts until the device reboots — nothing on
    /// disk changes, so the device comes back completely normal.
    /// </summary>
    public const string DetachFrameBufferConsoleCommand =
        "sudo sh -c 'for d in /sys/class/vtconsole/vtcon*; do " +
        "grep -qi \"frame buffer\" \"$d/name\" && echo 0 > \"$d/bind\"; done'";

    /// <summary>
    /// Whether a <see cref="FrameBufferConsoleStateCommand"/> listing reports a
    /// framebuffer console still bound to the screen. A listing that names no
    /// framebuffer console at all reports false: there is nothing to detach, so
    /// nothing should stand between the user and a run.
    /// </summary>
    public static bool IsFrameBufferConsoleAttached(string consoleStateOutput)
    {
        if (string.IsNullOrWhiteSpace(consoleStateOutput))
            return false;
        foreach (var line in consoleStateOutput.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // "<sysfs path> <bind>", where bind is 1 while it owns the screen.
            var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[fields.Length - 1] == "1")
                return true;
        }
        return false;
    }

    /// <summary>
    /// The read-only command listing every mounted filesystem and its options.
    /// /proc/mounts is the kernel's own view and needs no tool beyond cat, so
    /// it works on the most minimal device image.
    /// </summary>
    public const string MountedFilesystemsCommand = "cat /proc/mounts";

    /// <summary>The (sudo) command remounting the root filesystem read-write.</summary>
    public const string RemountRootReadWriteCommand = "sudo mount -o remount,rw /";

    /// <summary>
    /// Whether a <c>/proc/mounts</c> listing reports the root filesystem as
    /// mounted read-only — the state that makes deploying fail partway through
    /// with an opaque SFTP "Failure". A listing that cannot be read, or that
    /// names no root mount, reports false: only a positive <c>ro</c> should
    /// stand between the user and a run.
    /// </summary>
    public static bool IsRootFilesystemReadOnly(string mountsOutput)
    {
        if (string.IsNullOrWhiteSpace(mountsOutput))
            return false;
        var readOnly = false;
        foreach (var line in mountsOutput.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // device mountpoint fstype options dump pass
            var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[1] != "/")
                continue;
            // A later entry for the same mount point shadows the earlier one,
            // so the last root line is the effective state.
            readOnly = HasReadOnlyOption(fields[3]);
        }
        return readOnly;
    }

    /// <summary>Whether a comma-separated mount-option list contains "ro".</summary>
    static bool HasReadOnlyOption(string options)
    {
        foreach (var option in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (option == "ro")
                return true;
        }
        return false;
    }

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

    /// <summary>
    /// The orientation-sensor daemon package: iio-sensor-proxy publishes the
    /// accelerometer's orientation on the D-Bus system bus, which is what an
    /// application declaring UseOrientationSensor follows in production.
    /// Installed on every FrameBuffer device — it is tiny and harmless on
    /// hardware without a sensor.
    /// </summary>
    public const string OrientationSensorPackage = "iio-sensor-proxy";

    /// <summary>The read-only command reporting whether a Debian package is installed.</summary>
    public static string PackageInstalledCommand(string package) => $"dpkg -s {package} 2>/dev/null";

    /// <summary>
    /// Whether a <c>dpkg -s</c> listing reports the package as actually
    /// installed — dpkg also answers for removed-but-not-purged packages, so
    /// only the full installed status counts.
    /// </summary>
    public static bool IsPackageInstalled(string dpkgOutput) =>
        !string.IsNullOrEmpty(dpkgOutput)
        && dpkgOutput.Contains("Status: install ok installed", StringComparison.Ordinal);
}
