//
// DebuggerTransport.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the stream pair a DebugSession speaks DAP over, and the local
//      process implementation of it)
// SPDX-License-Identifier: MIT
//

using System;
using System.Diagnostics;
using System.IO;

namespace CodeBrix.Develop.Core.Debugging;

/// <summary>
/// The debug adapter a <see cref="DebugSession"/> drives, reduced to the two
/// streams DAP needs and the ability to end it. A local session runs the
/// bundled debugger as a child process; a device session runs it on the far
/// end of an SSH exec channel. Nothing above this interface knows which.
/// </summary>
public interface IDebuggerTransport : IDisposable
{
    /// <summary>Messages FROM the adapter (its standard output).</summary>
    Stream Input { get; }

    /// <summary>Messages TO the adapter (its standard input).</summary>
    Stream Output { get; }

    /// <summary>
    /// Ends the adapter the hard way, after a graceful DAP disconnect has
    /// been tried (or has failed). Must be safe to call more than once, and
    /// safe to call on an adapter that already exited.
    /// </summary>
    void Terminate();
}

/// <summary>
/// The local transport: the bundled debugger as a child process, spoken to
/// over its redirected standard input and output. Its standard error is
/// pumped to the IDE log, where a debugger that fails to start explains
/// itself.
/// </summary>
public sealed class ProcessDebuggerTransport : IDebuggerTransport
{
    readonly Process process;

    ProcessDebuggerTransport(Process process)
    {
        this.process = process;
    }

    /// <summary>
    /// Starts the debugger executable with the VSCode (DAP) interpreter in
    /// the given working directory.
    /// </summary>
    public static ProcessDebuggerTransport Start(string debuggerPath, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(debuggerPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--interpreter=vscode");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                LoggingService.LogWarning($"netcoredbg: {e.Data}");
        };
        if (!process.Start())
            throw new InvalidOperationException("The debugger process could not be started");
        process.BeginErrorReadLine();
        return new ProcessDebuggerTransport(process);
    }

    /// <inheritdoc/>
    public Stream Input => process.StandardOutput.BaseStream;

    /// <inheritdoc/>
    public Stream Output => process.StandardInput.BaseStream;

    /// <inheritdoc/>
    public void Terminate()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process may have already exited.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Terminate();
        process.Dispose();
    }
}
