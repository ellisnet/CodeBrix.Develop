//
// SelectionGeometry.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from the author's Lily.Shell TerminalView)
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Ide.Gui.Terminal; //was previously: Lily.Shell.TerminalView.Rendering;

/// <summary>
/// Pure geometry for mouse selection: pixel-to-cell hit testing and the
/// per-row column span of a selection whose endpoints are in buffer-absolute
/// coordinates (the SelectionService convention: X = column, Y = absolute
/// row including scrollback; the end cell is inclusive).
/// </summary>
internal static class SelectionGeometry
{
    /// <summary>
    /// Maps a point in pixels to a (column, viewport row) cell, clamped to
    /// the grid so dragging past an edge selects to that edge.
    /// </summary>
    public static (int Column, int Row) ToCell(double x, double y, CellMetrics cell,
        int columns, int rows)
    {
        var column = (int) Math.Floor(x / cell.Width);
        var row = (int) Math.Floor(y / cell.Height);
        return (Math.Clamp(column, 0, columns - 1), Math.Clamp(row, 0, rows - 1));
    }

    /// <summary>
    /// Computes the selected column span of one absolute buffer row, given
    /// selection endpoints in buffer-absolute coordinates (either order).
    /// Returns false when the row is outside the selection. The normalized
    /// end column is EXCLUSIVE — matching GetSelectedText, so the highlight
    /// always equals the copied text; the returned columns are inclusive.
    /// </summary>
    public static bool TryGetRowSpan(
        int startColumn, int startRow, int endColumn, int endRow,
        int absoluteRow, int columns,
        out int firstColumn, out int lastColumn)
    {
        //Normalize so (startRow, startColumn) is the earlier endpoint
        if (startRow > endRow || (startRow == endRow && startColumn > endColumn))
        {
            (startColumn, endColumn) = (endColumn, startColumn);
            (startRow, endRow) = (endRow, startRow);
        }

        firstColumn = 0;
        lastColumn = 0;

        if (absoluteRow < startRow || absoluteRow > endRow) { return false; }

        firstColumn = absoluteRow == startRow ? startColumn : 0;
        lastColumn = absoluteRow == endRow ? endColumn - 1 : columns - 1;

        firstColumn = Math.Clamp(firstColumn, 0, columns - 1);
        lastColumn = Math.Clamp(lastColumn, 0, columns - 1);
        return firstColumn <= lastColumn;
    }
}
