//
// BreakpointStore.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Core.Debugging;

/// <summary>
/// The in-memory set of user breakpoints, keyed by file and 1-based line.
/// Deliberately not persisted: breakpoints live only while the solution is
/// open and are lost when it (or the application) closes.
/// </summary>
public class BreakpointStore
{
    readonly object gate = new object();
    readonly Dictionary<string, SortedSet<int>> byFile = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);

    /// <summary>Raised after the breakpoints of a file change.</summary>
    public event Action<FilePath> Changed;

    /// <summary>
    /// Toggles the breakpoint at the given file and 1-based line; returns
    /// true when the breakpoint is now set, false when it was removed.
    /// </summary>
    public bool Toggle(FilePath file, int line)
    {
        bool added;
        lock (gate)
        {
            if (!byFile.TryGetValue(file, out var lines))
                byFile[file] = lines = new SortedSet<int>();
            if (!(added = lines.Add(line)))
                lines.Remove(line);
            if (lines.Count == 0)
                byFile.Remove(file);
        }
        Changed?.Invoke(file);
        return added;
    }

    /// <summary>Whether a breakpoint is set at the given file and 1-based line.</summary>
    public bool IsSet(FilePath file, int line)
    {
        lock (gate)
            return byFile.TryGetValue(file, out var lines) && lines.Contains(line);
    }

    /// <summary>The 1-based breakpoint lines of the given file, ascending.</summary>
    public IReadOnlyList<int> GetLines(FilePath file)
    {
        lock (gate)
            return byFile.TryGetValue(file, out var lines) ? lines.ToList() : (IReadOnlyList<int>) Array.Empty<int>();
    }

    /// <summary>The files that currently have breakpoints.</summary>
    public IReadOnlyList<FilePath> GetFiles()
    {
        lock (gate)
            return byFile.Keys.Select(key => new FilePath(key)).ToList();
    }

    /// <summary>Removes every breakpoint (the solution closed).</summary>
    public void Clear()
    {
        List<string> files;
        lock (gate)
        {
            files = byFile.Keys.ToList();
            byFile.Clear();
        }
        foreach (var file in files)
            Changed?.Invoke(new FilePath(file));
    }
}
