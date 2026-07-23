//
// EnvironmentInfo.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBrix.Develop.Core.Options;

namespace CodeBrix.Develop.Core;

/// <summary>
/// Detects the operating system, CPU architecture, and (on Linux) the desktop
/// session type the application is running under, records them as hidden
/// options in options.sqlite — overwriting any previous values before anything
/// reads them — and reports them to the console at startup.
/// </summary>
public static class EnvironmentInfo
{
    /// <summary>
    /// Option key: the detected operating system — "linux", "windows",
    /// "macos", or "&lt;name&gt;-unsupported" for anything else.
    /// </summary>
    public const string OperatingSystemKey = "CodeBrix.Develop.Environment.CurrentOperatingSystem";

    /// <summary>Option key: the machine's CPU architecture as .NET reports it, lowercased.</summary>
    public const string OSArchitectureKey = "CodeBrix.Develop.Environment.CurrentOSArchitecture";

    /// <summary>Option key: the running process's CPU architecture as .NET reports it, lowercased.</summary>
    public const string ProcessArchitectureKey = "CodeBrix.Develop.Environment.CurrentProcessArchitecture";

    /// <summary>
    /// Option key: on Linux the value of $XDG_SESSION_TYPE (typically "x11" or
    /// "wayland"), or "unknown" when it cannot be read; empty on other systems.
    /// </summary>
    public const string DesktopSessionTypeKey = "CodeBrix.Develop.Environment.CurrentDesktopSessionType";

    /// <summary>The operating system recorded this run, or "" before detection has run.</summary>
    public static string CurrentOperatingSystem => Read(OperatingSystemKey);

    /// <summary>The desktop session type recorded this run, or "" before detection has run.</summary>
    public static string CurrentDesktopSessionType => Read(DesktopSessionTypeKey);

    static string Read(string key) =>
        PropertyService.IsInitialized ? PropertyService.Get(key, "") : "";

    /// <summary>
    /// Detects the environment, writes the four values into the application's
    /// options store (overwriting any previous values before they are read),
    /// then logs a formatted report. Call once at startup, immediately after
    /// <see cref="PropertyService.Initialize()"/>.
    /// </summary>
    public static void DetectStoreAndReport() => DetectStoreAndReport(PropertyService.Store);

    internal static void DetectStoreAndReport(OptionsStore store)
    {
        if (store == null)
            throw new ArgumentNullException(nameof(store));

        var operatingSystem = DetectOperatingSystem();
        var osArchitecture = DetectOSArchitecture();
        var processArchitecture = DetectProcessArchitecture();
        var desktopSessionType = DetectDesktopSessionType();

        // Overwrite-first: these are recorded before anything reads them.
        store.Set(OperatingSystemKey, operatingSystem);
        store.Set(OSArchitectureKey, osArchitecture);
        store.Set(ProcessArchitectureKey, processArchitecture);
        store.Set(DesktopSessionTypeKey, desktopSessionType);

        foreach (var line in BuildReportLines(operatingSystem, osArchitecture, processArchitecture, desktopSessionType))
            LoggingService.LogInfo(line);
    }

    /// <summary>Detects the operating system name (see <see cref="OperatingSystemKey"/>).</summary>
    public static string DetectOperatingSystem() => NormalizeOperatingSystem(
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        RuntimeInformation.OSDescription);

    /// <summary>The machine's CPU architecture as .NET reports it, lowercased (e.g. "x64", "arm64").</summary>
    public static string DetectOSArchitecture() =>
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The running process's CPU architecture as .NET reports it, lowercased.</summary>
    public static string DetectProcessArchitecture() =>
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>Detects the desktop session type (see <see cref="DesktopSessionTypeKey"/>).</summary>
    public static string DetectDesktopSessionType() =>
        NormalizeDesktopSessionType(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), SafeReadXdgSessionType());

    static string SafeReadXdgSessionType()
    {
        try
        {
            return Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        }
        catch
        {
            // Any failure reading the variable is treated as "not determinable".
            return null;
        }
    }

    internal static string NormalizeOperatingSystem(bool isLinux, bool isWindows, bool isMacOS, string osDescription)
    {
        if (isLinux)
            return "linux";
        if (isWindows)
            return "windows";
        if (isMacOS)
            return "macos";

        // An unrecognized platform is reported by the first token of its
        // description, lowercased, with an explicit "-unsupported" suffix.
        var token = (osDescription ?? string.Empty).Trim();
        var space = token.IndexOf(' ');
        if (space > 0)
            token = token.Substring(0, space);
        token = token.ToLowerInvariant();
        if (string.IsNullOrEmpty(token))
            token = "unknown";
        return token + "-unsupported";
    }

    internal static string NormalizeDesktopSessionType(bool isLinux, string xdgSessionType)
    {
        if (!isLinux)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(xdgSessionType))
            return "unknown";
        return xdgSessionType.Trim().ToLowerInvariant();
    }

    internal static IReadOnlyList<string> BuildReportLines(
        string operatingSystem, string osArchitecture, string processArchitecture, string desktopSessionType)
    {
        var lines = new List<string>
        {
            "Runtime environment:",
            FormatLine("Operating system", operatingSystem),
        };

        // A single "Architecture" line when the machine and process agree;
        // otherwise the two are reported separately (e.g. an x64 process on an
        // arm64 machine running under emulation).
        if (string.Equals(osArchitecture, processArchitecture, StringComparison.Ordinal))
        {
            lines.Add(FormatLine("Architecture", osArchitecture));
        }
        else
        {
            lines.Add(FormatLine("OS architecture", osArchitecture));
            lines.Add(FormatLine("Process architecture", processArchitecture));
        }

        // Reported only when there is one to report (never on Windows/macOS).
        if (!string.IsNullOrEmpty(desktopSessionType))
            lines.Add(FormatLine("Desktop session type", desktopSessionType));

        return lines;
    }

    static string FormatLine(string label, string value) => $"  {label + ":",-22}{value}";
}
