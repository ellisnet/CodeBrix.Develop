//
// DesktopServices.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CodeBrix.Develop.Core;

namespace CodeBrix.Develop.Ide;

/// <summary>
/// Hands a folder to the desktop environment's own applications: its file
/// manager, and its terminal. Every launch is fire-and-forget — the started
/// application outlives the IDE.
/// </summary>
public static class DesktopServices
{
    // The Debian alternatives entry first (it is whatever the user chose),
    // then the terminals the common desktops ship, then the fallback that
    // every X11 install has.
    static readonly string[] terminalCommands =
    {
        "x-terminal-emulator", "gnome-terminal", "kgx", "konsole", "xfce4-terminal",
        "mate-terminal", "tilix", "alacritty", "kitty", "xterm",
    };

    /// <summary>
    /// Opens the folder in the desktop's file manager. Returns false with a
    /// reason when the folder is gone or nothing could be launched.
    /// </summary>
    public static bool TryOpenFolder(string directory, out string error)
    {
        if (!Directory.Exists(directory))
        {
            error = $"The folder no longer exists: {directory}";
            return false;
        }
        // Resolved rather than trusted to PATH: the launch goes through a
        // shell, which would report a missing command as its own exit code
        // long after Process.Start has already succeeded.
        if (ResolveOnPath("xdg-open") is not { } opener)
        {
            error = "xdg-open was not found — no file manager could be opened";
            return false;
        }
        try
        {
            Start(opener, new[] { directory }, directory);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Could not open the folder {directory}", ex);
            error = $"Could not open the folder: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Opens the desktop's terminal application with the folder as its
    /// working directory. The $TERMINAL preference wins when it is set.
    /// Returns false with a reason when no terminal could be started.
    /// </summary>
    public static bool TryOpenTerminal(string directory, out string error)
    {
        if (!Directory.Exists(directory))
        {
            error = $"The folder no longer exists: {directory}";
            return false;
        }

        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } preferred)
            candidates.Add(preferred);
        candidates.AddRange(terminalCommands);

        foreach (var command in candidates)
        {
            if (ResolveOnPath(command) is not { } executable)
                continue;
            try
            {
                // The terminal inherits the working directory, which is what
                // opens the shell where the solution lives.
                Start(executable, Array.Empty<string>(), directory);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"The terminal '{executable}' could not be started: {ex.Message}");
            }
        }
        error = "No terminal application was found to open";
        return false;
    }

    static void Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // A launched child would otherwise inherit the IDE's stdout/stderr and
        // write its own diagnostics into the terminal CodeBrix.Develop was
        // started from — file managers in particular are chatty (Nemo probes
        // Samba for user shares and complains when it is not set up). None of
        // that is the IDE's output, so it goes to /dev/null. Redirecting
        // through a shell rather than .NET's pipes keeps the IDE out of the
        // middle: there is no pipe to drain, and no risk of the child stalling
        // on a full buffer or dying when the IDE closes its end.
        //
        // "$0" "$@" passes the command and its arguments through the shell
        // untouched, so nothing needs quoting; exec replaces the shell, so no
        // extra process is left behind.
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" \"$@\" >/dev/null 2>&1");
        startInfo.ArgumentList.Add(fileName);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // Nothing is read back from it and nothing waits on it: the launched
        // application belongs to the desktop now, not to the IDE.
        using var process = Process.Start(startInfo);
    }

    // The first PATH entry holding an executable file of that name, or null.
    // An absolute command (from $TERMINAL) is taken as given.
    static string? ResolveOnPath(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar))
            return File.Exists(command) ? command : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
                continue;
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
