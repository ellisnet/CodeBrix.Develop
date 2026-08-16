//
// SshDebuggerTransport.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Text;
using System.Threading;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Debugging;
using CodeBrix.SSH;

namespace CodeBrix.Develop.Ide.Remote;

/// <summary>
/// The debug adapter running on an SSH device, reached over a raw exec
/// channel: its standard output and input carry DAP straight into the IDE's
/// ordinary <see cref="DapClient"/>, so a device session behaves exactly like
/// a local one. The channel has no PTY, which is what keeps the protocol's
/// byte framing intact.
/// <para>
/// The adapter's standard error is not part of the protocol — it is where a
/// debugger that cannot start says why (a missing binary, a missing runtime),
/// so it is reported rather than dropped.
/// </para>
/// </summary>
public sealed class SshDebuggerTransport : IDebuggerTransport
{
    readonly SshCommand command;
    readonly Stream toAdapter;
    readonly Action<string>? onDiagnostic;
    volatile bool disposed;

    /// <summary>
    /// Starts the adapter on the device and opens its streams. Throws when
    /// the channel cannot be opened; a command that starts and immediately
    /// fails instead reports itself through
    /// <paramref name="onDiagnostic"/> and by closing the connection.
    /// </summary>
    public SshDebuggerTransport(SshTerminalSession session, string commandText, Action<string>? onDiagnostic = null)
    {
        this.onDiagnostic = onDiagnostic;
        command = session.CreateCommand(commandText);
        // BeginExecute opens the channel before it returns, which is exactly
        // what CreateInputStream requires ("only during execution").
        var execution = command.BeginExecute();
        toAdapter = command.CreateInputStream();

        StartErrorReader();
        StartExitWatcher(execution);
    }

    /// <inheritdoc/>
    public Stream Input => command.OutputStream;

    /// <inheritdoc/>
    public Stream Output => toAdapter;

    void StartErrorReader()
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(command.ExtendedOutputStream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    LoggingService.LogWarning($"netcoredbg (device): {line}");
                    onDiagnostic?.Invoke($"netcoredbg: {line}");
                }
            }
            catch (Exception)
            {
                // The channel closed; there is nothing further to read.
            }
        })
        {
            IsBackground = true,
            Name = "ssh-debugger-stderr",
        };
        thread.Start();
    }

    void StartExitWatcher(IAsyncResult execution)
    {
        var thread = new Thread(() =>
        {
            try
            {
                command.EndExecute(execution);
            }
            catch (Exception)
            {
                // Channel torn down by a stop or a disconnect; that is an exit.
            }
            if (disposed)
                return;
            // A debug adapter that was never reached at all (no such file,
            // not executable) exits at once with a shell status and no DAP
            // traffic, which would otherwise look like a session that ended
            // for no reason.
            var status = command.ExitStatus;
            if (status is int code && code != 0)
            {
                LoggingService.LogWarning($"The debug adapter on the device exited with status {code}.");
                onDiagnostic?.Invoke($"The debug adapter on the device exited with status {code}.");
            }
        })
        {
            IsBackground = true,
            Name = "ssh-debugger-wait",
        };
        thread.Start();
    }

    /// <inheritdoc/>
    public void Terminate()
    {
        // Closing the channel ends the adapter, and the adapter's own
        // shutdown takes the application it launched with it.
        try
        {
            command.CancelAsync();
        }
        catch (Exception)
        {
            // Already finishing.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Terminate();
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
