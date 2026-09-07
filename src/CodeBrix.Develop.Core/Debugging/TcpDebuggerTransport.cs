//
// TcpDebuggerTransport.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the stream pair for a debug adapter that listens on a TCP port
//      instead of speaking over its own standard input and output)
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Debugging;

/// <summary>
/// The debug adapter reached over a TCP socket on the loopback interface:
/// what an adapter started with <c>--server=&lt;port&gt;</c> speaks DAP on.
/// <para>
/// This is how an Android session works. The adapter runs inside the app's
/// sandbox on the device, listening on a device port; <c>adb forward</c>
/// publishes that port on this machine, and everything above this class sees
/// an ordinary <see cref="IDebuggerTransport"/> — one socket, used for both
/// directions, because a socket's <see cref="NetworkStream"/> IS both.
/// </para>
/// </summary>
public sealed class TcpDebuggerTransport : IDebuggerTransport
{
    /// <summary>
    /// How long <see cref="ConnectAsync"/> waits between attempts. The
    /// adapter is started in the background on the device and takes a moment
    /// to bind its port, so the first attempts are expected to be refused.
    /// </summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    readonly TcpClient client;
    readonly NetworkStream stream;
    int terminated;

    TcpDebuggerTransport(TcpClient client, int port)
    {
        this.client = client;
        stream = client.GetStream();
        Port = port;
    }

    /// <summary>The loopback port the adapter was reached on.</summary>
    public int Port { get; }

    /// <summary>
    /// Connects to an adapter listening on 127.0.0.1:<paramref name="port"/>,
    /// retrying every <see cref="RetryInterval"/> until it accepts or
    /// <paramref name="timeout"/> elapses. A refused connection is the normal
    /// state of affairs until the adapter finishes starting, so only the
    /// expiry of the whole timeout is an error — and it throws
    /// <see cref="TimeoutException"/> carrying the last socket failure, which
    /// is what says whether nothing was listening or something else was wrong.
    /// </summary>
    public static async Task<TcpDebuggerTransport> ConnectAsync(int port, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        SocketException lastFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                return new TcpDebuggerTransport(client, port);
            }
            catch (SocketException ex)
            {
                client.Dispose();
                lastFailure = ex;
            }
            catch (Exception)
            {
                client.Dispose();
                throw;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"No debug adapter accepted a connection on 127.0.0.1:{port} within " +
                    $"{timeout.TotalSeconds:F0} seconds.", lastFailure);
            }
            await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Stream Input => stream;

    /// <inheritdoc/>
    public Stream Output => stream;

    /// <summary>
    /// Closes the socket, which ends the adapter's side of the conversation
    /// and unblocks the reader waiting on it. Safe to call more than once and
    /// safe on a connection the far end already closed.
    /// </summary>
    public void Terminate()
    {
        if (Interlocked.Exchange(ref terminated, 1) != 0)
            return;
        try
        {
            client.Close();
        }
        catch (Exception)
        {
            // The socket may already be closed; there is nothing to undo.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Terminate();
        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
            // Best-effort: a disposed socket is the desired end state.
        }
    }
}
