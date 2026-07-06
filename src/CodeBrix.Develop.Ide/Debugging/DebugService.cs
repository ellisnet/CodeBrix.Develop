//
// DebugService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Debugging;
using CodeBrix.Develop.Core.Projects;

namespace CodeBrix.Develop.Ide.Debugging;

/// <summary>
/// The IDE's debugging orchestrator: owns the breakpoint store and the
/// single live <see cref="DebugSession"/>, and surfaces the state changes
/// the UI reacts to. Events are raised on background threads — UI
/// consumers must marshal to the GTK main loop.
/// </summary>
public static class DebugService
{
    static DebugSession? session;
    static readonly object gate = new object();

    /// <summary>The in-memory breakpoints (session-only; cleared when the solution closes).</summary>
    public static BreakpointStore Breakpoints { get; } = new BreakpointStore();

    /// <summary>Whether a debug session is running.</summary>
    public static bool IsSessionActive => session != null;

    /// <summary>Whether the debuggee is paused at a stop location.</summary>
    public static bool IsPaused => session?.IsPaused == true;

    /// <summary>The top stack frame's id while paused (for evaluation), or -1.</summary>
    public static int CurrentFrameId { get; private set; } = -1;

    /// <summary>Raised when the debuggee pauses: the stop reason and the call stack.</summary>
    public static event Action<string, IReadOnlyList<StackFrameInfo>>? Paused;

    /// <summary>Raised when the debuggee resumes running.</summary>
    public static event Action? Resumed;

    /// <summary>Raised when the session ends, with the debuggee's exit code when known.</summary>
    public static event Action<int?>? SessionEnded;

    /// <summary>Raised for each line of debuggee output.</summary>
    public static event Action<string>? OutputReceived;

    static DebugService()
    {
        Breakpoints.Changed += file =>
        {
            // Push live breakpoint edits into the running session.
            if (session is { } activeSession)
                _ = PushBreakpointsAsync(activeSession, file);
        };
    }

    static async Task PushBreakpointsAsync(DebugSession activeSession, FilePath file)
    {
        try
        {
            await activeSession.SetBreakpointsAsync(file, Breakpoints.GetLines(file)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Breakpoint update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Launches the given (already built) project under the debugger with
    /// the current breakpoints applied.
    /// </summary>
    public static async Task StartAsync(DotNetProject project)
    {
        lock (gate)
        {
            if (session != null)
                throw new InvalidOperationException("A debug session is already active");
        }

        var program = project.GetOutputExecutable();
        if (!File.Exists(program))
            throw new FileNotFoundException($"The built executable was not found: {program}", program);

        var breakpointsByFile = Breakpoints.GetFiles()
            .ToDictionary(file => (string) file, file => Breakpoints.GetLines(file));

        var newSession = await DebugSession.LaunchAsync(
            DebugSession.DefaultDebuggerPath, program, project.BaseDirectory, breakpointsByFile).ConfigureAwait(false);

        newSession.Stopped += (reason, threadId) => _ = OnStoppedAsync(newSession, reason);
        newSession.Resumed += () =>
        {
            CurrentFrameId = -1;
            Resumed?.Invoke();
        };
        newSession.SessionEnded += exitCode =>
        {
            lock (gate)
            {
                if (session != newSession)
                    return;
                session = null;
            }
            CurrentFrameId = -1;
            SessionEnded?.Invoke(exitCode);
        };
        newSession.OutputReceived += line => OutputReceived?.Invoke(line);

        lock (gate)
            session = newSession;
    }

    static async Task OnStoppedAsync(DebugSession stoppedSession, string reason)
    {
        try
        {
            var frames = await stoppedSession.GetStackTraceAsync().ConfigureAwait(false);
            CurrentFrameId = frames.Count > 0 ? frames[0].Id : -1;
            Paused?.Invoke(reason, frames);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Reading the call stack failed", ex);
            Paused?.Invoke(reason, Array.Empty<StackFrameInfo>());
        }
    }

    /// <summary>Resumes the paused debuggee.</summary>
    public static Task ContinueAsync() => session?.ContinueAsync() ?? Task.CompletedTask;

    /// <summary>Steps over the current line.</summary>
    public static Task StepOverAsync() => session?.StepOverAsync() ?? Task.CompletedTask;

    /// <summary>Steps into the call on the current line.</summary>
    public static Task StepIntoAsync() => session?.StepIntoAsync() ?? Task.CompletedTask;

    /// <summary>Steps out of the current method.</summary>
    public static Task StepOutAsync() => session?.StepOutAsync() ?? Task.CompletedTask;

    /// <summary>Evaluates an expression in the current (top) frame while paused.</summary>
    public static Task<EvaluationResult?> EvaluateAsync(string expression)
    {
        if (session is not { IsPaused: true } pausedSession || CurrentFrameId < 0)
            return Task.FromResult<EvaluationResult?>(null);
        return pausedSession.EvaluateAsync(expression, CurrentFrameId)!;
    }

    /// <summary>Stops the session, terminating the debuggee.</summary>
    public static Task StopAsync() => session?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Kills the session synchronously (application shutdown or solution
    /// close) and clears the breakpoints when requested.
    /// </summary>
    public static void Shutdown(bool clearBreakpoints)
    {
        session?.Dispose();
        if (clearBreakpoints)
            Breakpoints.Clear();
    }
}
