//
// HoverExpression.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Core.Debugging;

/// <summary>
/// Extracts the expression to evaluate when the pointer hovers over source
/// text: the identifier under the pointer, extended left through a dotted
/// member chain (hovering "Bar" in "foo.Bar.Baz" yields "foo.Bar").
/// </summary>
public static class HoverExpression
{
    /// <summary>
    /// The hover expression at the 0-based <paramref name="column"/> of
    /// <paramref name="lineText"/>, or null when the pointer is not over an
    /// identifier.
    /// </summary>
    public static string At(string lineText, int column)
    {
        if (string.IsNullOrEmpty(lineText) || column < 0 || column >= lineText.Length)
            return null;
        if (!IsIdentifierChar(lineText[column]))
            return null;

        // The word under the pointer.
        var start = column;
        while (start > 0 && IsIdentifierChar(lineText[start - 1]))
            start--;
        var end = column + 1;
        while (end < lineText.Length && IsIdentifierChar(lineText[end]))
            end++;
        if (char.IsDigit(lineText[start]))
            return null; // a numeric literal, not an identifier

        // Extend left through a member chain: "foo.Bar.|Baz" -> "foo.Bar.Baz".
        while (start > 1 && lineText[start - 1] == '.' && IsIdentifierChar(lineText[start - 2]))
        {
            start--; // the dot
            while (start > 0 && IsIdentifierChar(lineText[start - 1]))
                start--;
        }
        if (char.IsDigit(lineText[start]))
            return null; // e.g. hovering "5.ToString" territory — skip

        return lineText.Substring(start, end - start);
    }

    static bool IsIdentifierChar(char value) => char.IsLetterOrDigit(value) || value == '_';
}
