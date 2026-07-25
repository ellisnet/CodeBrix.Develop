//
// AppearanceOptionsPanel.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop's IDEStyleOptionsPanel and VS Code's
//      workbench color-theme picker)
// SPDX-License-Identifier: MIT
//

using System;
using System.Linq;
using CodeBrix.Develop.Ide.Themes;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// The Appearance options page: the application color theme, chosen from
/// the embedded VS Code themes, and how a solution's name is spelled out in
/// the window title. Theme selection applies live (VS Code-style preview);
/// OK persists it to options.sqlite, Cancel restores the original theme.
/// </summary>
public class AppearanceOptionsPanel : OptionsPanel
{
    string? originalThemeId;
    string? selectedThemeId;
    Gtk.CheckButton? spacedTitleCheck;

    /// <inheritdoc/>
    public override Gtk.Widget CreatePanelWidget()
    {
        originalThemeId = ThemeService.CurrentTheme?.Id;
        selectedThemeId = originalThemeId;

        var heading = Gtk.Label.New("Color theme");
        heading.AddCssClass("heading");
        heading.SetXalign(0);

        var themeNames = ThemeService.Themes.Select(theme => theme.Name).ToArray();
        var dropDown = Gtk.DropDown.NewFromStrings(themeNames);
        dropDown.SetHalign(Gtk.Align.Start);
        var currentIndex = ThemeService.Themes.ToList().FindIndex(theme => theme.Id == originalThemeId);
        if (currentIndex >= 0)
            dropDown.SetSelected((uint) currentIndex);
        dropDown.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() != "selected")
                return;
            var index = (int) dropDown.GetSelected();
            if (index < 0 || index >= ThemeService.Themes.Count)
                return;
            var theme = ThemeService.Themes[index];
            if (theme.Id == selectedThemeId)
                return;
            selectedThemeId = theme.Id;
            ThemeService.Apply(theme.Id); // live preview
        };

        var description = Gtk.Label.New(
            "The color theme restyles the whole application, including editor syntax\n" +
            "highlighting. Changes preview immediately; Cancel restores the previous\n" +
            "theme. Themes come from the Visual Studio Code project.");
        description.SetXalign(0);
        description.AddCssClass("dim-label");

        var titleHeading = Gtk.Label.New("Window title");
        titleHeading.AddCssClass("heading");
        titleHeading.SetXalign(0);
        titleHeading.SetMarginTop(16);

        spacedTitleCheck = Gtk.CheckButton.NewWithLabel("Add spaces to solution names in the title bar");
        spacedTitleCheck.SetActive(IdePreferences.SpacedSolutionTitle.Value);

        var titleDescription = Gtk.Label.New(
            "The solution name is spelled out with spaces: \"Doom.Brix\" appears as\n" +
            "\"Doom Brix\", \"WikipediaPublisher\" as \"Wikipedia Publisher\". The IDE window's\n" +
            "title then no longer contains the solution's name verbatim, so searching\n" +
            "for the window of the application being built cannot find this one instead.");
        titleDescription.SetXalign(0);
        titleDescription.AddCssClass("dim-label");

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.Append(heading);
        box.Append(dropDown);
        box.Append(description);
        box.Append(titleHeading);
        box.Append(spacedTitleCheck);
        box.Append(titleDescription);
        return box;
    }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges() =>
        selectedThemeId != originalThemeId || SelectedSpacedTitle != IdePreferences.SpacedSolutionTitle.Value;

    /// <inheritdoc/>
    public override void ApplyChanges()
    {
        if (selectedThemeId != null)
            IdePreferences.ColorTheme.Value = selectedThemeId;
        IdePreferences.SpacedSolutionTitle.Value = SelectedSpacedTitle;
    }

    bool SelectedSpacedTitle => spacedTitleCheck?.GetActive() ?? IdePreferences.SpacedSolutionTitle.Value;

    /// <inheritdoc/>
    public override void CancelChanges()
    {
        if (originalThemeId != null && selectedThemeId != originalThemeId)
            ThemeService.Apply(originalThemeId);
    }
}
