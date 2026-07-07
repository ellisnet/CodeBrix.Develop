//
// CompletionController.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (modern completion-as-you-type: auto-trigger on typed characters,
//      filter while typing, commit with Enter/Tab — no key chord needed;
//      also drives the signature-help bubble)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Core.Xaml;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Completion;

/// <summary>
/// Runs the completion session of one editor document: decides when typing
/// should open the list, filters it as the user keeps typing, routes
/// navigation/commit keys while it is open, and shows/updates the
/// signature-help bubble inside argument lists. C# is served by Roslyn,
/// XAML by the metadata-index-driven XAML services.
/// </summary>
public class CompletionController
{
    readonly FilePath fileName;
    readonly bool isCSharp;
    readonly bool isXaml;
    readonly Func<string> getText;
    readonly Func<int> getCaretUtf16Offset;
    readonly Func<Gdk.Rectangle> getCaretRectangle;
    readonly Action<CodeCompletionItem> commitItem;
    readonly CompletionPopup popup;
    readonly SignatureHelpPopover signatureHelp;
    readonly SynchronizationContext? uiContext;

    IReadOnlyList<CodeCompletionItem> unfilteredItems = Array.Empty<CodeCompletionItem>();
    bool sessionActive;
    int anchorOffset;      // UTF-16 offset where the filter span starts
    int version;           // bumped on every edit/dismiss
    int triggerSequence;   // only the LATEST trigger request may show its results
    int signatureSequence; // dito for signature help

    string? pendingInsertedText;
    bool pendingDelete;
    int suppressEdits;

    /// <summary>Creates the controller and its popups, parented to the editor view.</summary>
    public CompletionController(
        Gtk.Widget view, FilePath fileName, bool isCSharp, bool isXaml,
        Func<string> getText, Func<int> getCaretUtf16Offset, Func<Gdk.Rectangle> getCaretRectangle,
        Action<CodeCompletionItem> commitItem)
    {
        this.fileName = fileName;
        this.isCSharp = isCSharp;
        this.isXaml = isXaml;
        this.getText = getText;
        this.getCaretUtf16Offset = getCaretUtf16Offset;
        this.getCaretRectangle = getCaretRectangle;
        this.commitItem = commitItem;
        uiContext = SynchronizationContext.Current;

        popup = new CompletionPopup(view);
        popup.ItemCommitted += Commit;
        signatureHelp = new SignatureHelpPopover(view);
    }

    /// <summary>Whether this document supports completion at all.</summary>
    public bool IsSupported => isCSharp || isXaml;

    /// <summary>Buffer insert-text notification (fires before the change lands).</summary>
    public void NotifyInsert(string text) => pendingInsertedText = text;

    /// <summary>Buffer delete-range notification (fires before the change lands).</summary>
    public void NotifyDelete() => pendingDelete = true;

    /// <summary>
    /// Insert-mark movement notification. A caret move that is NOT part of
    /// an edit (mouse click, arrow keys, Home/End) dismisses the popups.
    /// </summary>
    public void NotifyCaretMoved()
    {
        if (pendingInsertedText == null && !pendingDelete && suppressEdits == 0)
            DismissAll();
    }

    /// <summary>Dismisses both popups (scrolling, focus loss, clicks).</summary>
    public void DismissAll()
    {
        version++;
        sessionActive = false;
        popup.Hide();
        signatureHelp.Hide();
    }

    /// <summary>
    /// Buffer changed notification (fires after the change landed):
    /// refilters or dismisses an active session, auto-triggers a new one,
    /// and keeps signature help current.
    /// </summary>
    public void ProcessEdit()
    {
        var insertedText = pendingInsertedText;
        var wasDelete = pendingDelete;
        pendingInsertedText = null;
        pendingDelete = false;
        version++;
        if (suppressEdits > 0 || !IsSupported)
            return;

        var typedChar = insertedText is { Length: 1 } ? insertedText[0] : '\0';
        var text = getText();
        var caret = getCaretUtf16Offset();

        if (sessionActive)
        {
            if (caret < anchorOffset)
            {
                DismissCompletion();
            }
            else
            {
                var filterText = text.Substring(anchorOffset, Math.Min(caret, text.Length) - anchorOffset);
                if (IsValidFilterText(filterText))
                {
                    var filtered = CompletionFilter.Filter(unfilteredItems, filterText);
                    if (filtered.Count > 0)
                        popup.UpdateItems(filtered);
                    else
                        DismissCompletion();
                }
                else
                {
                    DismissCompletion();
                }
            }
        }

        // Falls through after a dismissal so "Console." immediately opens
        // the member list even though '.' ended the previous session.
        // A letter only triggers at the START of a word: letters typed
        // mid-word must not fire fresh (empty) requests, because each new
        // request supersedes the in-flight one that has the real items.
        if (!sessionActive && typedChar != '\0' && !wasDelete)
        {
            var previous = caret >= 2 ? text[Math.Min(caret, text.Length) - 2] : '\0';
            var startsWord = !char.IsLetterOrDigit(previous) && previous != '_';
            if (isCSharp && (typedChar == '.'
                || ((typedChar == '_' || char.IsLetter(typedChar)) && startsWord)))
            {
                _ = TriggerAsync(text, caret, explicitInvocation: false, typedChar);
            }
            else if (isXaml && XamlCompletionService.MayTriggerCompletion(typedChar)
                && (!char.IsLetter(typedChar) || (startsWord && previous != '.' && previous != ':' && previous != '-')))
            {
                _ = TriggerAsync(text, caret, explicitInvocation: false, typedChar);
            }
        }

        if (isCSharp && (typedChar == '(' || typedChar == ',' || signatureHelp.IsVisible))
            _ = UpdateSignatureHelpAsync(text, caret);
    }

    /// <summary>Explicit completion request (Ctrl+Space / the Edit menu).</summary>
    public void Invoke()
    {
        if (!IsSupported)
        {
            IdeApp.Workbench?.ShowStatus("Code completion is available for C# and XAML files");
            return;
        }
        if (!TypeSystemService.IsWorkspaceLoaded)
        {
            IdeApp.Workbench?.ShowStatus("The type system is still loading...");
            return;
        }
        _ = TriggerAsync(getText(), getCaretUtf16Offset(), explicitInvocation: true, '\0');
    }

    /// <summary>
    /// Handles a navigation/commit key. Returns true when the key was
    /// consumed (a popup was open and reacted); false lets the editor
    /// process the key normally.
    /// </summary>
    public bool HandleKey(string key)
    {
        if (popup.IsVisible)
        {
            switch (key)
            {
                case "Up":
                    popup.MoveSelection(-1);
                    return true;
                case "Down":
                    popup.MoveSelection(1);
                    return true;
                case "Page_Up":
                    popup.MoveSelection(-10);
                    return true;
                case "Page_Down":
                    popup.MoveSelection(10);
                    return true;
                case "Return":
                case "KP_Enter":
                case "Tab":
                    if (popup.SelectedItem is { } item)
                    {
                        Commit(item);
                        return true;
                    }
                    break;
                case "Escape":
                    DismissCompletion();
                    return true;
            }
            return false;
        }
        if (key == "Escape" && signatureHelp.IsVisible)
        {
            signatureHelp.Hide();
            return true;
        }
        return false;
    }

    void DismissCompletion()
    {
        sessionActive = false;
        popup.Hide();
    }

    void Commit(CodeCompletionItem item)
    {
        DismissCompletion();
        version++;
        suppressEdits++;
        try
        {
            commitItem(item);
        }
        finally
        {
            suppressEdits--;
        }
        // Committing a XAML attribute leaves the caret inside its fresh
        // quotes; offer the values right away.
        if (isXaml && item.CaretBack == 1 && item.InsertionText != null
            && item.InsertionText.EndsWith("=\"\"", StringComparison.Ordinal))
        {
            uiContext?.Post(_ => Invoke(), null);
        }
    }

    async Task TriggerAsync(string text, int caretOffset, bool explicitInvocation, char typedChar)
    {
        var requestSequence = ++triggerSequence;
        try
        {
            IReadOnlyList<CodeCompletionItem> items;
            if (isCSharp)
            {
                items = await Task.Run(async () =>
                {
                    if (!TypeSystemService.IsWorkspaceLoaded)
                        return Array.Empty<CodeCompletionItem>();
                    if (!explicitInvocation
                        && !TypeSystemService.ShouldTriggerCompletion(fileName, text, caretOffset, typedChar))
                        return Array.Empty<CodeCompletionItem>();
                    return await TypeSystemService.GetCompletionsAsync(fileName, text, caretOffset).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            else
            {
                var reason = explicitInvocation ? XamlCompletionReason.Invocation : XamlCompletionReason.TypedChar;
                items = await Task.Run(() =>
                    XamlLanguageService.GetCompletionsAsync(fileName, text, caretOffset, reason, typedChar)).ConfigureAwait(false);
            }

            uiContext?.Post(_ =>
            {
                // Only the latest request may present; but a result computed
                // a few keystrokes ago is still good — the user merely kept
                // typing inside the same word, and the filter below
                // re-anchors it against the CURRENT text. Anything that
                // invalidated the anchor fails the checks and is dropped.
                if (requestSequence != triggerSequence)
                    return;
                if (items.Count == 0)
                {
                    if (explicitInvocation)
                        IdeApp.Workbench?.ShowStatus("No completions here");
                    return;
                }
                var anchor = items.Min(i => i.ReplacementStart);
                var currentText = getText();
                var currentCaret = getCaretUtf16Offset();
                if (anchor > currentCaret || anchor < 0 || currentCaret > currentText.Length)
                    return;
                var filterText = currentText.Substring(anchor, currentCaret - anchor);
                if (!IsValidFilterText(filterText))
                    return;
                var filtered = CompletionFilter.Filter(items, filterText);
                if (filtered.Count == 0)
                    return;
                unfilteredItems = items;
                anchorOffset = anchor;
                sessionActive = true;
                popup.Show(getCaretRectangle(), filtered);
            }, null);
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Completion request for {fileName.FileName} failed", ex);
        }
    }

    async Task UpdateSignatureHelpAsync(string text, int caretOffset)
    {
        var requestSequence = ++signatureSequence;
        try
        {
            var result = await Task.Run(() =>
                TypeSystemService.IsWorkspaceLoaded
                    ? TypeSystemService.GetSignatureHelpAsync(fileName, text, caretOffset)
                    : Task.FromResult<SignatureHelpResult?>(null)).ConfigureAwait(false);

            uiContext?.Post(_ =>
            {
                if (requestSequence != signatureSequence)
                    return;
                var currentCaret = getCaretUtf16Offset();
                if (result == null || currentCaret < result.SpanStart || currentCaret > result.SpanEnd)
                {
                    signatureHelp.Hide();
                    return;
                }
                signatureHelp.Show(getCaretRectangle(), result);
            }, null);
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Signature help for {fileName.FileName} failed", ex);
        }
    }

    bool IsValidFilterText(string filterText)
    {
        foreach (var c in filterText)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                continue;
            if (isXaml && (c == '.' || c == ':' || c == '-'))
                continue;
            return false;
        }
        return true;
    }
}
