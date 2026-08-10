//
// TerminalColor.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using Cairo = CodeBrix.Develop.UI.Cairo;

namespace CodeBrix.Develop.Ide.Gui.Terminal;

/// <summary>An opaque RGB color of the terminal's palette.</summary>
public readonly record struct TerminalColor(byte Red, byte Green, byte Blue)
{
    /// <summary>Sets the color as the Cairo context's source.</summary>
    public void Apply(Cairo.Context cr) =>
        cr.SetSourceRgb(Red / 255.0, Green / 255.0, Blue / 255.0);
}
