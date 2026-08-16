//
// RemoteDebugger.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// Where the debug adapter lives on an SSH device and how it is started
/// there. Debugging on a device is the IDE's own bundled netcoredbg, copied
/// to the device and run over an SSH exec channel with its DAP stdio piped
/// into the same client that drives a local session: the device needs the
/// .NET runtime and nothing else — no SDK, and no debugger of its own.
/// <para>
/// Everything here is a path or a command string, so the rules are testable
/// without a device on the other end.
/// </para>
/// </summary>
public static class RemoteDebugger
{
    /// <summary>The debug adapter's executable name, on the device as in the IDE's own output.</summary>
    public const string DebuggerFileName = "netcoredbg";

    /// <summary>
    /// The folder the CodeBrix.Develop.Debug package lands in, relative to the
    /// IDE's application directory. The whole folder goes to the device: the
    /// adapter loads its managed part and the runtime's debug shim from
    /// alongside itself.
    /// </summary>
    public const string BundleFolderName = "netcoredbg";

    /// <summary>
    /// The device folder the adapter is copied into, relative to the device
    /// user's home. It sits under a cache directory because it is entirely
    /// reproducible: deleting it costs one redeploy and nothing else.
    /// </summary>
    public const string RemoteDirectory = ".cache/codebrix-develop/netcoredbg";

    /// <summary>The adapter's full path on a device whose user's home is <paramref name="home"/>.</summary>
    public static string RemotePath(string home) => $"{home}/{RemoteDirectory}/{DebuggerFileName}";

    /// <summary>
    /// The read-only command reporting the device user's home directory.
    /// Needed in resolved form because the debug adapter starts the
    /// application itself, with no shell in between to expand <c>$HOME</c>.
    /// printf (unlike echo) adds no trailing newline of its own.
    /// </summary>
    public const string HomeDirectoryCommand = "printf %s \"$HOME\"";

    /// <summary>
    /// The command restoring the executable bit on files copied by SFTP,
    /// which transfers contents and not permissions. The adapter and the
    /// application's native launcher both have to be runnable.
    /// </summary>
    public static string MakeExecutableCommand(IEnumerable<string> paths)
    {
        var quoted = new List<string>();
        foreach (var path in paths)
            quoted.Add($"\"{path}\"");
        return $"chmod +x {string.Join(" ", quoted)}";
    }

    /// <summary>
    /// The command that runs the adapter on the device, speaking DAP on its
    /// standard input and output. It stays in the foreground of its own exec
    /// channel for the life of the session — the channel IS the debug
    /// connection, so nothing is backgrounded and no process id is needed:
    /// closing the channel ends the adapter, and the adapter ending closes
    /// the channel.
    /// <para>
    /// The environment is applied to the ADAPTER, which the application it
    /// starts inherits — belt to the launch request's own "env" braces. The
    /// runtime directory is also put on PATH, because an exec channel is not
    /// a login shell and a device's .NET lives in the user's home, nowhere a
    /// bare "dotnet" would be found. Only the shell sees that one: it is a
    /// composition of the existing PATH, which a launch request's flat
    /// environment map cannot express.
    /// </para>
    /// </summary>
    public static string StartCommand(string debuggerPath, string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var assignments = FrameBufferLaunchEnvironment.ToShellAssignments(environment);
        var path = "";
        foreach (var variable in environment)
        {
            if (variable.Key == FrameBufferLaunchEnvironment.DotnetRootVariable && !string.IsNullOrEmpty(variable.Value))
                path = $"PATH=\"{variable.Value}:$PATH\" ";
        }
        return $"cd \"{workingDirectory}\" && exec env {path}{assignments} \"{debuggerPath}\" --interpreter=vscode";
    }

    /// <summary>
    /// The runtime identifier of the debug adapter the IDE carries. The
    /// CodeBrix.Develop.Debug package is a single-architecture native carrier
    /// chosen at build time by the host's SDK, so the bundled adapter only
    /// runs on hardware of the IDE host's own architecture.
    /// </summary>
    public static string HostRuntimeIdentifier => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        Architecture.Arm => "linux-arm",
        Architecture.X86 => "linux-x86",
        _ => null,
    };

    /// <summary>
    /// Whether the bundled adapter can run on a device of the given runtime
    /// identifier. The application itself is always published for the DEVICE
    /// (<see cref="DeviceRid"/>), so a mismatch stops debugging alone — a
    /// plain run on a cross-architecture device still works.
    /// </summary>
    public static bool CanDebugDevice(string deviceRuntimeIdentifier) =>
        !string.IsNullOrEmpty(deviceRuntimeIdentifier)
        && string.Equals(deviceRuntimeIdentifier, HostRuntimeIdentifier, StringComparison.Ordinal);

    /// <summary>
    /// The explanation shown when the bundled adapter cannot run on the
    /// device, naming both architectures so the reason is obvious.
    /// </summary>
    public static string IncompatibleDeviceMessage(string deviceRuntimeIdentifier) =>
        $"This device is {deviceRuntimeIdentifier ?? "an unrecognized architecture"} and the IDE carries the " +
        $"{HostRuntimeIdentifier ?? "unknown"} debugger, so it cannot be debugged from here yet. " +
        "Running on the device works — only debugging needs a matching debugger.";
}
