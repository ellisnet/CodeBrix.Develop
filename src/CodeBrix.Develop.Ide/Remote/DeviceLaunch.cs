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
            sftp.UploadFile(stream, remotePath, canOverride: true);
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
    /// (~/.dotnet is off the non-login PATH).
    /// </summary>
    public static RemoteApplication Run(
        SshTerminalSession session, string remoteAppDir, string entryDll, Action<string> onLog)
    {
        // Launch in the background, print the PID on a marker line so stop can
        // kill it precisely, then wait so the channel stays open for the app's
        // lifetime.
        var command = string.Join(" ",
            $"cd \"$HOME/{remoteAppDir}\" &&",
            "DOTNET_ROOT=\"$HOME/.dotnet\"",
            "CODEBRIX_FRAMEBUFFER_USE_DRM=0",
            $"\"$HOME/.dotnet/dotnet\" \"{entryDll}\" &",
            "__cbpid=$!;",
            $"echo \"{RemoteApplication.PidMarker} $__cbpid\";",
            "wait $__cbpid");
        return new RemoteApplication(session, command, onLog);
    }
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

    /// <summary>
    /// Stops the app: kills the remote process (TERM, then KILL after a
    /// grace), which lets the channel close on its own.
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
                session.RunCommand($"kill {pid} 2>/dev/null; sleep 1; kill -9 {pid} 2>/dev/null; true");
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
