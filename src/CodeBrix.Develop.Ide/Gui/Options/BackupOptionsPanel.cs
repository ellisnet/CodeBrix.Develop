//
// BackupOptionsPanel.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Linq;
using CodeBrix.Develop.Core.Options;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// The Backup options page: how many automatic startup backups of
/// options.sqlite to retain (0 disables the automatic backup).
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

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.Append(heading);
        box.Append(retainLabel);
        box.Append(dropDown);
        box.Append(description);
        return box;
    }

    /// <inheritdoc/>
    public override void ApplyChanges()
    {
        if (dropDown != null)
            IdePreferences.AutoBackupRetention.Value = (int) dropDown.GetSelected();
    }
}
