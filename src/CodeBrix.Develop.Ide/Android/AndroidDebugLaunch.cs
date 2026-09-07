//
// AndroidDebugLaunch.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the one-click Android debug pipeline: place the debugger, launch the
//      app, start the adapter, forward the port, attach)
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Android;
using CodeBrix.Develop.Core.Debugging;
using CodeBrix.Develop.Core.Projects;
using CodeBrix.Develop.Ide.Debugging;

namespace CodeBrix.Develop.Ide.Android;

/// <summary>
/// Everything between "the app is installed on the device" and "the IDE is
/// attached to it": place the on-device debugger in the app's sandbox if it
/// changed, mark the app as the debug app, launch it fresh, find its process
/// and its runtime libraries, start the debugger in DAP server mode, forward
/// its port, connect, and attach.
/// <para>
/// The BUILD and INSTALL are not here. They belong to the workbench, which
/// owns the build service and the per-solution SDK the Android targets need.
/// </para>
/// <para>
/// The rules this follows — paths, command lines, parsers — all live in
/// <see cref="AndroidDebugger"/>, where they are unit-tested without a
/// device. What is left here is the ORDER of the steps and what to do when
/// one of them fails.
/// </para>
/// </summary>
public static class AndroidDebugLaunch
{
    /// <summary>
    /// How long the DAP configurationDone request is given. The real attach
    /// — the runtime handshake with the app — happens inside it and takes
    /// several seconds on a phone, so this is generous on purpose.
    /// </summary>
    public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to keep trying to connect to the forwarded port. The debugger
    /// is started in the background on the device, so the first attempts are
    /// expected to be refused.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    // How long to wait for the launched app to appear in "pidof". A cold
    // start of a Debug app on a phone is a few seconds; the poll costs
    // nothing while it waits.
    static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan ProcessPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Starts a debug session for an already-installed Android app on the
    /// given device and returns the handle its cleanup is done through.
    /// The debug session itself is registered with
    /// <see cref="DebugService"/>, exactly as a local or SSH-device session
    /// would be, so everything above this point behaves identically.
    /// <para>
    /// If the attach does not complete, ONE retry is made on a genuinely
    /// fresh app process: the usual cause is a process the runtime has
    /// already handed to something else (a stale debug-app marking, an app
    /// that was running before it was marked), and a fresh launch is the
    /// documented cure. Every failure path cleans up after itself — the
    /// on-device debugger is killed, the port forward withdrawn and the
    /// debug-app marking cleared — before throwing with a message that says
    /// which step gave up.
    /// </para>
    /// </summary>
    public static async Task<AndroidDebugSession> StartAsync(DotNetProject project, string serial,
        Action<string> onLog, CancellationToken cancellationToken = default)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(serial))
            throw new ArgumentException("A device serial is required to debug on a device.", nameof(serial));

        var package = project.ApplicationId;
        if (string.IsNullOrWhiteSpace(package))
        {
            throw new InvalidOperationException(
                $"{project.Name} declares no ApplicationId, so the IDE cannot tell which package to debug.");
        }

        // These three come before the guarded section deliberately: nothing
        // they do needs undoing. Reading a property changes nothing, and the
        // payload they place is stamped and REUSED by the next session — the
        // one thing a failed start must not throw away.
        var abi = await ReadAbiAsync(serial, onLog, cancellationToken).ConfigureAwait(false);
        var payloadDirectory = RequirePayloadDirectory(abi);
        await EnsurePayloadAsync(serial, package, payloadDirectory, onLog, cancellationToken).ConfigureAwait(false);

        var hostPort = AndroidDebugger.FindFreeHostPort();
        try
        {
            // BEFORE the launch, always: applied to an app that is already
            // running, set-debug-app restarts it, which would orphan the very
            // process the session then attached to.
            await RequireAsync(serial, AndroidDebugger.SetDebugAppCommand(package),
                "mark the app as the debug app", cancellationToken).ConfigureAwait(false);

            var process = await LaunchFreshAsync(serial, package, onLog, cancellationToken).ConfigureAwait(false);
            try
            {
                await AttachAsync(serial, package, abi, process, hostPort, onLog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoggingService.LogWarning($"The first Android attach failed: {ex.Message}");
                onLog($"Attaching failed ({ex.Message}). Retrying once on a fresh app process…");
                await CleanUpDeviceAsync(serial, package, hostPort,
                    clearDebugApp: false, forceStop: false).ConfigureAwait(false);
                process = await LaunchFreshAsync(serial, package, onLog, cancellationToken).ConfigureAwait(false);
                await AttachAsync(serial, package, abi, process, hostPort, onLog, cancellationToken).ConfigureAwait(false);
            }
            return new AndroidDebugSession(serial, package, hostPort, process.Pid);
        }
        catch (Exception ex)
        {
            await CleanUpDeviceAsync(serial, package, hostPort,
                clearDebugApp: true, forceStop: false).ConfigureAwait(false);
            if (ex is OperationCanceledException)
                throw;
            throw new InvalidOperationException(
                $"Debugging {package} on {serial} could not be started: {ex.Message}", ex);
        }
    }

    static async Task<string> ReadAbiAsync(string serial, Action<string> onLog, CancellationToken cancellationToken)
    {
        var result = await AndroidDebugBridge.ShellAsync(serial, "getprop ro.product.cpu.abi", cancellationToken)
            .ConfigureAwait(false);
        var abi = AndroidDebugger.ParseAbi(result.StandardOutput);
        if (string.IsNullOrEmpty(abi))
            throw new InvalidOperationException($"The device {serial} did not report an ABI. {Describe(result)}");
        onLog($"Device {serial} is {abi}.");
        return abi;
    }

    static string RequirePayloadDirectory(string abi)
    {
        if (!AndroidDebugger.IsSupportedAbi(abi))
            throw new InvalidOperationException(AndroidDebugger.PayloadMissingMessage(abi));

        var directory = AndroidDebugger.LocalPayloadDirectory(abi);
        if (!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0)
            throw new InvalidOperationException(AndroidDebugger.PayloadMissingMessage(abi));
        return directory;
    }

    /// <summary>
    /// Puts the debugger in the app's sandbox when what is there is not what
    /// the IDE carries. The comparison is a SHA-256 stamp over the payload,
    /// written beside it on the device, so the push happens once per device
    /// and then never again — and happens automatically when the payload
    /// itself changes.
    /// </summary>
    static async Task EnsurePayloadAsync(string serial, string package, string payloadDirectory,
        Action<string> onLog, CancellationToken cancellationToken)
    {
        var stamp = AndroidDebugger.ComputePayloadStamp(payloadDirectory);
        var existing = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ReadStampCommand(package),
            cancellationToken).ConfigureAwait(false);
        if (existing.Succeeded
            && string.Equals(existing.StandardOutput.Trim(), stamp, StringComparison.Ordinal))
        {
            onLog("The on-device debugger is already up to date.");
            return;
        }

        var files = Directory.GetFiles(payloadDirectory)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
        onLog($"Placing the on-device debugger ({files.Count} file(s)) in {package}…");

        await RequireAsync(serial, $"mkdir -p {AndroidDebugger.StagingDirectory}",
            "create the staging folder on the device", cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            var remote = $"{AndroidDebugger.StagingDirectory}/{Path.GetFileName(file)}";
            var push = await AndroidDebugBridge.PushAsync(serial, file, remote, cancellationToken).ConfigureAwait(false);
            if (!push.Succeeded)
                throw new InvalidOperationException($"Could not copy {Path.GetFileName(file)} to the device. {Describe(push)}");
        }
        // adb push leaves files readable only by the shell user; the app has
        // to be able to read them out of the staging folder.
        await RequireAsync(serial, $"chmod 644 {AndroidDebugger.StagingDirectory}/*",
            "make the staged debugger readable", cancellationToken).ConfigureAwait(false);
        await RequireAsync(serial, AndroidDebugger.InstallPayloadCommand(package),
            $"copy the debugger into {package}'s sandbox (the app must be a DEBUG build for run-as to work)",
            cancellationToken).ConfigureAwait(false);
        await RequireAsync(serial, AndroidDebugger.WriteStampCommand(package, stamp),
            "record the debugger version on the device", cancellationToken).ConfigureAwait(false);
        onLog($"Placed the debugger in {AndroidDebugger.DebuggerDirectory(package)}.");
    }

    /// <summary>
    /// Stops the app, starts it again from its own launcher activity, and
    /// reports the new process together with the directory its CoreCLR was
    /// loaded from. A FRESH process is not a nicety: the runtime answers a
    /// debugger's handshake with a resync rather than an accept when the
    /// process was already running before it was marked for debugging.
    /// </summary>
    static async Task<LaunchedProcess> LaunchFreshAsync(string serial, string package, Action<string> onLog,
        CancellationToken cancellationToken)
    {
        await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ForceStopCommand(package), cancellationToken)
            .ConfigureAwait(false);

        var resolved = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ResolveActivityCommand(package),
            cancellationToken).ConfigureAwait(false);
        var component = AndroidDebugger.ParseResolvedActivity(resolved.CombinedOutput);
        if (component == null)
        {
            throw new InvalidOperationException(
                $"The device could not name a launcher activity for {package}; is it installed? {Describe(resolved)}");
        }

        onLog($"Starting {component}…");
        var started = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.StartActivityCommand(component),
            cancellationToken).ConfigureAwait(false);
        if (!started.Succeeded)
            throw new InvalidOperationException($"{component} could not be started. {Describe(started)}");

        var pid = await WaitForProcessAsync(serial, package, cancellationToken).ConfigureAwait(false);

        var maps = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ReadMapsCommand(package, pid),
            cancellationToken).ConfigureAwait(false);
        var libraryDirectory = AndroidDebugger.ParseLibraryDirectory(maps.CombinedOutput);
        if (libraryDirectory == null)
        {
            throw new InvalidOperationException(
                $"{package} (process {pid}) has no CoreCLR mapped, so there is nothing for the debugger to " +
                "attach to. An Android app runs on CoreCLR from .NET 11 onwards; before that it runs on " +
                "MonoVM, which this debugger cannot attach to.");
        }

        onLog($"Fresh process {pid}; its runtime libraries are in {libraryDirectory}.");
        return new LaunchedProcess(pid, libraryDirectory);
    }

    static async Task<int> WaitForProcessAsync(string serial, string package, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ProcessTimeout;
        while (true)
        {
            var result = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.PidOfCommand(package),
                cancellationToken).ConfigureAwait(false);
            if (AndroidDebugger.ParsePid(result.StandardOutput) is int pid)
                return pid;
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"{package} did not start on {serial} within {ProcessTimeout.TotalSeconds:F0} seconds.");
            }
            await Task.Delay(ProcessPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the debugger on the device in DAP server mode, publishes its
    /// port on this machine, connects, and runs the DAP attach. On success
    /// the session is live and registered with <see cref="DebugService"/>.
    /// </summary>
    static async Task AttachAsync(string serial, string package, string abi, LaunchedProcess process, int hostPort,
        Action<string> onLog, CancellationToken cancellationToken)
    {
        // A debugger orphaned by an earlier session still holds the device
        // port, and would answer the connection instead of the new one.
        await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.KillDebuggerCommand(package), cancellationToken)
            .ConfigureAwait(false);

        var devicePort = AndroidDebugger.DefaultDevicePort;
        onLog($"Starting the on-device debugger on port {devicePort}…");
        var server = await AndroidDebugBridge.ShellAsync(serial,
            AndroidDebugger.StartServerCommand(package, abi, process.LibraryDirectory, devicePort),
            cancellationToken).ConfigureAwait(false);
        if (!server.Succeeded)
            throw new InvalidOperationException($"The on-device debugger could not be started. {Describe(server)}");

        // Do not connect until the device says the debugger is listening. adb
        // forward accepts the host side of a connection BEFORE it tries the
        // device side, so a connect succeeds even when nothing is listening
        // yet, and the first DAP request then finds the connection closed.
        await WaitForServerAsync(serial, package, devicePort, cancellationToken).ConfigureAwait(false);

        var forward = await AndroidDebugBridge.ForwardAsync(serial, hostPort, devicePort, cancellationToken)
            .ConfigureAwait(false);
        if (!forward.Succeeded)
        {
            throw new InvalidOperationException(
                $"adb could not forward tcp:{hostPort} to the device's tcp:{devicePort}. {Describe(forward)}");
        }

        onLog($"Connecting to the debugger through 127.0.0.1:{hostPort}…");
        var transport = await TcpDebuggerTransport.ConnectAsync(hostPort, ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            onLog($"Attaching to process {process.Pid}…");
            await DebugService.StartAttachedAsync(transport, process.Pid, AttachTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The attach never completed, so nothing owns the transport.
            transport.Dispose();
            throw;
        }
        onLog($"Attached to {package} (process {process.Pid}) on {serial}.");
    }

    // How long the on-device debugger is given to come up and listen. It is
    // a small native process; on a phone it is listening well under a second
    // after the shell returns.
    static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan ServerPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Waits until the debugger process exists in the app's sandbox AND its
    /// port shows as LISTEN in the kernel's socket table. A debugger that
    /// dies instead (a missing library, a bad option) is reported with its
    /// own output, which is the only place it says why.
    /// </summary>
    static async Task WaitForServerAsync(string serial, string package, int devicePort, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ServerStartTimeout;
        var seenProcess = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pid = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.PidOfDebuggerCommand(package),
                cancellationToken).ConfigureAwait(false);
            var running = AndroidDebugger.ParsePid(pid.StandardOutput) != null;
            if (running)
            {
                seenProcess = true;
                var sockets = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ListeningSocketsCommand,
                    cancellationToken).ConfigureAwait(false);
                if (AndroidDebugger.IsPortListening(sockets.StandardOutput, devicePort))
                    return;
            }
            else if (seenProcess)
            {
                // It was up and is gone: it exited. Its output says why.
                throw new InvalidOperationException(
                    $"The on-device debugger exited before it started listening. {await DebuggerOutputAsync(serial, package).ConfigureAwait(false)}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"The on-device debugger did not start listening on port {devicePort} within " +
                    $"{ServerStartTimeout.TotalSeconds:F0} seconds. {await DebuggerOutputAsync(serial, package).ConfigureAwait(false)}");
            }
            await Task.Delay(ServerPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<string> DebuggerOutputAsync(string serial, string package)
    {
        try
        {
            var output = await AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ReadDebuggerOutputCommand(package))
                .ConfigureAwait(false);
            var text = output.CombinedOutput.Trim();
            return text.Length == 0 ? "The debugger wrote no output." : $"The debugger said: {text}";
        }
        catch (Exception)
        {
            return "The debugger's output could not be read.";
        }
    }

    /// <summary>
    /// The device side of ending a session, and of giving up on starting
    /// one. Every step is best-effort and independent: a device that has been
    /// unplugged cannot be tidied, and saying so once in the log is the whole
    /// of the correct response.
    /// </summary>
    internal static async Task CleanUpDeviceAsync(string serial, string package, int hostPort,
        bool clearDebugApp, bool forceStop)
    {
        await BestEffortAsync(AndroidDebugger.KillDebuggerCommand(package),
            () => AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.KillDebuggerCommand(package)))
            .ConfigureAwait(false);
        await BestEffortAsync($"adb forward --remove tcp:{hostPort}",
            () => AndroidDebugBridge.RemoveForwardAsync(serial, hostPort)).ConfigureAwait(false);
        if (clearDebugApp)
        {
            await BestEffortAsync(AndroidDebugger.ClearDebugAppCommand,
                () => AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ClearDebugAppCommand)).ConfigureAwait(false);
        }
        if (forceStop)
        {
            await BestEffortAsync(AndroidDebugger.ForceStopCommand(package),
                () => AndroidDebugBridge.ShellAsync(serial, AndroidDebugger.ForceStopCommand(package))).ConfigureAwait(false);
        }
    }

    static async Task BestEffortAsync(string description, Func<Task<AdbResult>> command)
    {
        try
        {
            await command().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogInfo($"Cleaning up the Android debug session ({description}): {ex.Message}");
        }
    }

    // Runs a device command that the pipeline cannot continue without.
    static async Task RequireAsync(string serial, string command, string what, CancellationToken cancellationToken)
    {
        var result = await AndroidDebugBridge.ShellAsync(serial, command, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not {what}. {Describe(result)}");
    }

    // What a failed adb command said, in one line, or a note that it said
    // nothing at all (which an "am" command failing silently really does).
    static string Describe(AdbResult result)
    {
        var text = result.CombinedOutput.Trim();
        return text.Length == 0
            ? $"adb exited with code {result.ExitCode} and said nothing."
            : $"adb exited with code {result.ExitCode}: {text}";
    }

    // A freshly launched app process and where its runtime came from.
    sealed class LaunchedProcess
    {
        internal LaunchedProcess(int pid, string libraryDirectory)
        {
            Pid = pid;
            LibraryDirectory = libraryDirectory;
        }

        internal int Pid { get; }

        internal string LibraryDirectory { get; }
    }
}

/// <summary>
/// A live Android debug session's device side: which device, which package,
/// which host port the forward claims, and which process the debugger is
/// attached to. The DAP session itself belongs to
/// <see cref="DebugService"/>; this is what has to be undone ON THE DEVICE
/// when that session ends.
/// </summary>
public sealed class AndroidDebugSession : IDisposable
{
    int cleanedUp;

    internal AndroidDebugSession(string serial, string packageName, int hostPort, int processId)
    {
        Serial = serial;
        PackageName = packageName;
        HostPort = hostPort;
        ProcessId = processId;
    }

    /// <summary>The device the session runs on.</summary>
    public string Serial { get; }

    /// <summary>The Android package being debugged.</summary>
    public string PackageName { get; }

    /// <summary>The loopback port the adb forward publishes the debugger on.</summary>
    public int HostPort { get; }

    /// <summary>The app process the debugger is attached to.</summary>
    public int ProcessId { get; }

    /// <summary>
    /// Undoes everything the launch did on the device: kills the on-device
    /// debugger (belt and braces after a DAP disconnect), withdraws the port
    /// forward, clears the persistent debug-app marking — which would
    /// otherwise outlive the IDE and change how the app behaves next time the
    /// user starts it — and stops the app, because Stop means stop, exactly
    /// as it does for a plain Android run. Safe to call more than once; only
    /// the first call does anything.
    /// </summary>
    public Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref cleanedUp, 1) != 0)
            return Task.CompletedTask;
        return AndroidDebugLaunch.CleanUpDeviceAsync(Serial, PackageName, HostPort,
            clearDebugApp: true, forceStop: true);
    }

    /// <summary>
    /// Starts the cleanup without waiting for it. The device round trips take
    /// seconds and Dispose is called from teardown paths (session ended,
    /// application closing) that must not block on a phone; failures are
    /// logged by the cleanup itself.
    /// </summary>
    public void Dispose() => _ = CleanupAsync();
}
