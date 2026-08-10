//
// SshTerminalSession.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Text;
using System.Threading;
using CodeBrix.Develop.Core;
using CodeBrix.SSH;

namespace CodeBrix.Develop.Ide.Remote;

/// <summary>
/// One interactive SSH shell session to a device: a CodeBrix.SSH client
/// holding an xterm-256color PTY channel, pumped by a background reader
/// thread. <see cref="DataReceived"/> and <see cref="Disconnected"/> are
/// raised on background threads — marshal to the UI thread before touching
/// widgets. Host keys are accepted without verification, the deliberate
/// trade of a development tool connecting to the developer's own device.
/// </summary>
public sealed class SshTerminalSession : IDisposable
{
    readonly string host;
    readonly int port;
    readonly string user;
    readonly string password;

    SshClient? client;
    ShellStream? shell;
    Thread? readThread;
    string? lastError;
    int disconnectedRaised;
    volatile bool disposed;

    /// <summary>Prepares a session; nothing connects until <see cref="Connect"/>.</summary>
    public SshTerminalSession(string host, int port, string user, string password)
    {
        this.host = host;
        this.port = port;
        this.user = user;
        this.password = password;
    }

    /// <summary>"user@host", for status messages.</summary>
    public string DisplayName => $"{user}@{host}";

    /// <summary>The login user, for building per-user provisioning commands.</summary>
    public string User => user;

    /// <summary>The device host, for opening a matching SFTP connection.</summary>
    public string Host => host;

    /// <summary>The device port.</summary>
    public int Port => port;

    /// <summary>
    /// Opens a connected SFTP client to the same device on its own connection
    /// (SFTP is a separate SSH connection from the interactive shell). The
    /// caller owns and disposes it.
    /// </summary>
    public SftpClient CreateSftpClient()
    {
        if (disposed)
            throw new InvalidOperationException("The SSH session is not connected");
        var sftp = new SftpClient(host, port, user, password);
        sftp.Connect();
        return sftp;
    }

    /// <summary>
    /// Creates an exec command on the live connection; the caller drives its
    /// streams (e.g. a long-lived app launch, streaming stdout/stderr). Runs
    /// on its own channel, independent of the interactive shell.
    /// </summary>
    public SshCommand CreateCommand(string commandText)
    {
        if (disposed || client is not { } c)
            throw new InvalidOperationException("The SSH session is not connected");
        return c.CreateCommand(commandText);
    }

    /// <summary>
    /// Raised from the reader thread with a chunk of raw terminal output.
    /// The array is freshly allocated per chunk and safe to hand off.
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Raised once, from a background thread, when the session ends for any
    /// reason other than <see cref="Dispose"/> — with a short description.
    /// </summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// Connects and opens the shell channel with the given PTY size. Blocks
    /// for the network round trips — call it off the UI thread. Throws on
    /// failure (connection refused, authentication rejected, timeouts);
    /// the session is disposable but unusable afterwards.
    /// </summary>
    public void Connect(int columns, int rows)
    {
        var connecting = new SshClient(host, port, user, password);
        connecting.HostKeyReceived += (_, e) => e.CanTrust = true;
        // An interactive terminal sits idle for long stretches; without
        // keepalives a NAT or firewall quietly drops the mapping.
        connecting.KeepAliveInterval = TimeSpan.FromSeconds(30);
        connecting.Connect();
        client = connecting;

        shell = connecting.CreateShellStream("xterm-256color",
            (uint) Math.Max(MinimumColumns, columns),
            (uint) Math.Max(MinimumRows, rows),
            width: 0, height: 0, bufferSize: 8192);
        shell.ErrorOccurred += (_, e) => lastError = e.Exception.Message;

        readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ssh-terminal-read",
        };
        readThread.Start();
    }

    // The PTY size floor the shell requests respect; mirrors the terminal
    // engine's own minimums.
    const int MinimumColumns = 2;
    const int MinimumRows = 1;

    /// <summary>
    /// Runs a one-off command on its own exec channel of this connection —
    /// the interactive shell is untouched. Blocks until the command
    /// completes; call it off the UI thread. The exit status is null when
    /// the channel closed without reporting one.
    /// </summary>
    public (int? ExitStatus, string Output) RunCommand(string commandText)
    {
        if (disposed || client is not { } c)
            throw new InvalidOperationException("The SSH session is not connected");
        using var command = c.RunCommand(commandText);
        return (command.ExitStatus, command.Result ?? "");
    }

    /// <summary>Sends keyboard input (VT-encoded text) to the shell.</summary>
    public void Send(string text)
    {
        if (disposed || shell is not { } stream || text.Length == 0)
            return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            // ShellStream buffers writes; nothing reaches the wire unflushed.
            stream.Flush();
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"SSH terminal write failed: {ex.Message}");
        }
    }

    /// <summary>Tells the remote PTY the terminal grid changed size.</summary>
    public void Resize(int columns, int rows)
    {
        if (disposed || shell is not { } stream)
            return;
        try
        {
            stream.ChangeWindowSize(
                (uint) Math.Max(MinimumColumns, columns),
                (uint) Math.Max(MinimumRows, rows),
                width: 0, height: 0);
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"SSH terminal resize failed: {ex.Message}");
        }
    }

    void ReadLoop()
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                // Blocks until output arrives; returns 0 when the channel
                // closes (remote logout, connection loss, disposal).
                var read = shell!.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                var chunk = new byte[read];
                System.Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                DataReceived?.Invoke(chunk);
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                lastError ??= ex.Message;
        }
        RaiseDisconnected(lastError is { } error
            ? $"connection lost ({error})"
            : "the remote shell ended the session");
    }

    void RaiseDisconnected(string reason)
    {
        if (disposed || Interlocked.Exchange(ref disconnectedRaised, 1) != 0)
            return;
        Disconnected?.Invoke(reason);
    }

    /// <summary>
    /// Tears the session down without raising <see cref="Disconnected"/>.
    /// Disconnecting blocks on the network — call it off the UI thread.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            shell?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.LogInfo($"SSH shell stream dispose: {ex.Message}");
        }
        try
        {
            if (client is { } c)
            {
                c.Disconnect();
                c.Dispose();
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogInfo($"SSH client dispose: {ex.Message}");
        }
    }
}
