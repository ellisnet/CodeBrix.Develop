//
// CellStyle.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from the author's Lily.Shell TerminalView, from Skia
//      rendering to Cairo/Pango for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Ide.Gui.Terminal; //was previously: Lily.Shell.TerminalView.Rendering;

/// <summary>
/// The resolved drawing style of one terminal cell (or run of cells sharing
/// an attribute): concrete colors plus the type-face and decoration flags.
/// Produced by <see cref="AttributeDecoder"/>.
/// </summary>
public readonly record struct CellStyle(
    TerminalColor Foreground,
    TerminalColor Background,
    bool Bold,
    bool Italic,
    bool Underline,
    bool CrossedOut)
{
    /// <summary>True when the background differs from the terminal default and needs a fill rect.</summary>
    public bool HasVisibleBackground(TerminalColor defaultBackground) => Background != defaultBackground;
}
