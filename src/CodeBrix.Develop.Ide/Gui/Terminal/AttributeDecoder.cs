//
// AttributeDecoder.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from the author's Lily.Shell TerminalView, from Skia
//      rendering to Cairo/Pango for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using CodeBrix.Terminal.Engine;

namespace CodeBrix.Develop.Ide.Gui.Terminal; //was previously: Lily.Shell.TerminalView.Rendering;

/// <summary>
/// Unpacks a CodeBrix.Terminal packed cell attribute into a drawable
/// <see cref="CellStyle"/>. The packed layout (from CharData/CharacterAttribute):
/// bits 0-8 background color index, bits 9-17 foreground color index,
/// bits 18+ the FLAGS enum. Index 256 is the default color, 257 the inverted
/// default; 0-255 index the ANSI palette.
/// </summary>
public static class AttributeDecoder
{
    const int DefaultColorIndex = 256;   //Renderer.DefaultColor
    const int InvertedColorIndex = 257;  //Renderer.InvertedDefaultColor

    /// <summary>
    /// Decodes a packed attribute against the view's default colors.
    /// </summary>
    public static CellStyle Decode(int attribute, TerminalColor defaultForeground, TerminalColor defaultBackground)
    {
        var flags = (FLAGS) (attribute >> 18);
        var fgIndex = (attribute >> 9) & 0x1ff;
        var bgIndex = attribute & 0x1ff;

        //Classic bold-as-bright: BOLD promotes the dark palette (0-7) to bright (8-15)
        if (flags.HasFlag(FLAGS.BOLD) && fgIndex < 8)
        {
            fgIndex += 8;
        }

        var foreground = Resolve(fgIndex, defaultForeground, defaultBackground);
        var background = Resolve(bgIndex, defaultBackground, defaultForeground);

        if (flags.HasFlag(FLAGS.INVERSE))
        {
            (foreground, background) = (background, foreground);
        }

        if (flags.HasFlag(FLAGS.DIM))
        {
            foreground = Dim(foreground);
        }

        if (flags.HasFlag(FLAGS.INVISIBLE))
        {
            foreground = background;
        }

        return new CellStyle(
            foreground,
            background,
            flags.HasFlag(FLAGS.BOLD),
            flags.HasFlag(FLAGS.ITALIC),
            flags.HasFlag(FLAGS.UNDERLINE),
            flags.HasFlag(FLAGS.CrossedOut));
    }

    static TerminalColor Resolve(int index, TerminalColor defaultColor, TerminalColor invertedDefault)
    {
        if (index == DefaultColorIndex) { return defaultColor; }
        if (index == InvertedColorIndex) { return invertedDefault; }

        if (index >= 0 && index < Color.DefaultAnsiColors.Count)
        {
            var c = Color.DefaultAnsiColors[index];
            return new TerminalColor(c.Red, c.Green, c.Blue);
        }

        return defaultColor;
    }

    static TerminalColor Dim(TerminalColor color) =>
        new((byte) (color.Red * 0.6), (byte) (color.Green * 0.6), (byte) (color.Blue * 0.6));
}
