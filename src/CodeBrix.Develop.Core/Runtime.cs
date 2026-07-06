//
// Runtime.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Core.Runtime, simplified for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using CodeBrix.Develop.Core.TypeSystem;

namespace CodeBrix.Develop.Core;

/// <summary>
/// Core runtime bootstrap for CodeBrix.Develop. Must be initialized once at
/// application startup, before any solution is loaded.
/// </summary>
public static class Runtime
{
    static bool initialized;

    /// <summary>Whether <see cref="Initialize"/> has run.</summary>
    public static bool Initialized => initialized;

    /// <summary>
    /// Initializes the core runtime: locates the installed .NET SDK's MSBuild
    /// for the Roslyn workspace. Safe to call more than once.
    /// </summary>
    public static void Initialize()
    {
        if (initialized)
            return;
        initialized = true;

        LoggingService.LogInfo($"CodeBrix.Develop core runtime initializing (.NET {Environment.Version})");
        TypeSystemService.Initialize();
    }
}
