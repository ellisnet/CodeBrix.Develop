//
// CompletionFilter.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.CodeCompletion / VS completion matching,
//      original implementation for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.TypeSystem;

/// <summary>
/// Matches completion items against the text the user has typed so far:
/// exact, prefix, camel-hump, and substring matches, in decreasing order of
/// relevance. Used by completion-as-you-type to filter and rank the list.
/// </summary>
public static class CompletionFilter
{
    /// <summary>
    /// Scores how well <paramref name="candidate"/> matches the typed
    /// <paramref name="pattern"/>. Higher is better; -1 means no match.
    /// An empty pattern matches everything with score 0.
    /// </summary>
    public static int Score(string candidate, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return 0;
        if (string.IsNullOrEmpty(candidate))
            return -1;

        if (string.Equals(candidate, pattern, StringComparison.Ordinal))
            return 1000;
        if (string.Equals(candidate, pattern, StringComparison.OrdinalIgnoreCase))
            return 900;
        if (candidate.StartsWith(pattern, StringComparison.Ordinal))
            return 800;
        if (candidate.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return 700;
        if (MatchesCamelHumps(candidate, pattern))
            return 500;
        if (candidate.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            return 300;
        return -1;
    }

    /// <summary>
    /// Filters and ranks <paramref name="items"/> against the typed
    /// <paramref name="pattern"/>. Items keep their relative (SortText)
    /// order within equal match scores.
    /// </summary>
    public static IReadOnlyList<CodeCompletionItem> Filter(IReadOnlyList<CodeCompletionItem> items, string pattern)
    {
        var scored = new List<(CodeCompletionItem Item, int Score, int Index)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var score = Score(item.FilterText ?? item.DisplayText, pattern);
            if (score >= 0)
                scored.Add((item, score, i));
        }
        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(b.Index);
        });
        var results = new List<CodeCompletionItem>(scored.Count);
        foreach (var entry in scored)
            results.Add(entry.Item);
        return results;
    }

    // "TBl" matches "TextBlock": every pattern character must match, in
    // order, either a hump start (uppercase, digit, or the char after '_'
    // or '.') or the character right after the previous match.
    static bool MatchesCamelHumps(string candidate, string pattern)
    {
        var candidateIndex = 0;
        var lastMatchIndex = -1;
        for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
        {
            var patternChar = char.ToLowerInvariant(pattern[patternIndex]);
            var found = false;
            while (candidateIndex < candidate.Length)
            {
                var isHumpStart = candidateIndex == 0
                    || char.IsUpper(candidate[candidateIndex])
                    || char.IsDigit(candidate[candidateIndex])
                    || candidate[candidateIndex - 1] == '_'
                    || candidate[candidateIndex - 1] == '.';
                var contiguous = patternIndex > 0 && candidateIndex == lastMatchIndex + 1;
                if ((isHumpStart || contiguous)
                    && char.ToLowerInvariant(candidate[candidateIndex]) == patternChar)
                {
                    lastMatchIndex = candidateIndex;
                    candidateIndex++;
                    found = true;
                    break;
                }
                candidateIndex++;
            }
            if (!found)
                return false;
        }
        return true;
    }
}
