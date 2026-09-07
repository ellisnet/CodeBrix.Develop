//
// LaunchCapability.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// Whether a project can be debugged, and when it cannot, why.
/// </summary>
public sealed class DebugCapability
{
    DebugCapability(bool canDebug, string reason)
    {
        CanDebug = canDebug;
        Reason = reason ?? "";
    }

    /// <summary>Whether a debug session can be started for the project.</summary>
    public bool CanDebug { get; }

    /// <summary>
    /// Why debugging is unavailable, phrased for the user; "" when it is
    /// available. Shown on the Debug button and in the status bar, because a
    /// button that is disabled with no explanation reads as a broken IDE.
    /// </summary>
    public string Reason { get; }

    /// <summary>Debugging is available; there is nothing to explain.</summary>
    public static readonly DebugCapability Supported = new DebugCapability(true, "");

    internal static DebugCapability No(string reason) => new DebugCapability(false, reason);
}

/// <summary>
/// What the IDE can do with a project, decided from its target framework.
/// </summary>
/// <remarks>
/// THE ONE PLACE that decides whether Debug is offered. When Android debugging
/// is implemented, the Android arm below changes here and the toolbar, the
/// menu and the keyboard shortcut all follow — there is nothing else to find.
/// </remarks>
public static class LaunchCapability
{
    /// <summary>
    /// The first .NET version whose Android apps run on CoreCLR by default.
    /// Before it, an Android app runs on MonoVM, whose debugger speaks the
    /// Mono soft-debugger protocol — a protocol this IDE has no client for,
    /// and one being retired, so no client is planned.
    /// </summary>
    public const int FirstCoreClrAndroidDotNetVersion = 11;

    /// <summary>
    /// Whether the given project can be debugged, and why not when it cannot.
    /// </summary>
    public static DebugCapability Debugging(DotNetProject project)
    {
        if (project == null)
            return DebugCapability.No("No project is selected.");
        if (!project.IsAndroidProject)
            return DebugCapability.Supported;

        var framework = project.TargetFramework;
        if (framework == null || framework.FrameworkVersion == null)
            return DebugCapability.No(
                "This Android project's target framework could not be read, so the IDE cannot tell "
                + "which runtime it uses.");

        if (framework.FrameworkVersion.Major < FirstCoreClrAndroidDotNetVersion)
            return DebugCapability.No(
                $"Android apps on .NET {framework.FrameworkVersionText} run on MonoVM, which this "
                + "debugger cannot attach to. Target .NET "
                + $"{FirstCoreClrAndroidDotNetVersion}.0 or later for a debuggable Android app.");

        // .NET 11 and later: the app runs on CoreCLR and exposes the .NET
        // diagnostics channel, which is the transport a debugger would use.
        // The transport is verified; the debugger that drives it is not
        // written yet, so the honest answer is still no.
        return DebugCapability.No(
            "Debugging Android apps is not implemented yet. The app runs on CoreCLR and its "
            + "diagnostics channel is reachable, but the IDE has no debugger for it.");
    }
}
