//
// LoggingService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Core.LoggingService, simplified for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core;

/// <summary>
/// Minimal logging service for the IDE. Writes timestamped messages to the
/// console, and forwards every line to any registered sinks (e.g. the
/// IDE Log pad), replaying earlier lines when a sink registers late.
/// </summary>
public static class LoggingService
{
    static readonly object sync = new();
    static readonly List<string> history = new();
    static readonly List<Action<string>> sinks = new();

    static void Log(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level}: {message}";
        Console.WriteLine(line);
        Action<string>[] targets;
        lock (sync)
        {
            history.Add(line);
            targets = sinks.ToArray();
        }
        foreach (var sink in targets)
            sink(line);
    }

    /// <summary>
    /// Registers a sink that receives every logged line from now on; lines
    /// logged before registration are replayed to it first, so no message is
    /// missed. Sinks may be called from any thread — marshal to the UI
    /// thread inside the sink if needed.
    /// </summary>
    public static void AddSink(Action<string> sink)
    {
        string[] backlog;
        lock (sync)
        {
            backlog = history.ToArray();
            sinks.Add(sink);
        }
        foreach (var line in backlog)
            sink(line);
    }

    /// <summary>Logs an informational message.</summary>
    public static void LogInfo(string message) => Log("INFO ", message);

    /// <summary>Logs a warning message.</summary>
    public static void LogWarning(string message) => Log("WARN ", message);

    /// <summary>Logs an error message.</summary>
    public static void LogError(string message) => Log("ERROR", message);

    /// <summary>Logs an error message with exception details.</summary>
    public static void LogError(string message, Exception ex) => Log("ERROR", $"{message}: {ex}");
}
