//
// DapClient.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (a minimal VSCode Debug Adapter Protocol client for driving the
//      CodeBrix.Develop.Debug (netcoredbg) debugger over stdio)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Debugging;

/// <summary>
/// A minimal Debug Adapter Protocol (DAP) client: Content-Length framed
/// JSON messages over a stream pair, with request/response correlation and
/// event dispatch. Transport only — the debugging semantics live in
/// <see cref="DebugSession"/>.
/// </summary>
public class DapClient : IDisposable
{
    static readonly JsonSerializerOptions serializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly Stream input;
    readonly Stream output;
    readonly object writeGate = new object();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> pending =
        new ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>>();
    int nextSeq;
    Thread readThread;
    volatile bool disposed;

    /// <summary>
    /// Raised for every event message, with the event name and its body
    /// (an undefined-value clone-free element — handle synchronously or
    /// extract what you need before returning). Raised on the read thread.
    /// </summary>
    public event Action<string, JsonElement> EventReceived;

    /// <summary>Raised when the adapter's output stream ends (it exited).</summary>
    public event Action ConnectionClosed;

    /// <summary>
    /// Creates a client over the given streams: <paramref name="input"/>
    /// carries messages FROM the adapter (its stdout), <paramref name="output"/>
    /// carries messages TO the adapter (its stdin).
    /// </summary>
    public DapClient(Stream input, Stream output)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>Starts the background read loop.</summary>
    public void Start()
    {
        if (readThread != null)
            throw new InvalidOperationException("The client is already started");
        readThread = new Thread(ReadLoop) { IsBackground = true, Name = "DAP read loop" };
        readThread.Start();
    }

    /// <summary>
    /// Sends a request and returns the response's "body" element (an empty
    /// element when the response has none). Throws <see cref="DapException"/>
    /// when the adapter reports failure.
    /// </summary>
    public async Task<JsonElement> SendRequestAsync(string command, object arguments = null, CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref nextSeq);
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[seq] = completion;

        var message = JsonSerializer.SerializeToUtf8Bytes(new DapRequest
        {
            Seq = seq,
            Type = "request",
            Command = command,
            Arguments = arguments,
        }, serializerOptions);
        WriteMessage(message);

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        using var response = await completion.Task.ConfigureAwait(false);

        var root = response.RootElement;
        var success = root.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
        if (!success)
        {
            var errorText = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            throw new DapException(command, string.IsNullOrEmpty(errorText) ? $"The '{command}' request failed" : errorText);
        }
        return root.TryGetProperty("body", out var body) ? body.Clone() : default;
    }

    sealed class DapRequest
    {
        public int Seq { get; set; }
        public string Type { get; set; }
        public string Command { get; set; }
        public object Arguments { get; set; }
    }

    void WriteMessage(byte[] payload)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        lock (writeGate)
        {
            output.Write(header, 0, header.Length);
            output.Write(payload, 0, payload.Length);
            output.Flush();
        }
    }

    void ReadLoop()
    {
        try
        {
            while (!disposed)
            {
                var payload = ReadOneMessage();
                if (payload == null)
                    break;
                DispatchMessage(payload);
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                LoggingService.LogWarning($"DAP read loop ended: {ex.Message}");
        }
        FailAllPending();
        ConnectionClosed?.Invoke();
    }

    byte[] ReadOneMessage()
    {
        // Headers: ASCII lines terminated by \r\n, blank line ends them.
        var contentLength = -1;
        var line = new StringBuilder();
        while (true)
        {
            var value = input.ReadByte();
            if (value < 0)
                return null; // stream ended
            if (value == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                    break; // end of headers
                const string prefix = "Content-Length:";
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(text.Substring(prefix.Length).Trim(), CultureInfo.InvariantCulture);
            }
            else
            {
                line.Append((char) value);
            }
        }
        if (contentLength < 0)
            throw new InvalidDataException("DAP message without a Content-Length header");

        var payload = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var chunk = input.Read(payload, read, contentLength - read);
            if (chunk <= 0)
                return null;
            read += chunk;
        }
        return payload;
    }

    void DispatchMessage(byte[] payload)
    {
        var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        if (type == "response")
        {
            var requestSeq = root.GetProperty("request_seq").GetInt32();
            if (pending.TryRemove(requestSeq, out var completion))
            {
                completion.TrySetResult(document); // disposed by the awaiter
                return;
            }
            document.Dispose();
            return;
        }

        using (document)
        {
            if (type == "event")
            {
                var eventName = root.TryGetProperty("event", out var nameElement) ? nameElement.GetString() : "";
                var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.Clone() : default;
                try
                {
                    EventReceived?.Invoke(eventName, body);
                }
                catch (Exception ex)
                {
                    LoggingService.LogError($"DAP event handler for '{eventName}' failed", ex);
                }
            }
            // "request" messages from the adapter (reverse requests) are not
            // used by netcoredbg and are ignored.
        }
    }

    void FailAllPending()
    {
        foreach (var seq in pending.Keys)
        {
            if (pending.TryRemove(seq, out var completion))
                completion.TrySetException(new DapException("(connection)", "The debugger connection closed"));
        }
    }

    /// <summary>Stops the read loop and fails any pending requests.</summary>
    public void Dispose()
    {
        disposed = true;
        try { input.Dispose(); } catch { /* best effort */ }
        try { output.Dispose(); } catch { /* best effort */ }
        FailAllPending();
    }
}

/// <summary>An error response from the debug adapter.</summary>
public class DapException : Exception
{
    /// <summary>The DAP command that failed.</summary>
    public string Command { get; }

    /// <summary>Creates the exception for a failed command.</summary>
    public DapException(string command, string message) : base(message)
    {
        Command = command;
    }
}
