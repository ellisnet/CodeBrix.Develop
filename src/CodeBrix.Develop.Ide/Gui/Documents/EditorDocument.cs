//
// EditorDocument.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Document + SourceEditorView, rebuilt
//      on GtkSourceView 5 for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Ide.Gui.Completion;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;
using GtkSource = CodeBrix.Develop.UI.GtkSource;

namespace CodeBrix.Develop.Ide.Gui.Documents;

/// <summary>
/// An open source-file document: a GtkSourceView editor bound to a file on
/// disk, with syntax highlighting and Roslyn code completion for C#.
/// </summary>
public class EditorDocument
{
    readonly Gtk.ScrolledWindow scrolled;
    readonly GtkSource.View view;
    readonly GtkSource.Buffer buffer;
    readonly CompletionPopup completionPopup;

    /// <summary>The file shown in this document.</summary>
    public FilePath FileName { get; }

    /// <summary>Raised when the buffer's modified state flips.</summary>
    public event Action? ModifiedChanged;

    /// <summary>Whether the buffer has unsaved changes.</summary>
    public bool IsModified => buffer.GetModified();

    /// <summary>Whether this document contains C# source (and gets Roslyn services).</summary>
    public bool IsCSharp => FileName.HasExtension(".cs");

    /// <summary>The widget to place in the document area.</summary>
    public Gtk.Widget Widget => scrolled;

    /// <summary>Loads the given file into a new editor document.</summary>
    public EditorDocument(FilePath fileName)
    {
        FileName = fileName.FullPath;

        buffer = GtkSource.Buffer.New(null);
        ApplyLanguage();
        ApplyStyleScheme();

        buffer.SetText(File.ReadAllText(FileName), -1);
        buffer.SetModified(false);
        buffer.OnModifiedChanged += (_, _) => ModifiedChanged?.Invoke();

        view = GtkSource.View.NewWithBuffer(buffer);
        view.SetMonospace(true);
        view.SetShowLineNumbers(true);
        view.SetHighlightCurrentLine(true);
        view.SetAutoIndent(true);
        view.SetIndentWidth(4);
        view.SetTabWidth(4);
        view.SetInsertSpacesInsteadOfTabs(true);
        view.SetLeftMargin(4);

        completionPopup = new CompletionPopup(view);
        completionPopup.ItemCommitted += CommitCompletionItem;

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(view);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);
    }

    void ApplyLanguage()
    {
        var manager = GtkSource.LanguageManager.GetDefault();
        var language = manager.GuessLanguage(FileName.FileName, null);
        if (language == null && (FileName.HasExtension(".xaml") || FileName.HasExtension(".axaml")))
            language = manager.GetLanguage("xml");
        if (language != null)
            buffer.SetLanguage(language);
    }

    void ApplyStyleScheme()
    {
        var manager = GtkSource.StyleSchemeManager.GetDefault();
        var scheme = manager.GetScheme(WorkbenchTheme.PrefersDark ? "Adwaita-dark" : "Adwaita");
        if (scheme != null)
            buffer.SetStyleScheme(scheme);
    }

    /// <summary>Gives keyboard focus to the editor.</summary>
    public void Focus() => view.GrabFocus();

    /// <summary>The full buffer text.</summary>
    public string GetText()
    {
        buffer.GetBounds(out var start, out var end);
        return buffer.GetText(start, end, includeHiddenChars: true);
    }

    /// <summary>Writes the buffer to disk if modified.</summary>
    public void Save()
    {
        if (!IsModified)
            return;
        File.WriteAllText(FileName, GetText());
        buffer.SetModified(false);
    }

    /// <summary>
    /// Requests Roslyn code completion at the caret and shows the completion
    /// popup with the results.
    /// </summary>
    public async Task ShowCompletionAsync()
    {
        if (!IsCSharp || !TypeSystemService.IsWorkspaceLoaded)
        {
            IdeApp.Workbench?.ShowStatus(IsCSharp ? "The type system is still loading..." : "Code completion is available for C# files");
            return;
        }

        var text = GetText();
        buffer.GetIterAtMark(out var caret, buffer.GetInsert());
        buffer.GetBounds(out var start, out _);
        // Roslyn offsets are UTF-16 code units; a C# string's Length is too
        var utf16Offset = buffer.GetText(start, caret, includeHiddenChars: true).Length;

        var completions = await TypeSystemService.GetCompletionsAsync(FileName, text, utf16Offset);
        if (completions.Count == 0)
        {
            IdeApp.Workbench?.ShowStatus("No completions here");
            return;
        }

        view.GetIterLocation(caret, out var location);
        view.BufferToWindowCoords(Gtk.TextWindowType.Widget, location.X, location.Y, out var caretX, out var caretY);
        var rect = new Gdk.Rectangle { X = caretX, Y = caretY, Width = 1, Height = location.Height };
        completionPopup.Show(rect, completions);
    }

    void CommitCompletionItem(CodeCompletionItem item)
    {
        var text = GetText();
        var startChar = CharOffsetFromUtf16(text, item.ReplacementStart);
        var endChar = CharOffsetFromUtf16(text, item.ReplacementStart + item.ReplacementLength);

        buffer.GetIterAtOffset(out var replaceStart, startChar);
        buffer.GetIterAtOffset(out var replaceEnd, endChar);
        buffer.SelectRange(replaceStart, replaceEnd);
        buffer.DeleteSelection(interactive: false, defaultEditable: true);
        buffer.InsertAtCursor(item.InsertionText, -1);
        view.GrabFocus();
    }

    // GTK buffer offsets count Unicode code points, while Roslyn spans count
    // UTF-16 code units; surrogate pairs make the two diverge.
    static int CharOffsetFromUtf16(string text, int utf16Offset)
    {
        var charOffset = 0;
        for (var i = 0; i < utf16Offset && i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
                charOffset++;
        }
        return charOffset;
    }
}
