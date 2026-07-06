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
/// source editor. Arrow keys navigate, Enter commits, Escape dismisses.
/// </summary>
public class CompletionPopup
{
    const int MaxVisibleItems = 100;

    readonly Gtk.Popover popover;
    readonly Gtk.ListBox listBox;
    IReadOnlyList<CodeCompletionItem> items = Array.Empty<CodeCompletionItem>();

    /// <summary>Raised when the user commits a completion item.</summary>
    public event Action<CodeCompletionItem>? ItemCommitted;

    /// <summary>Creates the popup, parented to the given editor widget.</summary>
    public CompletionPopup(Gtk.Widget parent)
    {
        listBox = Gtk.ListBox.New();
        listBox.SetSelectionMode(Gtk.SelectionMode.Browse);

        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(listBox);
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.SetMinContentHeight(180);
        scrolled.SetMaxContentHeight(320);
        scrolled.SetPropagateNaturalHeight(true);
        scrolled.SetMinContentWidth(340);

        popover = Gtk.Popover.New();
        popover.SetChild(scrolled);
        popover.SetParent(parent);
        popover.SetAutohide(true);
        popover.SetHasArrow(false);
        popover.SetPosition(Gtk.PositionType.Bottom);

        listBox.OnRowActivated += (_, args) =>
        {
            if (args.Row is not { } row)
                return;
            var index = row.GetIndex();
            if (index >= 0 && index < items.Count)
            {
                popover.Popdown();
                ItemCommitted?.Invoke(items[index]);
            }
        };
    }

    /// <summary>Shows the given completion items at the given caret rectangle (editor-widget coordinates).</summary>
    public void Show(Gdk.Rectangle caret, IReadOnlyList<CodeCompletionItem> completionItems)
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

        popover.SetPointingTo(caret);
        popover.Popup();
        if (listBox.GetRowAtIndex(0) is Gtk.ListBoxRow first)
        {
            listBox.SelectRow(first);
            first.GrabFocus();
        }
    }

    /// <summary>Hides the popup.</summary>
    public void Hide() => popover.Popdown();

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
