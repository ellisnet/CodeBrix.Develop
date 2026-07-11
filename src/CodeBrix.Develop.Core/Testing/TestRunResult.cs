//
// TestRunResult.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.UnitTesting's UnitTestResult, simplified for
//      CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Text.RegularExpressions;

namespace CodeBrix.Develop.Core.Testing;

/// <summary>
/// The outcome of one test method's most recent run. A theory method's data
/// rows aggregate into a single result (worst outcome wins; failure messages
/// concatenate).
/// </summary>
public class TestRunResult
{
    // "   at Ns.Cls.m() in /path/File.cs:line 29" (.NET stacks) and
    // "   at Ns.Cls.m() in /path/File.cs:20" (MTP detail output).
    static readonly Regex stackLocationRegex = new Regex(
        @"\bin (?<file>.+?):(?:line )?(?<line>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The outcome (Passed, Failed, or Skipped).</summary>
    public TestStatus Status { get; set; } = TestStatus.NotRun;

    /// <summary>The failure message(s), or the skip reason, or "".</summary>
    public string Message { get; set; } = "";

    /// <summary>The failure stack trace(s), or "".</summary>
    public string StackTrace { get; set; } = "";

    /// <summary>The execution time in seconds (a theory's rows are summed).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Finds the failing source location by parsing the stack trace for its
    /// first "in file:line" occurrence. Returns false when the stack trace
    /// carries no source location.
    /// </summary>
    public bool TryGetFailureLocation(out FilePath file, out int line)
    {
        var match = stackLocationRegex.Match(StackTrace ?? "");
        if (match.Success && int.TryParse(match.Groups["line"].Value, out line))
        {
            file = new FilePath(match.Groups["file"].Value);
            return true;
        }
        file = default;
        line = 0;
        return false;
    }
}

/// <summary>The overall outcome of one test run (across all targeted projects).</summary>
public class TestRunSummary
{
    /// <summary>How many tests ran or were reported (theory rows count individually).</summary>
    public int Total { get; set; }

    /// <summary>How many tests passed.</summary>
    public int Passed { get; set; }

    /// <summary>How many tests failed.</summary>
    public int Failed { get; set; }

    /// <summary>How many tests were skipped.</summary>
    public int Skipped { get; set; }

    /// <summary>The wall-clock duration of the whole run (builds included).</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Whether a pre-run project build failed (the run was aborted).</summary>
    public bool BuildFailed { get; set; }

    /// <summary>Whether the run was cancelled by the user.</summary>
    public bool Cancelled { get; set; }

    /// <summary>A run-level error message (runner crash, missing executable, …), or "".</summary>
    public string Error { get; set; } = "";

    /// <summary>Whether the run completed with no failures and no run-level errors.</summary>
    public bool Success => !BuildFailed && !Cancelled && Error.Length == 0 && Failed == 0;
}
