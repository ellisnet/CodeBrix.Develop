//
// Workbench.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Workbench / DefaultWorkbench,
//      rebuilt on GTK 4 for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Projects;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Ide.Gui.Documents;
using CodeBrix.Develop.Ide.Gui.Pads;
using Gio = CodeBrix.Develop.UI.Gio;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui;

/// <summary>
/// The main IDE window: menu bar, Solution pad, document area, output pads,
/// and status bar, plus the commands that wire them to the core services.
/// </summary>
public class Workbench
{
    readonly Gtk.Application application;
    readonly Gtk.ApplicationWindow window;
    readonly SolutionPad solutionPad;
    readonly DocumentManager documentManager;
    readonly OutputPad buildOutput;
    readonly OutputPad applicationOutput;
    readonly Gtk.Notebook bottomNotebook;
    readonly Gtk.Label statusLabel;

    readonly BuildService buildService = new BuildService();
    readonly BuildService runService = new BuildService();
    readonly SynchronizationContext uiContext;
    CancellationTokenSource? runCancellation;

    Gio.SimpleAction? buildAction, rebuildAction, cleanAction, runAction, stopAction;

    /// <summary>Creates the workbench window for the given application.</summary>
    public Workbench(Gtk.Application app)
    {
        application = app;
        // Captured under Application.RunWithSynchronizationContext, so Post()
        // dispatches onto the GTK main loop.
        uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The workbench must be created on the GTK main loop with a GLib synchronization context");

        window = Gtk.ApplicationWindow.New(app);
        window.Title = "CodeBrix Develop";
        window.SetDefaultSize(1360, 880);
        ImageService.HiDpi = window.GetScaleFactor() > 1;

        solutionPad = new SolutionPad();
        solutionPad.FileActivated += OpenDocument;

        documentManager = new DocumentManager();

        buildOutput = new OutputPad();
        applicationOutput = new OutputPad();
        bottomNotebook = Gtk.Notebook.New();
        bottomNotebook.AppendPage(buildOutput.Widget, Gtk.Label.New("Build Output"));
        bottomNotebook.AppendPage(applicationOutput.Widget, Gtk.Label.New("Application Output"));
        bottomNotebook.SetVexpand(false);

        buildService.OutputReceived += line => uiContext.Post(_ => buildOutput.AppendLine(line), null);
        runService.OutputReceived += line => uiContext.Post(_ => applicationOutput.AppendLine(line), null);

        var verticalSplit = Gtk.Paned.New(Gtk.Orientation.Vertical);
        verticalSplit.SetStartChild(documentManager.Widget);
        verticalSplit.SetEndChild(bottomNotebook);
        verticalSplit.SetResizeStartChild(true);
        verticalSplit.SetShrinkStartChild(false);
        verticalSplit.SetPosition(600);

        var horizontalSplit = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        horizontalSplit.SetStartChild(solutionPad.Widget);
        horizontalSplit.SetEndChild(verticalSplit);
        horizontalSplit.SetResizeStartChild(false);
        horizontalSplit.SetShrinkStartChild(false);
        horizontalSplit.SetPosition(300);
        horizontalSplit.SetHexpand(true);
        horizontalSplit.SetVexpand(true);

        statusLabel = Gtk.Label.New("Ready");
        statusLabel.SetXalign(0);
        statusLabel.SetMarginStart(8);
        statusLabel.SetMarginTop(3);
        statusLabel.SetMarginBottom(3);

        var rootBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        rootBox.Append(BuildToolbar());
        rootBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        rootBox.Append(horizontalSplit);
        rootBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        rootBox.Append(statusLabel);
        window.SetChild(rootBox);

        InstallActions();
        application.SetMenubar(BuildMenubarModel());
        window.SetShowMenubar(true);
    }

    Gtk.Widget BuildToolbar()
    {
        var toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
        toolbar.AddCssClass("toolbar");

        toolbar.Append(ToolButton("open-16", "app.open-solution", "Open Solution (Ctrl+O)"));
        toolbar.Append(ToolButton("save-16", "app.save", "Save (Ctrl+S)"));
        toolbar.Append(ToolButton("save-all-16", "app.save-all", "Save All (Ctrl+Shift+S)"));
        toolbar.Append(ToolbarSeparator());
        toolbar.Append(ToolButton("build-target-16", "app.build", "Build Solution (Ctrl+Shift+B)"));
        toolbar.Append(ToolbarSeparator());
        toolbar.Append(ToolButton("execute-16", "app.run", "Start Without Debugging (F5)"));
        toolbar.Append(ToolButton("stop-16", "app.stop", "Stop (Shift+F5)"));
        return toolbar;
    }

    static Gtk.Button ToolButton(string iconName, string actionName, string tooltip)
    {
        var button = Gtk.Button.New();
        button.SetChild(ImageService.CreateImage(iconName));
        // Actionable wiring: the button follows the action's enabled state.
        button.SetActionName(actionName);
        button.SetTooltipText(tooltip);
        button.SetHasFrame(false);
        return button;
    }

    static Gtk.Widget ToolbarSeparator()
    {
        var separator = Gtk.Separator.New(Gtk.Orientation.Vertical);
        separator.SetMarginStart(4);
        separator.SetMarginEnd(4);
        separator.SetMarginTop(4);
        separator.SetMarginBottom(4);
        return separator;
    }

    /// <summary>Shows the window.</summary>
    public void Present() => window.Present();

    /// <summary>Sets the status-bar text. Must be called on the UI thread.</summary>
    public void ShowStatus(string message) => statusLabel.SetText(message);

    void InstallActions()
    {
        AddAction("open-solution", () => _ = OpenSolutionDialogAsync(), "<Control>o");
        AddAction("save", () => { documentManager.SaveActive(); ShowStatus("Saved"); }, "<Control>s");
        AddAction("save-all", () => { documentManager.SaveAll(); ShowStatus("All files saved"); }, "<Control><Shift>s");
        AddAction("close-file", () =>
        {
            if (documentManager.ActiveDocument is { } document)
                documentManager.CloseDocument(document);
        }, "<Control>w");
        AddAction("quit", () => { documentManager.SaveAll(); application.Quit(); }, "<Control>q");

        buildAction = AddAction("build", () => _ = BuildAsync(rebuild: false), "<Control><Shift>b", enabled: false);
        rebuildAction = AddAction("rebuild", () => _ = BuildAsync(rebuild: true), null, enabled: false);
        cleanAction = AddAction("clean", () => _ = CleanAsync(), null, enabled: false);
        runAction = AddAction("run", () => _ = RunAsync(), "F5", enabled: false);
        stopAction = AddAction("stop", () => runCancellation?.Cancel(), "<Shift>F5", enabled: false);

        AddAction("complete", () => _ = documentManager.ActiveDocument?.ShowCompletionAsync(), "<Control>space");
        AddAction("about", ShowAbout);
    }

    Gio.SimpleAction AddAction(string name, Action handler, string? accel = null, bool enabled = true)
    {
        var action = Gio.SimpleAction.New(name, null);
        action.SetEnabled(enabled);
        action.OnActivate += (_, _) =>
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Command '{name}' failed", ex);
                ShowStatus($"Command failed: {ex.Message}");
            }
        };
        application.AddAction(action);
        if (accel != null)
            application.SetAccelsForAction($"app.{name}", new[] { accel });
        return action;
    }

    Gio.MenuModel BuildMenubarModel()
    {
        var fileMenu = Gio.Menu.New();
        fileMenu.Append("_Open Solution…", "app.open-solution");
        fileMenu.Append("_Save", "app.save");
        fileMenu.Append("Save _All", "app.save-all");
        fileMenu.Append("_Close File", "app.close-file");
        fileMenu.Append("_Quit", "app.quit");

        var editMenu = Gio.Menu.New();
        editMenu.Append("Complete _Word", "app.complete");

        var buildMenu = Gio.Menu.New();
        buildMenu.Append("_Build Solution", "app.build");
        buildMenu.Append("_Rebuild Solution", "app.rebuild");
        buildMenu.Append("C_lean Solution", "app.clean");

        var runMenu = Gio.Menu.New();
        runMenu.Append("_Start Without Debugging", "app.run");
        runMenu.Append("S_top", "app.stop");

        var helpMenu = Gio.Menu.New();
        helpMenu.Append("_About CodeBrix Develop", "app.about");

        var menubar = Gio.Menu.New();
        menubar.AppendSubmenu("_File", fileMenu);
        menubar.AppendSubmenu("_Edit", editMenu);
        menubar.AppendSubmenu("_Build", buildMenu);
        menubar.AppendSubmenu("_Run", runMenu);
        menubar.AppendSubmenu("_Help", helpMenu);
        return menubar;
    }

    void OpenDocument(FilePath fileName)
    {
        try
        {
            documentManager.OpenDocument(fileName);
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Could not open {fileName}", ex);
            ShowStatus($"Could not open {fileName.FileName}: {ex.Message}");
        }
    }

    async Task OpenSolutionDialogAsync()
    {
        var filter = Gtk.FileFilter.New();
        filter.SetName("Solutions and projects (*.sln, *.slnx, *.csproj)");
        filter.AddPattern("*.sln");
        filter.AddPattern("*.slnx");
        filter.AddPattern("*.csproj");
        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(filter);

        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle("Open Solution");
        dialog.SetFilters(filters);

        Gio.File? file;
        try
        {
            file = await dialog.OpenAsync(window);
        }
        catch (Exception)
        {
            return; // dialog dismissed
        }

        if (file?.GetPath() is string path)
            await LoadSolutionAsync(path);
    }

    /// <summary>Loads a solution, fills the Solution pad, and warms up the type system.</summary>
    public async Task LoadSolutionAsync(FilePath fileName)
    {
        ShowStatus($"Loading {fileName.FileName}…");
        Solution solution;
        try
        {
            solution = await Task.Run(() => Solution.Load(fileName));
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Could not load solution {fileName}", ex);
            ShowStatus($"Could not load {fileName.FileName}: {ex.Message}");
            return;
        }

        IdeApp.CurrentSolution = solution;
        solutionPad.LoadSolution(solution);
        window.Title = $"{solution.Name} – CodeBrix Develop";
        foreach (var action in new[] { buildAction, rebuildAction, cleanAction, runAction })
            action?.SetEnabled(true);
        ShowStatus($"Solution '{solution.Name}' loaded ({solution.Projects.Count} project{(solution.Projects.Count == 1 ? "" : "s")}) — loading type system…");

        try
        {
            await TypeSystemService.LoadSolutionAsync(solution, new Progress<string>(ShowStatus));
            ShowStatus($"Ready — {solution.Name}");
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Type system load failed", ex);
            ShowStatus("Type system load failed — completion unavailable (see log)");
        }
    }

    async Task BuildAsync(bool rebuild)
    {
        if (IdeApp.CurrentSolution is not { } solution || buildService.IsBusy)
            return;
        documentManager.SaveAll();
        buildOutput.Clear();
        bottomNotebook.SetCurrentPage(0);
        ShowStatus(rebuild ? "Rebuilding…" : "Building…");

        var result = rebuild
            ? await buildService.RebuildAsync(solution.FileName)
            : await buildService.BuildAsync(solution.FileName);

        ShowStatus($"{(result.Success ? "Build succeeded" : "Build failed")} — " +
            $"{result.ErrorCount} error{(result.ErrorCount == 1 ? "" : "s")}, " +
            $"{result.WarningCount} warning{(result.WarningCount == 1 ? "" : "s")} " +
            $"({result.Elapsed.TotalSeconds:F1}s)");
    }

    async Task CleanAsync()
    {
        if (IdeApp.CurrentSolution is not { } solution || buildService.IsBusy)
            return;
        buildOutput.Clear();
        bottomNotebook.SetCurrentPage(0);
        ShowStatus("Cleaning…");
        var result = await buildService.CleanAsync(solution.FileName);
        ShowStatus(result.Success ? "Clean succeeded" : "Clean failed");
    }

    async Task RunAsync()
    {
        if (IdeApp.CurrentSolution is not { } solution || runService.IsBusy)
            return;
        if (solution.StartupProject is not { } project)
        {
            ShowStatus("The solution has no executable project to run");
            return;
        }

        documentManager.SaveAll();
        applicationOutput.Clear();
        bottomNotebook.SetCurrentPage(1);
        ShowStatus($"Running {project.Name}…");
        stopAction?.SetEnabled(true);
        runCancellation = new CancellationTokenSource();
        try
        {
            var exitCode = await runService.RunAsync(project, runCancellation.Token);
            ShowStatus($"{project.Name} exited with code {exitCode}");
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Run failed", ex);
            ShowStatus($"Run failed: {ex.Message}");
        }
        finally
        {
            stopAction?.SetEnabled(false);
            runCancellation.Dispose();
            runCancellation = null;
        }
    }

    void ShowAbout()
    {
        var about = Gtk.AboutDialog.New();
        about.SetTransientFor(window);
        about.SetModal(true);
        about.SetProgramName("CodeBrix Develop");
        about.SetComments("An IDE for developing CodeBrix.Platform applications.\nInspired by, and architected like, MonoDevelop.");
        about.SetCopyright("Copyright (c) 2026 Jeremy Ellis and contributors");
        about.SetLicenseType(Gtk.License.MitX11);
        about.SetWebsite("https://github.com/ellisnet");
        about.Present();
    }
}

/// <summary>Small helpers for matching the ambient GTK theme.</summary>
public static class WorkbenchTheme
{
    /// <summary>Best-effort detection of a dark ambient theme.</summary>
    public static bool PrefersDark
    {
        get
        {
            var themeName = Gtk.Settings.GetDefault()?.GtkThemeName;
            return themeName?.Contains("dark", StringComparison.OrdinalIgnoreCase) == true;
        }
    }
}
