//
// DeviceLaunch.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CodeBrix.Develop.Core;
using CodeBrix.SSH;

namespace CodeBrix.Develop.Ide.Remote;

/// <summary>
/// Deploys a published application to an SSH device and launches it there.
/// Deploy is an incremental SFTP sync (only files whose size or timestamp
/// changed); launch runs the app on the device's framebuffer over an SSH exec
/// channel, streaming its output and killing it on stop. Everything lives
/// under the device user's home — nothing needs root.
/// </summary>
public static class DeviceLaunch
{
    // Home-relative folder the app is deployed into; also the launch cwd.
    // Matches the "$HOME/apps/<App>" the launch command uses.
    const string AppsSubdirectory = "apps";

    /// <summary>The home-relative deploy folder for an app of the given name.</summary>
    public static string RemoteAppDirectory(string appName) => $"{AppsSubdirectory}/{appName}";

    /// <summary>
    /// Incrementally uploads the publish output to the device, creating remote
    /// folders as needed and skipping files whose size and timestamp already
    /// match. Returns the number of files uploaded.
    /// </summary>
    public static int Deploy(SftpClient sftp, string localPublishDir, string remoteAppDir, Action<string> onLog)
    {
        EnsureRemoteDirectory(sftp, remoteAppDir);
        var uploaded = 0;
        foreach (var localPath in Directory.GetFiles(localPublishDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(localPublishDir, localPath).Replace('\\', '/');
            var remotePath = $"{remoteAppDir}/{relative}";
            EnsureRemoteDirectory(sftp, PosixDirectoryOf(remotePath));

            var local = new FileInfo(localPath);
            if (!NeedsUpload(sftp, remotePath, local))
                continue;

            using var stream = File.OpenRead(localPath);
            try
            {
                sftp.UploadFile(stream, remotePath, canOverride: true);
            }
            catch (Exception ex)
            {
                // SFTP reports most write failures as a bare "Failure", which
                // says nothing about which of the publish output's files gave
                // up. Name it, and name the usual causes.
                throw new IOException(
                    $"Could not upload '{relative}' to ~/{remotePath} on the device: {ex.Message}. " +
                    "The device may be out of disk space, the file may still be held by a running " +
                    "instance of the app (press Stop first), or it may not be writable by the device user.",
                    ex);
            }
            uploaded++;
            onLog?.Invoke($"  ↑ {relative}");
        }
        return uploaded;
    }

    static bool NeedsUpload(SftpClient sftp, string remotePath, FileInfo local)
    {
        if (!sftp.Exists(remotePath))
            return true;
        try
        {
            var attributes = sftp.GetAttributes(remotePath);
            // Rebuilt files carry a newer local timestamp than the last upload;
            // unchanged framework/native files match on size and are skipped.
            return attributes.Size != local.Length || local.LastWriteTimeUtc > attributes.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return true; // if we cannot tell, upload
        }
    }

    static void EnsureRemoteDirectory(SftpClient sftp, string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir) || remoteDir == "." || remoteDir == "/")
            return;
        if (sftp.Exists(remoteDir))
            return;
        EnsureRemoteDirectory(sftp, PosixDirectoryOf(remoteDir));
        try
        {
            sftp.CreateDirectory(remoteDir);
        }
        catch (Exception)
        {
            // A concurrent creator, or it appeared between Exists and Create.
        }
    }

    static string PosixDirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "" : path.Substring(0, slash);
    }

    /// <summary>
    /// Launches the deployed app on the device's framebuffer and returns a
    /// handle streaming its output. Forces the software /dev/fb0 renderer
    /// (DRM master is never available over SSH) and runs dotnet by full path
    /// (~/.dotnet is off the non-login PATH). A nonzero
    /// <paramref name="touchRotationDegrees"/> (180 for a device whose touch
    /// digitizer is mounted upside-down, like the WinBook TW700) is passed to
    /// the head so it corrects touch positions.
    /// <paramref name="orientationEnabled"/> reflects the connect dialog's
    /// "Enable Orientation Changes": true lets the app listen for the IDE's
    /// orientation instructions, false disables the app's orientation sources
    /// entirely — either way the device's own accelerometer is ignored during
    /// testing, even for apps that declared UseOrientationSensor.
    /// </summary>
    public static RemoteApplication Run(
        SshTerminalSession session, string remoteAppDir, string entryDll, Action<string> onLog,
        int touchRotationDegrees = 0, bool orientationEnabled = false)
    {
        var orientationSource = orientationEnabled ? "develop" : "none";
        var environment = "DOTNET_ROOT=\"$HOME/.dotnet\" CODEBRIX_FRAMEBUFFER_USE_DRM=0"
            + $" CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE={orientationSource}";
        if (touchRotationDegrees != 0)
            environment += $" CODEBRIX_FRAMEBUFFER_TOUCH_ROTATION={touchRotationDegrees}";

        // Launch in the background, print the PID on a marker line so stop can
        // kill it precisely, then wait so the channel stays open for the app's
        // lifetime.
        //
        // The subshell and the exec matter. Backgrounding a "cd X && app" list
        // forks a subshell for the WHOLE list, so a bare $! is the SUBSHELL's
        // pid, not the app's: stopping killed the subshell, reported its 143,
        // and left the app running on the device's screen. Exec replaces the
        // subshell with the app itself, so $! is the app, kill reaches the app,
        // and the exit code reported is the app's own.
        var command = string.Join(" ",
            $"( cd \"$HOME/{remoteAppDir}\" &&",
            $"exec env {environment}",
            $"\"$HOME/.dotnet/dotnet\" \"{entryDll}\" ) &",
            "__cbpid=$!;",
            $"echo \"{RemoteApplication.PidMarker} $__cbpid\";",
            "wait $__cbpid");
        return new RemoteApplication(session, command, onLog);
    }

    /// <summary>
    /// The command broadcasting a device-orientation instruction on the
    /// device's D-Bus system bus. The head listens for exactly this signal
    /// when launched with CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE=develop
    /// (which <see cref="Run"/> always sets) and applies it through the same
    /// gate as any rotation source, so an app that locked its orientation
    /// refuses it. Broadcast signals need no bus policy or name ownership —
    /// nothing is provisioned — and dbus-send ships with the dbus package
    /// systemd-logind already requires.
    /// </summary>
    /// <param name="orientation">A DisplayOrientations member name: Landscape,
    /// Portrait, LandscapeFlipped or PortraitFlipped.</param>
    public static string SendOrientationCommand(string orientation) =>
        "dbus-send --system --type=signal /com/codebrix/platform/FrameBuffer " +
        $"com.codebrix.platform.FrameBuffer.DeviceOrientation string:{orientation}";
}

/// <summary>
/// A running application on the device: streams stdout/stderr, captures the
/// remote process id from a marker line, and stops the app by killing that
/// process. Raised events arrive on background threads.
/// </summary>
public sealed class RemoteApplication : IDisposable
{
    internal const string PidMarker = "__CODEBRIX_APP_PID__";

    readonly SshTerminalSession session;
    readonly Action<string> onLog;
    readonly SshCommand command;
    int? remotePid;
    volatile bool stopping;
    volatile bool disposed;

    internal RemoteApplication(SshTerminalSession session, string commandText, Action<string> onLog)
    {
        this.session = session;
        this.onLog = onLog;
        command = session.CreateCommand(commandText);
        var async = command.BeginExecute();

        StartReader(command.OutputStream, isError: false);
        StartReader(command.ExtendedOutputStream, isError: true);

        var waiter = new Thread(() =>
        {
            try
            {
                command.EndExecute(async);
            }
            catch (Exception)
            {
                // Channel torn down (stop / disconnect); treated as an exit.
            }
            Exited?.Invoke(command.ExitStatus);
        })
        {
            IsBackground = true,
            Name = "ssh-app-wait",
        };
        waiter.Start();
    }

    /// <summary>Raised once when the app exits (or is stopped); the exit code, or null.</summary>
    public event Action<int?>? Exited;

    void StartReader(Stream stream, bool isError)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!isError && TryCapturePid(line))
                        continue; // the marker line is ours, not app output
                    onLog?.Invoke(line);
                }
            }
            catch (Exception)
            {
                // Stream closed with the channel; nothing more to read.
            }
        })
        {
            IsBackground = true,
            Name = isError ? "ssh-app-stderr" : "ssh-app-stdout",
        };
        thread.Start();
    }

    bool TryCapturePid(string line)
    {
        if (!line.StartsWith(PidMarker, StringComparison.Ordinal))
            return false;
        var rest = line.Substring(PidMarker.Length).Trim();
        if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            remotePid = pid;
        return true;
    }

    // Reported by the stop command so the IDE knows whether the app really
    // died rather than assuming it did.
    const string StoppedMarker = "__CODEBRIX_APP_STOPPED__";

    // Blanks the device's screen to black. /dev/fb0 is writable by the video
    // group the device user is already in, so this needs no sudo. dd stops at
    // the end of the framebuffer with a write error; that is the expected end
    // of the copy, not a failure.
    const string BlankFrameBufferCommand = "dd if=/dev/zero of=/dev/fb0 bs=1M 2>/dev/null; true";

    /// <summary>
    /// Stops the app: TERM the remote process, escalate to KILL if it lingers,
    /// then confirm it is really gone before reporting — and blank the device's
    /// screen so a stopped application cannot leave a live-looking picture on
    /// it. The channel closes on its own once the process is dead.
    /// </summary>
    public void Stop()
    {
        if (stopping)
            return;
        stopping = true;
        if (remotePid is int pid)
        {
            try
            {
                // TERM, wait up to ~3s for a clean exit, KILL, then report
                // which of the two actually finished it.
                var stop = string.Join(" ",
                    $"kill -TERM {pid} 2>/dev/null;",
                    $"for _ in $(seq 1 10); do kill -0 {pid} 2>/dev/null || break; sleep 0.3; done;",
                    $"kill -0 {pid} 2>/dev/null && kill -KILL {pid} 2>/dev/null;",
                    "sleep 0.3;",
                    $"kill -0 {pid} 2>/dev/null && echo \"{StoppedMarker} no\" || echo \"{StoppedMarker} yes\";");
                var output = session.RunCommand(stop).Output ?? "";
                if (output.Contains($"{StoppedMarker} yes", StringComparison.Ordinal))
                {
                    // Only blank once the app is confirmed gone — blanking a
                    // screen the app is still drawing to proves nothing.
                    session.RunCommand(BlankFrameBufferCommand);
                }
                else
                {
                    LoggingService.LogWarning(
                        $"The application (pid {pid}) did not stop on the device; it may still be running on its screen.");
                    onLog?.Invoke($"Warning: the application (pid {pid}) did not stop on the device.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogInfo($"Stopping the remote app: {ex.Message}");
            }
        }
        try
        {
            command.CancelAsync();
        }
        catch (Exception)
        {
            // Already finishing.
        }
    }

    /// <summary>Stops and releases the command.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Stop();
        try
        {
            command.Dispose();
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }
}
