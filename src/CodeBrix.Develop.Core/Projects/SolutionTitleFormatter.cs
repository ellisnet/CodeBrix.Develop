//
// SolutionTitleFormatter.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Text;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>How a solution's name is spelled out where it is displayed.</summary>
public static class SolutionTitleFormatter
{
    /// <summary>
    /// The solution name with spaces put in: dots become spaces, and a
    /// TitleCase run is separated into words — "Doom.Brix" reads as
    /// "Doom Brix", "WikipediaPublisher" as "Wikipedia Publisher". Digits
    /// stay with the word they follow, and separators the name already has
    /// (spaces, "-", "_") are left alone.
    /// </summary>
    public static string WithSpaces(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name ?? "";

        var builder = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current == '.')
            {
                builder.Append(' ');
                continue;
            }
            if (index > 0 && char.IsUpper(current) && StartsWord(name, index))
                builder.Append(' ');
            builder.Append(current);
        }
        return CollapseSpaces(builder.ToString());
    }

    // An upper-case letter starts a new word when it follows a lower-case
    // letter or a digit ("...aB"), or when it is the last capital of a run
    // that carries on in lower case ("HTTPServer" reads as "HTTP Server").
    static bool StartsWord(string name, int index)
    {
        var previous = name[index - 1];
        if (char.IsLower(previous) || char.IsDigit(previous))
            return true;
        return char.IsUpper(previous) && index + 1 < name.Length && char.IsLower(name[index + 1]);
    }

    // A name that already had spacing of its own ("Doom. Brix") would come
    // out of the loop with runs of spaces.
    static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var character in text)
        {
            var isSpace = character == ' ';
            if (isSpace && previousWasSpace)
                continue;
            builder.Append(character);
            previousWasSpace = isSpace;
        }
        return builder.ToString().Trim();
    }
}
