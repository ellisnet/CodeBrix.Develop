//
// BackupOptionsPanel.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Linq;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Options;
using Gio = CodeBrix.Develop.UI.Gio;
using GObject = CodeBrix.Develop.UI.GObject;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// The Backup options page: how many automatic startup backups of
/// options.sqlite to retain (0 disables the automatic backup), plus
/// exporting the settings to a portable file and importing such a file
/// (staged as options_incoming.sqlite and adopted on the next start).
/// </summary>
public class BackupOptionsPanel : OptionsPanel
{
    Gtk.DropDown? dropDown;

    /// <inheritdoc/>
    public override Gtk.Widget CreatePanelWidget()
    {
        var heading = Gtk.Label.New("Options auto-backup");
        heading.AddCssClass("heading");
        heading.SetXalign(0);

        var choices = Enumerable.Range(0, OptionsStore.MaxAutoBackupRetention + 1)
            .Select(count => count == 0 ? "0 — no automatic backups" : count.ToString())
            .ToArray();
        dropDown = Gtk.DropDown.NewFromStrings(choices);
        dropDown.SetHalign(Gtk.Align.Start);
        dropDown.SetSelected((uint) Math.Clamp(
            IdePreferences.AutoBackupRetention.Value, 0, OptionsStore.MaxAutoBackupRetention));

        var description = Gtk.Label.New(
            "Each time the application starts it saves a complete backup copy of the\n" +
            "options.sqlite settings file, then keeps only the most recent backups up\n" +
            "to the count selected here. Backups live beside the settings file:\n" +
            $"{PropertyService.Store.DirectoryPath}");
        description.SetXalign(0);
        description.AddCssClass("dim-label");

        var retainLabel = Gtk.Label.New("Automatic backups to retain:");
        retainLabel.SetXalign(0);

        var transferHeading = Gtk.Label.New("Import and export");
        transferHeading.AddCssClass("heading");
        transferHeading.SetXalign(0);
        transferHeading.SetMarginTop(16);

        var exportButton = Gtk.Button.NewWithLabel("Export Options…");
        exportButton.OnClicked += (_, _) => _ = ExportAsync();
        var importButton = Gtk.Button.NewWithLabel("Import Options…");
        importButton.OnClicked += (_, _) => _ = ImportAsync();

        var transferButtons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        transferButtons.SetHalign(Gtk.Align.Start);
        transferButtons.Append(exportButton);
        transferButtons.Append(importButton);

        var transferDescription = Gtk.Label.New(
            "Export saves a complete, self-contained copy of the current settings\n" +
            "that can be moved to another installation. Import validates a settings\n" +
            "file and stages it to be loaded the next time the application starts.");
        transferDescription.SetXalign(0);
        transferDescription.AddCssClass("dim-label");

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.Append(heading);
        box.Append(retainLabel);
        box.Append(dropDown);
        box.Append(description);
        box.Append(transferHeading);
        box.Append(transferButtons);
        box.Append(transferDescription);
        return box;
    }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges() =>
        dropDown != null && (int) dropDown.GetSelected() !=
            Math.Clamp(IdePreferences.AutoBackupRetention.Value, 0, OptionsStore.MaxAutoBackupRetention);

    /// <inheritdoc/>
    public override void ApplyChanges()
    {
        if (dropDown != null)
            IdePreferences.AutoBackupRetention.Value = (int) dropDown.GetSelected();
    }

    async Task ExportAsync()
    {
        if (ParentDialog == null)
            return;
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle("Export Options");
            dialog.SetInitialName(OptionsStore.OptionsFileName);
            dialog.SetFilters(CreateSqliteFilters());

            Gio.File? file;
            try
            {
                file = await dialog.SaveAsync(ParentDialog.Window);
            }
            catch (Exception)
            {
                return; // dialog dismissed
            }
            if (file?.GetPath() is not string path)
                return;
            if (!path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                path += ".sqlite";

            try
            {
                PropertyService.Store.ExportToFile(path);
            }
            catch (Exception ex)
            {
                ShowAlert("The Options could not be exported.", ex.Message);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Options export failed", ex);
        }
    }

    async Task ImportAsync()
    {
        if (ParentDialog == null)
            return;
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle("Import Options");
            dialog.SetFilters(CreateSqliteFilters());

            Gio.File? file;
            try
            {
                file = await dialog.OpenAsync(ParentDialog.Window);
            }
            catch (Exception)
            {
                return; // dialog dismissed
            }
            if (file?.GetPath() is not string path)
                return;

            try
            {
                PropertyService.Store.StageIncomingFile(path);
            }
            catch (Exception ex)
            {
                ShowAlert("The selected file could not be imported.", ex.Message);
                return;
            }

            await OfferRestartAsync();
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Options import failed", ex);
        }
    }

    async Task OfferRestartAsync()
    {
        var restartAlert = CreateAlert("Options imported",
            "The application must be restarted to load the imported options. Restart now?");
        restartAlert.SetButtons(new[] { "Restart Later", "Restart Now" });
        restartAlert.SetCancelButton(0);
        restartAlert.SetDefaultButton(1);

        int choice;
        try
        {
            choice = await restartAlert.ChooseAsync(ParentDialog!.Window);
        }
        catch (Exception)
        {
            return; // dismissed — the staged file loads on the next manual start
        }
        if (choice != 1)
            return;

        if (ParentDialog.HasUnsavedChanges)
        {
            var saveAlert = CreateAlert("Save your Options changes?",
                "You have unsaved Options changes. Do you want to save them before restarting?");
            saveAlert.SetButtons(new[] { "Cancel", "Don't Save", "Save" });
            saveAlert.SetCancelButton(0);
            saveAlert.SetDefaultButton(2);

            try
            {
                choice = await saveAlert.ChooseAsync(ParentDialog.Window);
            }
            catch (Exception)
            {
                return; // dismissed — treat as Cancel
            }
            if (choice == 0)
                return; // restart cancelled; the import stays staged
            if (choice == 2 && !ParentDialog.TryApplyAllChanges())
                return; // validation failed; the offending page is showing
        }

        IdeApp.Restart();
    }

    void ShowAlert(string message, string detail)
    {
        if (ParentDialog == null)
            return;
        var alert = CreateAlert(message, detail);
        alert.Show(ParentDialog.Window);
    }

    static Gtk.AlertDialog CreateAlert(string message, string detail)
    {
        var alert = Gtk.AlertDialog.NewWithProperties(Array.Empty<GObject.ConstructArgument>());
        alert.SetMessage(message);
        alert.SetDetail(detail);
        alert.SetModal(true);
        return alert;
    }

    static Gio.ListStore CreateSqliteFilters()
    {
        var sqliteFilter = Gtk.FileFilter.New();
        sqliteFilter.SetName("SQLite settings files (*.sqlite)");
        sqliteFilter.AddPattern("*.sqlite");
        var allFilter = Gtk.FileFilter.New();
        allFilter.SetName("All files");
        allFilter.AddPattern("*");
        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(sqliteFilter);
        filters.Append(allFilter);
        return filters;
    }
}
