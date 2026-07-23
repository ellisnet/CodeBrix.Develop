//
// StartupHeadPolicy.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// The rules that choose a CodeBrix.Platform application's startup head for the
/// operating system and desktop session the IDE is running on, and that decide
/// which heads can be set as the startup project at all. A "head" is a
/// per-platform executable project named "&lt;App&gt;.&lt;Kind&gt;" (or bare
/// "&lt;Kind&gt;"), where Kind is one of the six known platform suffixes.
/// </summary>
public static class StartupHeadPolicy
{
    /// <summary>The recognized platform head kinds, in canonical order.</summary>
    public static readonly IReadOnlyList<string> AllHeadKinds = new[]
    {
        "MacOS", "LinuxX11", "LinuxWayland", "LinuxFrameBuffer", "Win32Skia", "WinWpfSkia",
    };

    /// <summary>
    /// The platform head kind of the given project name (e.g. "LinuxX11"), or
    /// null when the name is not a recognized head.
    /// </summary>
    public static string GetHeadKind(string projectName)
    {
        if (string.IsNullOrEmpty(projectName))
            return null;
        foreach (var kind in AllHeadKinds)
        {
            if (projectName == kind || projectName.EndsWith("." + kind, StringComparison.Ordinal))
                return kind;
        }
        return null;
    }

    /// <summary>Whether the given project name is a recognized platform head.</summary>
    public static bool IsHead(string projectName) => GetHeadKind(projectName) != null;

    /// <summary>
    /// The head kinds that can run on — and so may be manually set as the
    /// startup project for — the given operating system and desktop session.
    /// A Linux session whose type could not be determined still allows the
    /// compositor-agnostic X11 and frame-buffer heads.
    /// </summary>
    public static IReadOnlyList<string> RunnableHeadKinds(string operatingSystem, string desktopSessionType)
    {
        switch (operatingSystem)
        {
            case "macos":
                return new[] { "MacOS" };
            case "windows":
                return new[] { "Win32Skia", "WinWpfSkia" };
            case "linux":
                // A Wayland session can also host an X11 head via XWayland; a
                // pure X11 (or undetected) session cannot host a Wayland head.
                return desktopSessionType == "wayland"
                    ? new[] { "LinuxWayland", "LinuxX11", "LinuxFrameBuffer" }
                    : new[] { "LinuxX11", "LinuxFrameBuffer" };
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The ordered head kinds to try when automatically choosing a startup
    /// head. This is the runnable set, except that a Linux session whose type
    /// could not be determined auto-selects nothing (its runnable heads stay
    /// available to choose manually).
    /// </summary>
    public static IReadOnlyList<string> AutoStartupPreference(string operatingSystem, string desktopSessionType)
    {
        if (operatingSystem == "linux" && desktopSessionType != "x11" && desktopSessionType != "wayland")
            return Array.Empty<string>();
        return RunnableHeadKinds(operatingSystem, desktopSessionType);
    }

    /// <summary>
    /// Whether a project can run on the given operating system and desktop
    /// session: any non-head project qualifies, and a head qualifies when its
    /// kind is in the runnable set.
    /// </summary>
    public static bool CanRun(string projectName, string operatingSystem, string desktopSessionType)
    {
        var kind = GetHeadKind(projectName);
        if (kind == null)
            return true;
        return RunnableHeadKinds(operatingSystem, desktopSessionType).Contains(kind);
    }
}
