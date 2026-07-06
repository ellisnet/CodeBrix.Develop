//
// OutputPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop's Build Output / Application Output pads)
// SPDX-License-Identifier: MIT
//

using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>
/// A read-only, monospace, auto-scrolling text pad used for build output
/// and running-application output.
/// </summary>
public class OutputPad
{
    readonly Gtk.ScrolledWindow scrolled;
    readonly Gtk.TextView textView;
    readonly Gtk.TextBuffer buffer;

    /// <summary>Creates an empty output pad.</summary>
    public OutputPad()
    {
        textView = Gtk.TextView.New();
        textView.SetEditable(false);
        textView.SetCursorVisible(false);
        textView.SetMonospace(true);
        textView.SetLeftMargin(6);
        textView.SetTopMargin(4);
        buffer = textView.GetBuffer();

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(textView);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);
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
        buffer.Insert(end, line + "\n", -1);
        buffer.GetEndIter(out end);
        buffer.PlaceCursor(end);
        textView.ScrollToMark(buffer.GetInsert(), 0, false, 0, 1);
    }
}
