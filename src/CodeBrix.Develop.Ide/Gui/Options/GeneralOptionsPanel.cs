//
// GeneralOptionsPanel.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using Gio = CodeBrix.Develop.UI.Gio;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// The General options page: the project folder location where the user's
/// projects normally live. Blank means the user's Documents folder; a
/// configured folder that does not exist is silently ignored (and blanked)
/// in favor of Documents.
/// </summary>
public class GeneralOptionsPanel : OptionsPanel
{
    Gtk.Entry? folderEntry;

    /// <inheritdoc/>
    public override Gtk.Widget CreatePanelWidget()
    {
        var heading = Gtk.Label.New("Projects");
        heading.AddCssClass("heading");
        heading.SetXalign(0);

        var folderLabel = Gtk.Label.New("Project folder location:");
        folderLabel.SetXalign(0);

        folderEntry = Gtk.Entry.New();
        folderEntry.SetText(IdePreferences.ProjectsFolder.Value);
        folderEntry.SetHexpand(true);

        var browseButton = Gtk.Button.NewWithLabel("Browse…");
        browseButton.OnClicked += (_, _) => _ = BrowseAsync();

        var folderRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        folderRow.Append(folderEntry);
        folderRow.Append(browseButton);

        var description = Gtk.Label.New(
            "Projects are normally kept in this folder. Leave it blank to use your\n" +
            "Documents folder. If the folder entered here does not exist, the setting\n" +
            "is silently cleared and Documents is used instead.");
        description.SetXalign(0);
        description.AddCssClass("dim-label");

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.Append(heading);
        box.Append(folderLabel);
        box.Append(folderRow);
        box.Append(description);
        return box;
    }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges() =>
        folderEntry != null && folderEntry.GetText().Trim() != IdePreferences.ProjectsFolder.Value;

    /// <inheritdoc/>
    public override void ApplyChanges()
    {
        if (folderEntry != null)
            IdePreferences.ProjectsFolder.Value = folderEntry.GetText().Trim();
    }

    async Task BrowseAsync()
    {
        if (ParentDialog == null || folderEntry == null)
            return;
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle("Select Project Folder");
            dialog.SetInitialFolder(Gio.FileHelper.NewForPath(IdeApp.GetProjectsDirectory()));

            Gio.File? folder;
            try
            {
                folder = await dialog.SelectFolderAsync(ParentDialog.Window);
            }
            catch (Exception)
            {
                return; // dialog dismissed
            }
            if (folder?.GetPath() is string path)
                folderEntry.SetText(path);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Project folder selection failed", ex);
        }
    }
}
