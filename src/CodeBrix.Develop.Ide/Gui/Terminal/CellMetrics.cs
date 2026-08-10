//
// CellMetrics.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from the author's Lily.Shell TerminalView, re-measured
//      through Pango instead of the CodeBrix.Platform TextLayout engine)
// SPDX-License-Identifier: MIT
//

using System;
using Pango = CodeBrix.Develop.UI.Pango;

namespace CodeBrix.Develop.Ide.Gui.Terminal; //was previously: Lily.Shell.TerminalView.Rendering;

/// <summary>
/// The fixed cell geometry of the terminal grid, measured from a reference
/// glyph in the terminal font (measure "x" — for a monospaced font its
/// advance IS the cell advance). Re-measure on any font change.
/// </summary>
public readonly struct CellMetrics
{
    internal CellMetrics(double width, double height, double baseline)
    {
        Width = width;
        Height = height;
        Baseline = baseline;
    }

    /// <summary>The cell advance (width of one column) in pixels.</summary>
    public double Width { get; }

    /// <summary>The cell height (one row) in pixels.</summary>
    public double Height { get; }

    /// <summary>The text baseline offset from the top of the cell, in pixels.</summary>
    public double Baseline { get; }

    /// <summary>Measures the cell geometry of a font in a Pango context.</summary>
    public static CellMetrics Measure(Pango.Context context, Pango.FontDescription font)
    {
        var layout = Pango.Layout.New(context);
        layout.SetFontDescription(font);
        layout.SetText("x", -1);
        layout.GetPixelSize(out var width, out var height);
        var baseline = layout.GetBaseline() / (double) Pango.Constants.SCALE;

        return new CellMetrics(
            Math.Max(1, width),
            Math.Max(1, height),
            Math.Max(1, baseline));
    }
}
