//
// DebugSession.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (drives the CodeBrix.Develop.Debug (netcoredbg) debugger over the
//      VSCode Debug Adapter Protocol)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Debugging;

/// <summary>One frame of a paused thread's call stack.</summary>
public class StackFrameInfo
{
    /// <summary>The adapter's frame id (used for evaluation).</summary>
    public int Id { get; set; }

    /// <summary>The frame's display name (method signature).</summary>
    public string Name { get; set; }

    /// <summary>The source file, or "" when no source is available.</summary>
    public string File { get; set; }

    /// <summary>The 1-based line, or 0 when no source is available.</summary>
    public int Line { get; set; }
}

/// <summary>The result of evaluating an expression in a paused frame.</summary>
public class EvaluationResult
{
    /// <summary>Whether the expression evaluated to a value.</summary>
    public bool Success { get; set; }

    /// <summary>The value text, or the evaluator's error message.</summary>
    public string Text { get; set; }
}

/// <summary>
/// One live debugging session: launches the bundled CodeBrix.Develop.Debug
/// (netcoredbg) debugger, speaks DAP to it, and surfaces the events and
/// commands the IDE needs. Create with <see cref="LaunchAsync"/>.
/// </summary>
public class DebugSession : IDisposable
{
    Process process;
    DapClient client;
    int stoppedThreadId = -1;
    volatile bool paused;

    /// <summary>
    /// The bundled debugger executable: the CodeBrix.Develop.Debug package
    /// for the host architecture lands it at the same app-relative path.
    /// </summary>
    public static string DefaultDebuggerPath =>
        Path.Combine(AppContext.BaseDirectory, "netcoredbg", "netcoredbg");

    /// <summary>Whether the debuggee is currently paused (stopped event received).</summary>
    public bool IsPaused => paused;

    /// <summary>Raised when the debuggee stops (breakpoint, step, exception); reason + thread id.</summary>
    public event Action<string, int> Stopped;

    /// <summary>Raised when the debuggee resumes running.</summary>
    public event Action Resumed;

    /// <summary>Raised once when the session ends (debuggee exited or the debugger died); the exit code, or null.</summary>
    public event Action<int?> SessionEnded;

    /// <summary>Raised for each line of debuggee output.</summary>
    public event Action<string> OutputReceived;

    DebugSession()
    {
    }

    /// <summary>
    /// Starts the debugger, launches the program under it with the given
    /// breakpoints applied, and returns once the debuggee is running.
    /// </summary>
    public static async Task<DebugSession> LaunchAsync(string debuggerPath, string program, string workingDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpointsByFile, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(debuggerPath))
            throw new FileNotFoundException("The debugger binary was not found; is the CodeBrix.Develop.Debug package present?", debuggerPath);
        if (!File.Exists(program))
            throw new FileNotFoundException("The program to debug was not found; build it first.", program);

        var session = new DebugSession();
        var startInfo = new ProcessStartInfo(debuggerPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--interpreter=vscode");

        session.process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        session.process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                LoggingService.LogWarning($"netcoredbg: {e.Data}");
        };
        if (!session.process.Start())
            throw new InvalidOperationException("The debugger process could not be started");
        session.process.BeginErrorReadLine();

        session.client = new DapClient(session.process.StandardOutput.BaseStream, session.process.StandardInput.BaseStream);
        var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.client.EventReceived += (eventName, body) => session.OnEvent(eventName, body, initialized);
        session.client.ConnectionClosed += () => session.OnConnectionClosed();
        session.client.Start();

        try
        {
            await session.client.SendRequestAsync("initialize", new
            {
                clientID = "codebrix-develop",
                clientName = "CodeBrix Develop",
                adapterID = "coreclr",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat = "path",
                locale = "en-US",
            }, cancellationToken).ConfigureAwait(false);

            // The launch response only arrives after configurationDone, so
            // run the request concurrently and complete configuration when
            // the adapter signals "initialized".
            var launchTask = session.client.SendRequestAsync("launch", new
            {
                name = ".NET Core Launch",
                type = "coreclr",
                request = "launch",
                program,
                cwd = workingDirectory,
                console = "internalConsole",
                stopAtEntry = false,
                justMyCode = true,
            }, cancellationToken);

            await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            foreach (var pair in breakpointsByFile)
                await session.SetBreakpointsAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
            await session.client.SendRequestAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);
            await launchTask.ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    void OnEvent(string eventName, JsonElement body, TaskCompletionSource<bool> initialized)
    {
        switch (eventName)
        {
            case "initialized":
                initialized.TrySetResult(true);
                break;

            case "stopped":
                paused = true;
                stoppedThreadId = body.TryGetProperty("threadId", out var threadElement) ? threadElement.GetInt32() : -1;
                var reason = body.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : "stopped";
                Stopped?.Invoke(reason, stoppedThreadId);
                break;

            case "continued":
                paused = false;
                Resumed?.Invoke();
                break;

            case "exited":
                var exitCode = body.TryGetProperty("exitCode", out var codeElement) ? codeElement.GetInt32() : 0;
                EndSession(exitCode);
                break;

            case "terminated":
                EndSession(null);
                break;

            case "output":
                var text = body.TryGetProperty("output", out var outputElement) ? outputElement.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                    OutputReceived?.Invoke(text.TrimEnd('\n'));
                break;
        }
    }

    void OnConnectionClosed() => EndSession(null);

    int sessionEndedRaised;

    void EndSession(int? exitCode)
    {
        paused = false;
        if (Interlocked.Exchange(ref sessionEndedRaised, 1) == 0)
            SessionEnded?.Invoke(exitCode);
    }

    /// <summary>Replaces the breakpoints of one file (1-based lines).</summary>
    public Task SetBreakpointsAsync(string file, IReadOnlyList<int> lines, CancellationToken cancellationToken = default) =>
        client.SendRequestAsync("setBreakpoints", new
        {
            source = new { path = file },
            breakpoints = lines.Select(line => new { line }).ToArray(),
        }, cancellationToken);

    /// <summary>The call stack of the stopped thread (frames without source are skipped).</summary>
    public async Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync(CancellationToken cancellationToken = default)
    {
        var body = await client.SendRequestAsync("stackTrace", new
        {
            threadId = stoppedThreadId,
            startFrame = 0,
            levels = 50,
        }, cancellationToken).ConfigureAwait(false);

        var frames = new List<StackFrameInfo>();
        foreach (var frame in body.GetProperty("stackFrames").EnumerateArray())
        {
            var info = new StackFrameInfo
            {
                Id = frame.GetProperty("id").GetInt32(),
                Name = frame.TryGetProperty("name", out var name) ? name.GetString() : "?",
                Line = frame.TryGetProperty("line", out var line) ? line.GetInt32() : 0,
                File = frame.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                    && source.TryGetProperty("path", out var path) ? path.GetString() : "",
            };
            frames.Add(info);
        }
        return frames;
    }

    /// <summary>Evaluates an expression in the given frame (hover context).</summary>
    public async Task<EvaluationResult> EvaluateAsync(string expression, int frameId, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await client.SendRequestAsync("evaluate", new
            {
                expression,
                frameId,
                context = "hover",
            }, cancellationToken).ConfigureAwait(false);
            return new EvaluationResult
            {
                Success = true,
                Text = body.TryGetProperty("result", out var result) ? result.GetString() : "",
            };
        }
        catch (DapException ex)
        {
            return new EvaluationResult { Success = false, Text = ex.Message };
        }
    }

    Task SendResumingCommandAsync(string command, CancellationToken cancellationToken)
    {
        // netcoredbg reports the resume via a "continued" event, but mark
        // the session running immediately so UI state follows the command.
        paused = false;
        Resumed?.Invoke();
        return client.SendRequestAsync(command, new { threadId = stoppedThreadId }, cancellationToken);
    }

    /// <summary>Resumes execution.</summary>
    public Task ContinueAsync(CancellationToken cancellationToken = default) => SendResumingCommandAsync("continue", cancellationToken);

    /// <summary>Steps over the current line.</summary>
    public Task StepOverAsync(CancellationToken cancellationToken = default) => SendResumingCommandAsync("next", cancellationToken);

    /// <summary>Steps into the call on the current line.</summary>
    public Task StepIntoAsync(CancellationToken cancellationToken = default) => SendResumingCommandAsync("stepIn", cancellationToken);

    /// <summary>Steps out of the current method.</summary>
    public Task StepOutAsync(CancellationToken cancellationToken = default) => SendResumingCommandAsync("stepOut", cancellationToken);

    /// <summary>Pauses the running debuggee.</summary>
    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        client.SendRequestAsync("pause", new { threadId = 0 }, cancellationToken);

    /// <summary>Stops debugging, terminating the debuggee.</summary>
    public async Task StopAsync()
    {
        try
        {
            await client.SendRequestAsync("disconnect", new { terminateDebuggee = true },
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // fall through to the hard kill below
        }
        Dispose();
    }

    /// <summary>Tears the session down, killing the debugger if needed.</summary>
    public void Dispose()
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // the process may have already exited
        }
        client?.Dispose();
        process?.Dispose();
        EndSession(null);
    }
}
