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
    Gtk.Entry? sdkRootsEntry;
    Gtk.CheckButton? allowPreviewCheck;

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

        var sdkHeading = Gtk.Label.New(".NET SDKs");
        sdkHeading.AddCssClass("heading");
        sdkHeading.SetXalign(0);
        sdkHeading.SetMarginTop(12);

        var sdkLabel = Gtk.Label.New("Additional SDK installation folders:");
        sdkLabel.SetXalign(0);

        sdkRootsEntry = Gtk.Entry.New();
        sdkRootsEntry.SetText(IdePreferences.AdditionalDotnetSdkRoots.Value);
        sdkRootsEntry.SetHexpand(true);
        // "example:" matters. A bare path here reads as a VALUE that is
        // already set — placeholder text is only dimmed, not obviously
        // different — and a user who believes the field is filled in will
        // leave it alone and wonder why nothing works.
        sdkRootsEntry.SetPlaceholderText("(none) — example: /home/you/dotnet11");

        var sdkDescription = Gtk.Label.New(
            "Separate several folders with \";\". Each is the folder holding a \"dotnet\"\n" +
            "program, such as a preview SDK installed outside the system location. A project\n" +
            "is built with the SDK that can build its target framework; the system SDK is\n" +
            "used whenever it can, so this changes nothing for ordinary projects.");
        sdkDescription.SetXalign(0);
        sdkDescription.AddCssClass("dim-label");

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.Append(heading);
        box.Append(folderLabel);
        box.Append(folderRow);
        box.Append(description);
        allowPreviewCheck = Gtk.CheckButton.NewWithLabel("Allow preview MSBuild");
        allowPreviewCheck.SetActive(IdePreferences.AllowPreviewMSBuild.Value);
        allowPreviewCheck.SetMarginTop(8);

        var previewDescription = Gtk.Label.New(
            "Off, only released .NET SDKs are used, and a solution that needs a preview\n" +
            "SDK is refused with a message rather than opened against the wrong one.\n" +
            "On, a preview SDK may be used — and its MSBuild is then what loads EVERY\n" +
            "solution this session, released ones included. Turn it on when working on\n" +
            "projects that need the preview, and off again afterwards.");
        previewDescription.SetXalign(0);
        previewDescription.AddCssClass("dim-label");

        box.Append(sdkHeading);
        box.Append(sdkLabel);
        box.Append(sdkRootsEntry);
        box.Append(sdkDescription);
        box.Append(allowPreviewCheck);
        box.Append(previewDescription);
        return box;
    }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges() =>
        (folderEntry != null && folderEntry.GetText().Trim() != IdePreferences.ProjectsFolder.Value)
        || (sdkRootsEntry != null
            && sdkRootsEntry.GetText().Trim() != IdePreferences.AdditionalDotnetSdkRoots.Value)
        || (allowPreviewCheck != null
            && allowPreviewCheck.GetActive() != IdePreferences.AllowPreviewMSBuild.Value);

    /// <inheritdoc/>
    public override void ApplyChanges()
    {
        if (folderEntry != null)
            IdePreferences.ProjectsFolder.Value = folderEntry.GetText().Trim();
        if (sdkRootsEntry != null)
            IdePreferences.AdditionalDotnetSdkRoots.Value = sdkRootsEntry.GetText().Trim();
        if (allowPreviewCheck != null)
            IdePreferences.AllowPreviewMSBuild.Value = allowPreviewCheck.GetActive();
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
