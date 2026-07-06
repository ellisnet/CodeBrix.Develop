//
// LoggingService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Core.LoggingService, simplified for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Core;

/// <summary>
/// Minimal logging service for the IDE. Writes timestamped messages to the
/// console; a richer sink can be swapped in later without changing call sites.
/// </summary>
public static class LoggingService
{
    static void Log(string level, string message)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level}: {message}");

    /// <summary>Logs an informational message.</summary>
    public static void LogInfo(string message) => Log("INFO ", message);

    /// <summary>Logs a warning message.</summary>
    public static void LogWarning(string message) => Log("WARN ", message);

    /// <summary>Logs an error message.</summary>
    public static void LogError(string message) => Log("ERROR", message);

    /// <summary>Logs an error message with exception details.</summary>
    public static void LogError(string message, Exception ex) => Log("ERROR", $"{message}: {ex}");
}
