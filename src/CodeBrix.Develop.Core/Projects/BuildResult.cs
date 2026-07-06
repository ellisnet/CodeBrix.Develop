//
// BuildResult.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Projects.BuildResult, simplified for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// The outcome of a build operation: success flag, elapsed time, and the
/// errors and warnings parsed from the build output.
/// </summary>
public class BuildResult
{
    readonly List<BuildError> errors = new List<BuildError>();

    /// <summary>The errors and warnings produced by the build.</summary>
    public IReadOnlyList<BuildError> Errors => errors;

    /// <summary>The number of errors (excluding warnings).</summary>
    public int ErrorCount { get; private set; }

    /// <summary>The number of warnings.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Whether the build process exited successfully.</summary>
    public bool Success { get; set; }

    /// <summary>How long the build took.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Adds an error or warning to the result.</summary>
    public void Append(BuildError error)
    {
        if (error == null)
            return;
        errors.Add(error);
        if (error.IsWarning)
            WarningCount++;
        else
            ErrorCount++;
    }

    /// <summary>Formats a one-line summary, e.g. "Build: 2 errors, 1 warning".</summary>
    public override string ToString()
        => $"Build: {ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, {WarningCount} warning{(WarningCount == 1 ? "" : "s")}";
}
