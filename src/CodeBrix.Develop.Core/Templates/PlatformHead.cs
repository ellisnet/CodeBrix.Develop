//
// PlatformHead.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// The six CodeBrix.Platform (Skia) targets a generated application can
/// have a head project for, in the order they are offered to the user.
/// </summary>
public enum PlatformHead
{
    /// <summary>macOS (Apple Silicon + Intel).</summary>
    MacOS,
    /// <summary>Linux desktop via X11 (also runs on Wayland through XWayland).</summary>
    LinuxX11,
    /// <summary>Linux desktop as a pure native Wayland client.</summary>
    LinuxWayland,
    /// <summary>Linux framebuffer (no desktop; kiosk/embedded).</summary>
    LinuxFrameBuffer,
    /// <summary>Windows hosted in a Win32 window (the common Windows head).</summary>
    Win32Skia,
    /// <summary>Windows hosted inside WPF.</summary>
    WinWpfSkia,
}

/// <summary>
/// The per-head facts that drive project generation: what the head project
/// is named, which single runtime package it references, and how its
/// Program.cs selects the platform.
/// </summary>
public sealed class PlatformHeadInfo
{
    /// <summary>The head this describes.</summary>
    public PlatformHead Head { get; }

    /// <summary>The label shown to the user (e.g. "Windows Win32-Skia").</summary>
    public string DisplayName { get; }

    /// <summary>The project-name suffix (e.g. "Win32Skia" — never "Windows").</summary>
    public string ProjectSuffix { get; }

    /// <summary>The single platform head package the project references.</summary>
    public string PackageId { get; }

    /// <summary>The CodeBrixPlatformHostBuilder platform selector (e.g. "UseLinuxX11").</summary>
    public string BootstrapCall { get; }

    /// <summary>The head project's target framework.</summary>
    public string TargetFramework { get; }

    /// <summary>
    /// Whether this is the special WPF head: net10.0-windows target and the
    /// software-rendering line after Build().
    /// </summary>
    public bool IsWpf => Head == PlatformHead.WinWpfSkia;

    PlatformHeadInfo(PlatformHead head, string displayName, string projectSuffix,
        string packageId, string bootstrapCall, string targetFramework)
    {
        Head = head;
        DisplayName = displayName;
        ProjectSuffix = projectSuffix;
        PackageId = packageId;
        BootstrapCall = bootstrapCall;
        TargetFramework = targetFramework;
    }

    /// <summary>All heads, in the order they are offered to the user.</summary>
    public static IReadOnlyList<PlatformHeadInfo> All { get; } = new[]
    {
        new PlatformHeadInfo(PlatformHead.MacOS, "macOS", "MacOS",
            "CodeBrix.Platform.Runtime.Skia.MacOS.ApacheLicenseForever", "UseMacOS", "net10.0"),
        new PlatformHeadInfo(PlatformHead.LinuxX11, "Linux X11", "LinuxX11",
            "CodeBrix.Platform.Runtime.Skia.X11.ApacheLicenseForever", "UseLinuxX11", "net10.0"),
        new PlatformHeadInfo(PlatformHead.LinuxWayland, "Linux Wayland", "LinuxWayland",
            "CodeBrix.Platform.Runtime.Skia.Wayland.ApacheLicenseForever", "UseLinuxWayland", "net10.0"),
        new PlatformHeadInfo(PlatformHead.LinuxFrameBuffer, "Linux Frame Buffer", "LinuxFrameBuffer",
            "CodeBrix.Platform.Runtime.Skia.FrameBuffer.ApacheLicenseForever", "UseLinuxFrameBuffer", "net10.0"),
        new PlatformHeadInfo(PlatformHead.Win32Skia, "Windows Win32-Skia", "Win32Skia",
            "CodeBrix.Platform.Runtime.Skia.Win32.ApacheLicenseForever", "UseWindowsWin32", "net10.0"),
        new PlatformHeadInfo(PlatformHead.WinWpfSkia, "Windows WPF-Skia", "WinWpfSkia",
            "CodeBrix.Platform.Runtime.Skia.Wpf.ApacheLicenseForever", "UseWindowsWpf", "net10.0-windows"),
    };

    /// <summary>Returns the info record for the given head.</summary>
    public static PlatformHeadInfo Get(PlatformHead head)
    {
        foreach (var info in All)
        {
            if (info.Head == head)
                return info;
        }
        throw new ArgumentOutOfRangeException(nameof(head), head, "Unknown platform head");
    }
}
