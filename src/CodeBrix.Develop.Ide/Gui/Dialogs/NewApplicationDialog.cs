//
// NewApplicationDialog.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Templates;
using Gio = CodeBrix.Develop.UI.Gio;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Dialogs;

/// <summary>
/// The File &gt; New &gt; CodeBrix.Platform Application dialog: application
/// name, location (defaulting to the projects folder from Options), the six
/// platform head check boxes (all checked initially; at least one required),
/// and optional extra library assemblies. Create resolves the latest NuGet
/// package versions, generates the application, and reports the new .slnx.
/// </summary>
public class NewApplicationDialog
{
    readonly Gtk.Window window;
    readonly Gtk.Entry nameEntry;
    readonly Gtk.Entry locationEntry;
    readonly Gtk.Entry librariesEntry;
    readonly List<(PlatformHeadInfo Info, Gtk.CheckButton Check)> headChecks = new();
    readonly Gtk.DropDown fontDropDown;
    readonly Gtk.Label nameErrorLabel;
    readonly Gtk.Label errorLabel;
    readonly Gtk.Label progressLabel;
    readonly Gtk.Button createButton;
    readonly Gtk.Button cancelButton;
    readonly Gtk.Button browseButton;
    bool creating;

    /// <summary>Raised with the path of the generated .slnx after a successful Create.</summary>
    public event Action<FilePath>? Created;

    /// <summary>Creates the dialog over the given parent window.</summary>
    public NewApplicationDialog(Gtk.Window parent)
    {
        window = Gtk.Window.New();
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetTitle("New CodeBrix.Platform Application");
        window.SetDefaultSize(600, -1);

        var nameLabel = Gtk.Label.New("Application name:");
        nameLabel.SetXalign(0);
        nameEntry = Gtk.Entry.New();
        nameEntry.SetHexpand(true);
        WatchText(nameEntry);
        nameErrorLabel = Gtk.Label.New(null);
        nameErrorLabel.SetXalign(0);
        nameErrorLabel.SetWrap(true);
        nameErrorLabel.AddCssClass("cb-error");
        nameErrorLabel.SetVisible(false);

        var locationLabel = Gtk.Label.New("Location:");
        locationLabel.SetXalign(0);
        locationEntry = Gtk.Entry.New();
        locationEntry.SetHexpand(true);
        locationEntry.SetText(IdeApp.GetProjectsDirectory());
        WatchText(locationEntry);
        browseButton = Gtk.Button.NewWithLabel("Browse…");
        browseButton.OnClicked += (_, _) => _ = BrowseLocationAsync();
        var locationRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        locationRow.Append(locationEntry);
        locationRow.Append(browseButton);

        var headsLabel = Gtk.Label.New("Platform heads:");
        headsLabel.SetXalign(0);
        var headsBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        foreach (var info in PlatformHeadInfo.All)
        {
            var check = Gtk.CheckButton.NewWithLabel(info.DisplayName);
            check.SetActive(true);
            check.OnToggled += (_, _) => Validate();
            headChecks.Add((info, check));
            headsBox.Append(check);
        }

        var fontLabel = Gtk.Label.New("Application font:");
        fontLabel.SetXalign(0);
        fontDropDown = Gtk.DropDown.NewFromStrings(
            ApplicationFontInfo.All.Select(info => info.DisplayName).ToArray());
        fontDropDown.SetHalign(Gtk.Align.Start);
        fontDropDown.SetSelected(0); // Open Sans is the default

        var librariesLabel = Gtk.Label.New("Additional library assemblies (optional, comma-separated suffixes):");
        librariesLabel.SetXalign(0);
        librariesEntry = Gtk.Entry.New();
        librariesEntry.SetHexpand(true);
        WatchText(librariesEntry);
        var librariesHint = Gtk.Label.New("Example: Graphics, DatabaseAccess — generated under src/libs with matching test projects.");
        librariesHint.SetXalign(0);
        librariesHint.AddCssClass("dim-label");

        errorLabel = Gtk.Label.New(null);
        errorLabel.SetXalign(0);
        errorLabel.SetWrap(true);
        errorLabel.AddCssClass("dim-label");

        progressLabel = Gtk.Label.New(null);
        progressLabel.SetXalign(0);

        cancelButton = Gtk.Button.NewWithLabel("Cancel");
        cancelButton.OnClicked += (_, _) => window.Close();
        createButton = Gtk.Button.NewWithLabel("Create");
        createButton.AddCssClass("suggested-action");
        createButton.OnClicked += (_, _) => _ = CreateAsync();
        var buttonRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttonRow.SetHalign(Gtk.Align.End);
        buttonRow.Append(cancelButton);
        buttonRow.Append(createButton);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        content.SetMarginTop(14);
        content.SetMarginBottom(14);
        content.Append(nameLabel);
        content.Append(nameEntry);
        content.Append(nameErrorLabel);
        content.Append(locationLabel);
        content.Append(locationRow);
        content.Append(headsLabel);
        content.Append(headsBox);
        content.Append(fontLabel);
        content.Append(fontDropDown);
        content.Append(librariesLabel);
        content.Append(librariesEntry);
        content.Append(librariesHint);
        content.Append(errorLabel);
        content.Append(progressLabel);
        content.Append(buttonRow);
        window.SetChild(content);
        window.SetDefaultWidget(createButton);

        Validate();
    }

    /// <summary>Shows the dialog.</summary>
    public void Present() => window.Present();

    void WatchText(Gtk.Entry entry)
    {
        entry.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "text")
                Validate();
        };
    }

    IReadOnlyList<string> ParseLibrarySuffixes() =>
        librariesEntry.GetText()
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    IReadOnlyList<PlatformHead> CheckedHeads() =>
        headChecks.Where(entry => entry.Check.GetActive()).Select(entry => entry.Info.Head).ToList();

    // The single validation pass: an empty name just disables Create; a
    // problem with the typed name is flagged on the entry itself (red
    // border + hint text right below it); other problems are explained in
    // the label above the buttons. Create is enabled only when everything
    // is answerable.
    void Validate()
    {
        if (creating)
            return;

        var name = nameEntry.GetText().Trim();
        var nameProblem = GetNameProblem(name);
        if (nameProblem == null)
            nameEntry.RemoveCssClass("error");
        else
            nameEntry.AddCssClass("error");
        nameErrorLabel.SetText(nameProblem ?? "");
        nameErrorLabel.SetVisible(nameProblem != null);

        var otherProblem = GetOtherValidationError();
        errorLabel.SetText(otherProblem ?? "");

        createButton.SetSensitive(name.Length > 0 && nameProblem == null && otherProblem == null);
    }

    // Null when the name is fine — or still empty (no nagging before the
    // user has typed anything; Create stays disabled either way).
    string? GetNameProblem(string name)
    {
        if (name.Length == 0)
            return null;
        if (ApplicationTemplate.GetNameError(name) is string nameError)
            return nameError;
        var location = locationEntry.GetText().Trim();
        if (location.Length > 0
            && (Directory.Exists(Path.Combine(location, name)) || File.Exists(Path.Combine(location, name))))
            return $"This name is already in use: a folder named \"{name}\" exists in the location.";
        return null;
    }

    string? GetOtherValidationError()
    {
        var location = locationEntry.GetText().Trim();
        if (location.Length == 0)
            return "Enter a location folder.";
        if (!Directory.Exists(location))
            return "The location folder does not exist.";

        if (CheckedHeads().Count == 0)
            return "Check at least one platform head.";

        var suffixes = ParseLibrarySuffixes();
        foreach (var suffix in suffixes)
        {
            if (ApplicationTemplate.GetLibrarySuffixError(suffix) is string suffixError)
                return $"Library \"{suffix}\": {suffixError}";
        }
        if (suffixes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != suffixes.Count)
            return "Library suffixes must be unique.";

        return null;
    }

    async Task BrowseLocationAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle("Select Location");
            var current = locationEntry.GetText().Trim();
            dialog.SetInitialFolder(Gio.FileHelper.NewForPath(
                Directory.Exists(current) ? current : IdeApp.GetProjectsDirectory()));

            Gio.File? folder;
            try
            {
                folder = await dialog.SelectFolderAsync(window);
            }
            catch (Exception)
            {
                return; // dialog dismissed
            }
            if (folder?.GetPath() is string path)
                locationEntry.SetText(path);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Location selection failed", ex);
        }
    }

    async Task CreateAsync()
    {
        var name = nameEntry.GetText().Trim();
        if (creating || name.Length == 0 || GetNameProblem(name) != null || GetOtherValidationError() != null)
            return;
        creating = true;
        SetBusy(true);
        try
        {
            var options = new ApplicationTemplateOptions
            {
                Name = nameEntry.GetText().Trim(),
                Location = locationEntry.GetText().Trim(),
                Heads = CheckedHeads(),
                Font = ApplicationFontInfo.All[
                    Math.Clamp((int) fontDropDown.GetSelected(), 0, ApplicationFontInfo.All.Count - 1)].Font,
                LibrarySuffixes = ParseLibrarySuffixes(),
            };

            progressLabel.SetText("Resolving latest package versions…");
            var resolver = new PackageVersionResolver();
            options.PackageVersions = await resolver.ResolveLatestVersionsAsync(
                ApplicationTemplate.GetRequiredPackageIds(options));

            progressLabel.SetText("Generating project files…");
            var slnxPath = await Task.Run(() => ApplicationTemplate.Generate(options));

            window.Close();
            Created?.Invoke(slnxPath);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("New application generation failed", ex);
            progressLabel.SetText("");
            errorLabel.SetText($"The application could not be created: {ex.Message}");
            creating = false;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        nameEntry.SetSensitive(!busy);
        locationEntry.SetSensitive(!busy);
        browseButton.SetSensitive(!busy);
        fontDropDown.SetSensitive(!busy);
        librariesEntry.SetSensitive(!busy);
        foreach (var (_, check) in headChecks)
            check.SetSensitive(!busy);
        createButton.SetSensitive(!busy);
        cancelButton.SetSensitive(!busy);
    }
}
