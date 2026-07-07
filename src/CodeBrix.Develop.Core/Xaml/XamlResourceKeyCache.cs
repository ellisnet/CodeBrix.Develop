//
// XamlResourceKeyCache.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CodeBrix.Develop.Core.Xaml;

/// <summary>
/// Harvests x:Key resource names from the solution's .xaml files for
/// {StaticResource}/{ThemeResource} completion. Files are re-read only when
/// their timestamps change; the scan is a lightweight regex pass, not a
/// full parse.
/// </summary>
public static class XamlResourceKeyCache
{
    static readonly Regex keyPattern = new Regex(
        "x:Key\\s*=\\s*\"([^\"{}]+)\"", RegexOptions.Compiled);

    static readonly ConcurrentDictionary<string, (DateTime WriteTime, string[] Keys)> fileCache =
        new ConcurrentDictionary<string, (DateTime, string[])>(StringComparer.Ordinal);

    /// <summary>
    /// All resource keys found in .xaml files under the given directories
    /// (bin/ and obj/ subtrees are skipped), sorted and de-duplicated.
    /// </summary>
    public static IReadOnlyList<string> GetKeys(IEnumerable<string> directories)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                continue;
            foreach (var file in EnumerateXamlFiles(directory))
            {
                foreach (var key in GetFileKeys(file))
                    keys.Add(key);
            }
        }
        return new List<string>(keys);
    }

    /// <summary>Drops all cached file contents (e.g. when the solution closes).</summary>
    public static void Clear() => fileCache.Clear();

    static IEnumerable<string> EnumerateXamlFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var name = Path.GetFileName(current);
            if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".", StringComparison.Ordinal))
                continue;

            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(current, "*.xaml");
                subdirectories = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue; // unreadable directory: skip silently
            }
            foreach (var file in files)
                yield return file;
            foreach (var subdirectory in subdirectories)
                pending.Push(subdirectory);
        }
    }

    static string[] GetFileKeys(string file)
    {
        try
        {
            var writeTime = File.GetLastWriteTimeUtc(file);
            if (fileCache.TryGetValue(file, out var cached) && cached.WriteTime == writeTime)
                return cached.Keys;

            var matches = keyPattern.Matches(File.ReadAllText(file));
            var keys = new string[matches.Count];
            for (var i = 0; i < matches.Count; i++)
                keys[i] = matches[i].Groups[1].Value;
            fileCache[file] = (writeTime, keys);
            return keys;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
