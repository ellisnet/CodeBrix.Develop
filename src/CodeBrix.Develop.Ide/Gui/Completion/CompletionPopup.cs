//
// CompletionPopup.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.CodeCompletion.CompletionWindow, rebuilt
//      as a GTK 4 popover for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core.TypeSystem;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Completion;

/// <summary>
/// The code-completion list, shown as a popover pointing at the caret of a
/// source editor. The popover never takes keyboard focus — the editor keeps
/// receiving keystrokes while the list filters; the editor routes
/// Up/Down/Enter/Tab/Escape here while the list is visible.
/// </summary>
public class CompletionPopup
{
    const int MaxVisibleItems = 100;

    readonly Gtk.Popover popover;
    readonly Gtk.ListBox listBox;
    readonly Gtk.ScrolledWindow scrolled;
    IReadOnlyList<CodeCompletionItem> items = Array.Empty<CodeCompletionItem>();
    int selectedIndex = -1;

    /// <summary>Raised when the user commits a completion item.</summary>
    public event Action<CodeCompletionItem>? ItemCommitted;

    /// <summary>Whether the list is currently shown.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>The currently selected item, or null.</summary>
    public CodeCompletionItem? SelectedItem =>
        selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null;

    /// <summary>Creates the popup, parented to the given editor widget.</summary>
    public CompletionPopup(Gtk.Widget parent)
    {
        listBox = Gtk.ListBox.New();
        listBox.SetSelectionMode(Gtk.SelectionMode.Browse);
        // The list is keyboard-driven from the editor; rows must never take
        // focus away from the text view.
        listBox.SetCanFocus(false);

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(listBox);
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.SetMinContentHeight(180);
        scrolled.SetMaxContentHeight(320);
        scrolled.SetPropagateNaturalHeight(true);
        scrolled.SetMinContentWidth(340);

        popover = Gtk.Popover.New();
        popover.SetChild(scrolled);
        popover.SetParent(parent);
        // Autohide would grab input and swallow the very next keystroke;
        // the editor dismisses the popup itself (Escape, clicks, caret moves).
        popover.SetAutohide(false);
        popover.SetHasArrow(false);
        popover.SetPosition(Gtk.PositionType.Bottom);

        listBox.OnRowActivated += (_, args) =>
        {
            if (args.Row is not { } row)
                return;
            var index = row.GetIndex();
            if (index >= 0 && index < items.Count)
            {
                Hide();
                ItemCommitted?.Invoke(items[index]);
            }
        };
    }

    /// <summary>Shows the given items at the given caret rectangle (editor-widget coordinates).</summary>
    public void Show(Gdk.Rectangle caret, IReadOnlyList<CodeCompletionItem> completionItems)
    {
        popover.SetPointingTo(caret);
        SetItems(completionItems);
        if (items.Count == 0)
        {
            Hide();
            return;
        }
        popover.Popup();
        IsVisible = true;
    }

    /// <summary>Replaces the list contents (filter-as-you-type) without moving the popover.</summary>
    public void UpdateItems(IReadOnlyList<CodeCompletionItem> completionItems)
    {
        SetItems(completionItems);
        if (items.Count == 0)
            Hide();
    }

    /// <summary>Moves the selection by the given delta (±1 arrow, ±10 page).</summary>
    public void MoveSelection(int delta)
    {
        if (items.Count == 0)
            return;
        Select(Math.Clamp(selectedIndex + delta, 0, items.Count - 1));
    }

    /// <summary>Hides the popup.</summary>
    public void Hide()
    {
        popover.Popdown();
        IsVisible = false;
        selectedIndex = -1;
    }

    void SetItems(IReadOnlyList<CodeCompletionItem> completionItems)
    {
        items = completionItems.Count > MaxVisibleItems
            ? completionItems.Take(MaxVisibleItems).ToList()
            : completionItems;

        listBox.RemoveAll();
        foreach (var item in items)
        {
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            box.Append(ImageService.CreateImage(GetElementIconName(item)));
            var nameLabel = Gtk.Label.New(item.DisplayText);
            nameLabel.SetXalign(0);
            nameLabel.SetHexpand(true);
            box.Append(nameLabel);
            var kindLabel = Gtk.Label.New(FormatKind(item));
            kindLabel.SetXalign(1);
            kindLabel.AddCssClass("dim-label");
            box.Append(kindLabel);
            listBox.Append(box);
        }
        if (items.Count > 0)
            Select(0);
        else
            selectedIndex = -1;
    }

    void Select(int index)
    {
        selectedIndex = index;
        if (listBox.GetRowAtIndex(index) is not Gtk.ListBoxRow row)
            return;
        listBox.SelectRow(row);
        ScrollToRow(index);
    }

    // Keep the selected row visible without giving it focus (GrabFocus
    // would steal the keyboard from the editor).
    void ScrollToRow(int index)
    {
        var adjustment = listBox.GetAdjustment();
        if (adjustment == null || items.Count == 0)
            return;
        var rowHeight = adjustment.GetUpper() / items.Count;
        var rowTop = index * rowHeight;
        var rowBottom = rowTop + rowHeight;
        var value = adjustment.GetValue();
        var page = adjustment.GetPageSize();
        if (rowTop < value)
            adjustment.SetValue(rowTop);
        else if (rowBottom > value + page)
            adjustment.SetValue(rowBottom - page);
    }

    static string FormatKind(CodeCompletionItem item)
        => item.Tags is { Count: > 0 } ? item.Tags[0].ToLowerInvariant() : "";

    // Roslyn completion tags mapped to MonoDevelop element icons.
    static string GetElementIconName(CodeCompletionItem item)
    {
        var tag = item.Tags is { Count: > 0 } ? item.Tags[0] : "";
        return tag switch
        {
            "Class" or "Record" => "element-class-16",
            "Structure" or "RecordStruct" => "element-struct-16",
            "Interface" => "element-interface-16",
            "Enum" => "element-enum-16",
            "EnumMember" or "Constant" => "element-constant-16",
            "Delegate" => "element-delegate-16",
            "Event" => "element-event-16",
            "Field" => "element-field-16",
            "Property" => "element-property-16",
            "Method" or "Operator" => "element-method-16",
            "ExtensionMethod" => "element-extensionmethod-16",
            "Namespace" => "element-namespace-16",
            "Keyword" => "element-keyword-16",
            "Local" or "Parameter" or "RangeVariable" => "element-variable-16",
            "Module" => "element-module-16",
            "TypeParameter" => "element-type-16",
            "Snippet" => "element-template-16",
            _ => "element-other-declaration-16",
        };
    }
}
