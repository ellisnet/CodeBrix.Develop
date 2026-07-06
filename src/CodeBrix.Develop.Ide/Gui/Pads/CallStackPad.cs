//
// CallStackPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Debugging;
using Gio = CodeBrix.Develop.UI.Gio;
using GObject = CodeBrix.Develop.UI.GObject;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>
/// One row of the Call Stack pad, as a GObject list-model item.
/// </summary>
[GObject.Subclass<GObject.Object>]
public partial class CallStackFrameNode
{
    /// <summary>The text shown for the frame.</summary>
    public string Title { get; set; } = "";

    /// <summary>The source file, or "" when the frame has no source.</summary>
    public string File { get; set; } = "";

    /// <summary>The 1-based source line.</summary>
    public int Line { get; set; }

    /// <summary>Whether this is the top (execution) frame.</summary>
    public bool IsTopFrame { get; set; }

    /// <summary>Creates a node for a stack frame.</summary>
    public static CallStackFrameNode Create(StackFrameInfo frame, bool isTopFrame)
    {
        var node = NewWithProperties(Array.Empty<GObject.ConstructArgument>());
        node.Title = frame.File.Length > 0
            ? $"{frame.Name} — {Path.GetFileName(frame.File)}:{frame.Line}"
            : frame.Name;
        node.File = frame.File;
        node.Line = frame.Line;
        node.IsTopFrame = isTopFrame;
        return node;
    }
}

/// <summary>
/// The Call Stack pad, shown beside the output pads while debugging: the
/// paused thread's frames, top-most first. Activating a frame navigates to
/// its source location.
/// </summary>
public class CallStackPad
{
    readonly Gtk.ScrolledWindow scrolled;
    readonly Gtk.ListView listView;
    readonly Gio.ListStore store;
    readonly Gtk.SignalListItemFactory factory;

    /// <summary>Raised when the user activates a frame that has a source file.</summary>
    public event Action<FilePath, int>? FrameActivated;

    /// <summary>Creates the (initially empty) pad.</summary>
    public CallStackPad()
    {
        store = Gio.ListStore.New(CallStackFrameNode.GetGType());
        var selection = Gtk.SingleSelection.New(store);

        factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += static (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            box.SetMarginStart(8);
            box.SetMarginTop(1);
            box.SetMarginBottom(1);
            var image = Gtk.Image.New();
            image.SetPixelSize(15);
            box.Append(image);
            var label = Gtk.Label.New(null);
            label.SetXalign(0);
            box.Append(label);
            listItem.SetChild(box);
        };
        factory.OnBind += static (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            if (listItem.GetItem() is not CallStackFrameNode node)
                return;
            var box = (Gtk.Box) listItem.GetChild()!;
            var image = (Gtk.Image) box.GetFirstChild()!;
            var label = (Gtk.Label) image.GetNextSibling()!;
            image.SetFromPaintable(ImageService.GetIcon(node.IsTopFrame ? "gutter-execution-15" : "gutter-stack-15"));
            label.SetText(node.Title);
        };

        listView = Gtk.ListView.New(selection, factory);
        listView.OnActivate += (_, args) =>
        {
            if (store.GetObject(args.Position) is CallStackFrameNode node && node.File.Length > 0)
                FrameActivated?.Invoke(new FilePath(node.File), node.Line);
        };

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(listView);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);
        scrolled.AddCssClass("cb-output");
    }

    /// <summary>The widget to place in the bottom pad area.</summary>
    public Gtk.Widget Widget => scrolled;

    /// <summary>Shows the given frames (top-most first).</summary>
    public void ShowFrames(IReadOnlyList<StackFrameInfo> frames)
    {
        store.RemoveAll();
        for (var i = 0; i < frames.Count; i++)
            store.Append(CallStackFrameNode.Create(frames[i], isTopFrame: i == 0));
    }

    /// <summary>Empties the pad (the debuggee resumed or the session ended).</summary>
    public void Clear() => store.RemoveAll();
}
