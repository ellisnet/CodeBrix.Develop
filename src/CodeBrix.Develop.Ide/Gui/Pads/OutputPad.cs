//
// OutputPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop's Build Output / Application Output pads)
// SPDX-License-Identifier: MIT
//

using CodeBrix.Develop.Ide.Themes;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>Semantic colors for output-pad text, resolved from the current theme.</summary>
public enum OutputColor
{
    /// <summary>The normal foreground color.</summary>
    Normal,
    /// <summary>The theme's "good" (success) color.</summary>
    Good,
    /// <summary>The theme's warning color.</summary>
    Warning,
    /// <summary>The theme's error color.</summary>
    Bad,
}

/// <summary>
/// A read-only, monospace, auto-scrolling text pad used for build output,
/// running-application output, NuGet reports, and the IDE log.
/// </summary>
public class OutputPad
{
    readonly Gtk.ScrolledWindow scrolled;
    readonly Gtk.TextView textView;
    readonly Gtk.TextBuffer buffer;
    readonly Gtk.TextTag goodTag;
    readonly Gtk.TextTag warningTag;
    readonly Gtk.TextTag badTag;
    readonly bool colorizeLogLevels;

    /// <summary>
    /// Creates an empty output pad. With <paramref name="colorizeLogLevels"/>,
    /// the level token of LoggingService-formatted lines ("[time] LEVEL: …")
    /// is tinted with the current theme's good/warning/error colors.
    /// </summary>
    public OutputPad(bool colorizeLogLevels = false)
    {
        this.colorizeLogLevels = colorizeLogLevels;
        textView = Gtk.TextView.New();
        textView.SetEditable(false);
        textView.SetCursorVisible(false);
        textView.SetMonospace(true);
        textView.SetLeftMargin(6);
        textView.SetTopMargin(4);
        buffer = textView.GetBuffer();

        var tagTable = buffer.GetTagTable();
        goodTag = Gtk.TextTag.New(null);
        warningTag = Gtk.TextTag.New(null);
        badTag = Gtk.TextTag.New(null);
        tagTable.Add(goodTag);
        tagTable.Add(warningTag);
        tagTable.Add(badTag);
        ApplyThemeColors();
        // The pad lives as long as the application; no unsubscribe needed.
        ThemeService.ThemeChanged += ApplyThemeColors;

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(textView);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);
        scrolled.AddCssClass("cb-output");
    }

    /// <summary>The widget to place in the workbench.</summary>
    public Gtk.Widget Widget => scrolled;

    /// <summary>Removes all output. Must be called on the UI thread.</summary>
    public void Clear()
    {
        buffer.GetBounds(out var start, out var end);
        buffer.Delete(start, end);
    }

    /// <summary>Appends a line and scrolls to it. Must be called on the UI thread.</summary>
    public void AppendLine(string line)
    {
        buffer.GetEndIter(out var end);
        var lineOffset = end.GetOffset();
        buffer.Insert(end, line + "\n", -1);
        if (colorizeLogLevels)
            ApplyLevelTag(lineOffset, line);
        buffer.GetEndIter(out end);
        buffer.PlaceCursor(end);
        textView.ScrollToMark(buffer.GetInsert(), 0, false, 0, 1);
    }

    /// <summary>
    /// Appends one line built from colored segments and scrolls to it.
    /// Must be called on the UI thread.
    /// </summary>
    public void AppendSegments(params (string Text, OutputColor Color)[] segments)
    {
        foreach (var (text, color) in segments)
        {
            buffer.GetEndIter(out var end);
            var startOffset = end.GetOffset();
            buffer.Insert(end, text, -1);
            var tag = color switch
            {
                OutputColor.Good => goodTag,
                OutputColor.Warning => warningTag,
                OutputColor.Bad => badTag,
                _ => null,
            };
            if (tag != null)
            {
                buffer.GetIterAtOffset(out var start, startOffset);
                buffer.GetEndIter(out var segmentEnd);
                buffer.ApplyTag(tag, start, segmentEnd);
            }
        }
        buffer.GetEndIter(out var lineEnd);
        buffer.Insert(lineEnd, "\n", -1);
        buffer.GetEndIter(out lineEnd);
        buffer.PlaceCursor(lineEnd);
        textView.ScrollToMark(buffer.GetInsert(), 0, false, 0, 1);
    }

    // Retints the semantic tags from the current theme; existing lines
    // update in place because the tags are already applied to their ranges.
    void ApplyThemeColors()
    {
        var theme = ThemeService.CurrentDefinition;
        if (theme == null)
            return;
        var dark = theme.Info.IsDark;
        goodTag.Foreground = theme.GetColor(dark ? "#89D185" : "#388A34",
            "testing.iconPassed", "terminal.ansiGreen", "charts.green");
        warningTag.Foreground = theme.GetColor(dark ? "#CCA700" : "#BF8803",
            "editorWarning.foreground", "list.warningForeground", "terminal.ansiYellow");
        badTag.Foreground = theme.GetColor(dark ? "#F14C4C" : "#E51400",
            "editorError.foreground", "errorForeground", "list.errorForeground", "terminal.ansiRed");
    }

    // Tints the LEVEL token of a "[time] LEVEL: message" line: warnings get
    // the theme's warning color, errors its error color, everything else its
    // "good" color. Lines not in the log format (e.g. the continuation lines
    // of a logged exception) are left untinted.
    void ApplyLevelTag(int lineOffset, string line)
    {
        if (line.Length == 0 || line[0] != '[')
            return;
        var close = line.IndexOf("] ", System.StringComparison.Ordinal);
        if (close < 0)
            return;
        var levelStart = close + 2;
        var colon = line.IndexOf(':', levelStart);
        if (colon < 0)
            return;
        var level = line[levelStart..colon].TrimEnd();
        if (level.Length is 0 or > 8)
            return;
        foreach (var c in level)
        {
            if (c is < 'A' or > 'Z')
                return;
        }
        var tag = level switch
        {
            "WARN" or "WARNING" => warningTag,
            "ERROR" or "FATAL" => badTag,
            _ => goodTag,
        };
        buffer.GetIterAtOffset(out var start, lineOffset + levelStart);
        buffer.GetIterAtOffset(out var end, lineOffset + colon);
        buffer.ApplyTag(tag, start, end);
    }
}
