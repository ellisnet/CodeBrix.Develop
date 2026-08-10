//
// TextRunSegment.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from the author's Lily.Shell TerminalView)
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Ide.Gui.Terminal; //was previously: Lily.Shell.TerminalView.Rendering;

/// <summary>
/// One horizontal run of buffer cells sharing an attribute, ready to draw:
/// the starting column, the width in cells, the text, and the packed
/// attribute. Wide (two-cell) characters always form their own segment so
/// grid alignment never depends on a proportional glyph advance.
/// </summary>
public sealed class TextRunSegment
{
    /// <summary>Creates a segment.</summary>
    public TextRunSegment(int startColumn, int cellCount, string text, int attribute, bool isWide)
    {
        StartColumn = startColumn;
        CellCount = cellCount;
        Text = text;
        Attribute = attribute;
        IsWide = isWide;
    }

    /// <summary>The first buffer column the segment occupies.</summary>
    public int StartColumn { get; }

    /// <summary>How many cells the segment spans (a wide character spans two).</summary>
    public int CellCount { get; }

    /// <summary>The characters to draw.</summary>
    public string Text { get; }

    /// <summary>The packed cell attribute shared by every cell in the segment.</summary>
    public int Attribute { get; }

    /// <summary>True when this is a single wide (two-cell) character.</summary>
    public bool IsWide { get; }
}
