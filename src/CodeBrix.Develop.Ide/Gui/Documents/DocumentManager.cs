//
// DocumentManager.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Documents.DocumentManager, rebuilt
//      on a GTK 4 notebook for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Documents;

/// <summary>
/// Manages the open editor documents as tabs of a notebook: opening,
/// switching, saving, and closing.
/// </summary>
public class DocumentManager
{
    readonly Gtk.Notebook notebook;
    readonly List<EditorDocument> documents = new();

    /// <summary>Creates an empty document area.</summary>
    public DocumentManager()
    {
        notebook = Gtk.Notebook.New();
        notebook.SetScrollable(true);
        notebook.SetHexpand(true);
        notebook.SetVexpand(true);
    }

    /// <summary>The widget to place in the workbench.</summary>
    public Gtk.Widget Widget => notebook;

    /// <summary>The open documents.</summary>
    public IReadOnlyList<EditorDocument> Documents => documents;

    /// <summary>The document of the selected tab, or null when none are open.</summary>
    public EditorDocument? ActiveDocument
    {
        get
        {
            var page = notebook.GetCurrentPage();
            if (page < 0)
                return null;
            var child = notebook.GetNthPage(page);
            return documents.FirstOrDefault(d => d.Widget == child);
        }
    }

    /// <summary>Opens the given file, or switches to it when already open.</summary>
    public EditorDocument OpenDocument(FilePath fileName)
    {
        fileName = fileName.FullPath;
        var existing = documents.FirstOrDefault(d => d.FileName == fileName);
        if (existing != null)
        {
            notebook.SetCurrentPage(notebook.PageNum(existing.Widget));
            existing.Focus();
            return existing;
        }

        var document = new EditorDocument(fileName);
        documents.Add(document);
        var page = notebook.AppendPage(document.Widget, CreateTabLabel(document));
        notebook.SetTabReorderable(document.Widget, true);
        notebook.SetCurrentPage(page);
        document.Focus();
        return document;
    }

    Gtk.Widget CreateTabLabel(EditorDocument document)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        var label = Gtk.Label.New(document.FileName.FileName);
        box.Append(label);

        var closeButton = Gtk.Button.NewFromIconName("window-close-symbolic");
        closeButton.SetHasFrame(false);
        closeButton.OnClicked += (_, _) => CloseDocument(document);
        box.Append(closeButton);

        document.ModifiedChanged += () =>
            label.SetText(document.IsModified ? $"{document.FileName.FileName} •" : document.FileName.FileName);
        return box;
    }

    /// <summary>Closes the given document, saving it first when modified.</summary>
    public void CloseDocument(EditorDocument document)
    {
        // First-pass policy: never lose work — modified documents are saved
        // on close (a save/discard prompt comes later).
        document.Save();
        var page = notebook.PageNum(document.Widget);
        if (page >= 0)
            notebook.RemovePage(page);
        documents.Remove(document);
    }

    /// <summary>Saves the active document.</summary>
    public void SaveActive() => ActiveDocument?.Save();

    /// <summary>Saves all open documents.</summary>
    public void SaveAll()
    {
        foreach (var document in documents)
            document.Save();
    }
}
