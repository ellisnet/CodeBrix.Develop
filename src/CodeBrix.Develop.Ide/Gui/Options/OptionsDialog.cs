//
// OptionsDialog.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Dialogs.OptionsDialog — section list
//      on the left, lazily created panels, validate/apply on OK,
//      last-visited-page memory — rebuilt on GTK 4 for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core;
using Gtk = CodeBrix.Develop.UI.Gtk;
using Pango = CodeBrix.Develop.UI.Pango;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// The Options dialog: a section list on the left, a scrollable settings
/// page on the right. Panels are created lazily on first visit; OK
/// validates and applies every panel that was instantiated, Cancel (or
/// closing the window) reverts any live-preview effects.
/// </summary>
public class OptionsDialog
{
    readonly Gtk.Window window;
    readonly Gtk.ListBox sectionList;
    readonly Gtk.Label pageTitle;
    readonly Gtk.ScrolledWindow pageFrame;
    readonly List<PageEntry> pages = new();
    bool applied;

    sealed class PageEntry
    {
        public required OptionsSection Section { get; init; }
        public IOptionsPanel? Panel { get; set; }
        public Gtk.Widget? Widget { get; set; }
        public Gtk.ListBoxRow? Row { get; set; }
    }

    /// <summary>Creates the dialog over the given section tree.</summary>
    public OptionsDialog(Gtk.Window parent, IReadOnlyList<OptionsSection> sections)
    {
        window = Gtk.Window.New();
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetTitle("Options");
        window.SetDefaultSize(920, 640);
        window.AddCssClass("cb-options");

        // Left: the section list.
        sectionList = Gtk.ListBox.New();
        sectionList.SetSelectionMode(Gtk.SelectionMode.Browse);
        foreach (var category in sections)
        {
            sectionList.Append(CreateHeaderRow(category.Label));
            foreach (var section in category.Children.Where(child => child.PanelFactory != null))
            {
                var entry = new PageEntry { Section = section };
                var row = CreatePageRow(section);
                entry.Row = row;
                pages.Add(entry);
                sectionList.Append(row);
            }
        }
        sectionList.OnRowSelected += (_, args) =>
        {
            if (args.Row != null && FindEntry(args.Row) is { } entry)
                ShowPage(entry);
        };

        var listScroll = Gtk.ScrolledWindow.New();
        listScroll.SetChild(sectionList);
        listScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        listScroll.SetSizeRequest(220, -1);
        listScroll.AddCssClass("cb-sidebar");

        // Right: page header, scrollable page content, and the button row.
        pageTitle = Gtk.Label.New(null);
        pageTitle.SetXalign(0);
        pageTitle.SetMarginStart(16);
        pageTitle.SetMarginEnd(16);
        pageTitle.SetMarginTop(14);
        pageTitle.SetMarginBottom(10);

        pageFrame = Gtk.ScrolledWindow.New();
        pageFrame.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        pageFrame.SetHexpand(true);
        pageFrame.SetVexpand(true);

        var cancelButton = Gtk.Button.NewWithLabel("Cancel");
        cancelButton.OnClicked += (_, _) => window.Close();

        var okButton = Gtk.Button.NewWithLabel("OK");
        okButton.AddCssClass("suggested-action");
        okButton.OnClicked += (_, _) => ConfirmAndClose();

        var buttonBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttonBox.SetHalign(Gtk.Align.End);
        buttonBox.SetMarginStart(16);
        buttonBox.SetMarginEnd(16);
        buttonBox.SetMarginTop(10);
        buttonBox.SetMarginBottom(12);
        buttonBox.Append(cancelButton);
        buttonBox.Append(okButton);

        var rightBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        rightBox.Append(pageTitle);
        rightBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        rightBox.Append(pageFrame);
        rightBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        rightBox.Append(buttonBox);

        var mainBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        mainBox.Append(listScroll);
        mainBox.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));
        mainBox.Append(rightBox);
        window.SetChild(mainBox);
        window.SetDefaultWidget(okButton);

        // Titlebar close (and Escape below) count as Cancel.
        window.OnCloseRequest += (_, _) =>
        {
            if (!applied)
                CancelInstantiatedPanels();
            return false;
        };
        var escape = Gtk.Shortcut.New(
            Gtk.ShortcutTrigger.ParseString("Escape"),
            Gtk.NamedAction.New("window.close"));
        var shortcuts = Gtk.ShortcutController.New();
        shortcuts.AddShortcut(escape);
        window.AddController(shortcuts);
    }

    /// <summary>
    /// The window hosting the dialog; panels use it as the parent for file
    /// and alert dialogs they raise.
    /// </summary>
    public Gtk.Window Window => window;

    /// <summary>Whether any instantiated panel holds changes not yet applied.</summary>
    public bool HasUnsavedChanges => pages.Any(page => page.Panel != null && page.Panel.HasUnsavedChanges());

    /// <summary>
    /// Validates and applies every instantiated panel — the OK behavior
    /// without closing the dialog. Returns false (showing the offending
    /// page) when validation fails.
    /// </summary>
    public bool TryApplyAllChanges()
    {
        foreach (var page in pages)
        {
            if (page.Panel != null && !page.Panel.ValidateChanges())
            {
                if (page.Row != null)
                    sectionList.SelectRow(page.Row);
                return false; // stay open on the offending page
            }
        }
        foreach (var page in pages)
            page.Panel?.ApplyChanges();
        applied = true;
        RememberCurrentPage();
        return true;
    }

    /// <summary>Shows the dialog, restoring the last-visited page.</summary>
    public void Present()
    {
        var lastPage = IdePreferences.OptionsLastPage.Value;
        var entry = pages.FirstOrDefault(page => page.Section.Id == lastPage) ?? pages.FirstOrDefault();
        if (entry?.Row != null)
            sectionList.SelectRow(entry.Row); // triggers ShowPage
        window.Present();
    }

    static Gtk.Widget CreateHeaderRow(string label)
    {
        var header = Gtk.Label.New(label);
        header.SetXalign(0);
        header.AddCssClass("heading");
        header.SetMarginStart(10);
        header.SetMarginTop(10);
        header.SetMarginBottom(4);
        var row = Gtk.ListBoxRow.New();
        row.SetChild(header);
        row.SetSelectable(false);
        row.SetActivatable(false);
        return row;
    }

    static Gtk.ListBoxRow CreatePageRow(OptionsSection section)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        box.SetMarginStart(22);
        box.SetMarginEnd(10);
        box.SetMarginTop(5);
        box.SetMarginBottom(5);
        if (section.IconName != null)
            box.Append(ImageService.CreateImage(section.IconName));
        var label = Gtk.Label.New(section.Label);
        label.SetXalign(0);
        box.Append(label);
        var row = Gtk.ListBoxRow.New();
        row.SetChild(box);
        return row;
    }

    PageEntry? FindEntry(Gtk.ListBoxRow row) => pages.FirstOrDefault(page => page.Row == row);

    void ShowPage(PageEntry entry)
    {
        if (entry.Panel == null)
        {
            try
            {
                entry.Panel = entry.Section.PanelFactory!();
                entry.Panel.Initialize(this);
                entry.Widget = entry.Panel.CreatePanelWidget();
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Could not create options panel '{entry.Section.Id}'", ex);
                entry.Widget = Gtk.Label.New($"This page could not be loaded: {ex.Message}");
            }
        }

        pageTitle.SetMarkup($"<span weight=\"bold\" size=\"large\">{GLibMarkupEscape(entry.Section.Label)}</span>");
        pageFrame.SetChild(entry.Widget);
    }

    void ConfirmAndClose()
    {
        if (TryApplyAllChanges())
            window.Close();
    }

    void CancelInstantiatedPanels()
    {
        foreach (var page in pages)
            page.Panel?.CancelChanges();
        RememberCurrentPage();
    }

    void RememberCurrentPage()
    {
        if (sectionList.GetSelectedRow() is { } row && FindEntry(row) is { } entry)
            IdePreferences.OptionsLastPage.Value = entry.Section.Id;
    }

    static string GLibMarkupEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
