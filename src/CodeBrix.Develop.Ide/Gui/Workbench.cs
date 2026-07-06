//
// Workbench.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Workbench / DefaultWorkbench,
//      rebuilt on GTK 4 for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Debugging;
using CodeBrix.Develop.Core.Projects;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Ide.Debugging;
using CodeBrix.Develop.Ide.Gui.Dialogs;
using CodeBrix.Develop.Ide.Gui.Documents;
using CodeBrix.Develop.Ide.Gui.Options;
using CodeBrix.Develop.Ide.Gui.Pads;
using CodeBrix.Develop.Ide.Themes;
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
    readonly CallStackPad callStackPad;
    readonly Gtk.Notebook bottomNotebook;
    readonly Gtk.Label statusLabel;
    EditorDocument? executionDocument;

    readonly Gtk.Paned verticalSplit;
    readonly Gtk.Paned horizontalSplit;
    readonly List<(Gtk.Button Button, string IconName)> toolbarButtons = new();

    readonly BuildService buildService = new BuildService();
    readonly BuildService runService = new BuildService();
    readonly SynchronizationContext uiContext;
    CancellationTokenSource? runCancellation;

    Gio.SimpleAction? buildAction, rebuildAction, cleanAction, runAction, stopAction, closeSolutionAction;
    Gio.SimpleAction? debugAction, stepOverAction, stepIntoAction, stepOutAction;

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
        window.SetDefaultSize(IdePreferences.WorkbenchWidth, IdePreferences.WorkbenchHeight);
        if (IdePreferences.WorkbenchMaximized)
            window.Maximize();
        ImageService.HiDpi = window.GetScaleFactor() > 1;

        solutionPad = new SolutionPad();
        solutionPad.FileActivated += OpenDocument;
        solutionPad.StartupProjectChanged += () =>
        {
            if (IdeApp.GetStartupProject(IdeApp.CurrentSolution) is { } startup)
                ShowStatus($"Startup project: {startup.Name}");
        };

        documentManager = new DocumentManager();

        buildOutput = new OutputPad();
        applicationOutput = new OutputPad();
        callStackPad = new CallStackPad();
        callStackPad.FrameActivated += (file, line) => NavigateTo(file, line);
        bottomNotebook = Gtk.Notebook.New();
        bottomNotebook.AppendPage(buildOutput.Widget, Gtk.Label.New("Build Output"));
        bottomNotebook.AppendPage(applicationOutput.Widget, Gtk.Label.New("Application Output"));
        bottomNotebook.AppendPage(callStackPad.Widget, Gtk.Label.New("Call Stack"));
        bottomNotebook.SetVexpand(false);

        buildService.OutputReceived += line => uiContext.Post(_ => buildOutput.AppendLine(line), null);
        runService.OutputReceived += line => uiContext.Post(_ => applicationOutput.AppendLine(line), null);

        // Debugger state changes arrive on background threads.
        DebugService.Paused += (reason, frames) => uiContext.Post(_ => OnDebugPaused(reason, frames), null);
        DebugService.Resumed += () => uiContext.Post(_ => OnDebugResumed(), null);
        DebugService.SessionEnded += exitCode => uiContext.Post(_ => OnDebugEnded(exitCode), null);
        DebugService.OutputReceived += line => uiContext.Post(_ => applicationOutput.AppendLine(line), null);

        verticalSplit = Gtk.Paned.New(Gtk.Orientation.Vertical);
        verticalSplit.SetStartChild(documentManager.Widget);
        verticalSplit.SetEndChild(bottomNotebook);
        verticalSplit.SetResizeStartChild(true);
        verticalSplit.SetShrinkStartChild(false);
        verticalSplit.SetPosition(IdePreferences.OutputPanePosition);

        horizontalSplit = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        horizontalSplit.SetStartChild(solutionPad.Widget);
        horizontalSplit.SetEndChild(verticalSplit);
        horizontalSplit.SetResizeStartChild(false);
        horizontalSplit.SetShrinkStartChild(false);
        horizontalSplit.SetPosition(IdePreferences.SolutionPanePosition);
        horizontalSplit.SetHexpand(true);
        horizontalSplit.SetVexpand(true);

        statusLabel = Gtk.Label.New("Ready");
        statusLabel.SetXalign(0);
        statusLabel.AddCssClass("cb-statusbar");
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

        // GTK reserves F10 for menubar activation; a capture-phase shortcut
        // on the window wins, keeping the VS convention (F10 = Step Over).
        var stepOverShortcut = Gtk.ShortcutController.New();
        stepOverShortcut.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        stepOverShortcut.AddShortcut(Gtk.Shortcut.New(
            Gtk.ShortcutTrigger.ParseString("F10"),
            Gtk.NamedAction.New("app.step-over")));
        window.AddController(stepOverShortcut);

        // Everything remembered about the UI goes to options.sqlite on the
        // way out (the portable-configuration principle).
        window.OnCloseRequest += (_, _) =>
        {
            DebugService.Shutdown(clearBreakpoints: false);
            documentManager.SaveAll();
            SaveUiState();
            return false;
        };

        ThemeService.ThemeChanged += OnThemeChanged;
    }

    void OnThemeChanged()
    {
        // Icon variants (~dark) follow the theme: reload the toolbar and
        // Solution-pad icons, and restyle the open editors.
        foreach (var (button, iconName) in toolbarButtons)
            button.SetChild(ImageService.CreateImage(iconName));
        solutionPad.RefreshIcons();
        documentManager.RefreshStyleSchemes();
    }

    void SaveUiState()
    {
        IdePreferences.WorkbenchMaximized.Value = window.IsMaximized();
        if (!window.IsMaximized())
        {
            window.GetDefaultSize(out var width, out var height);
            if (width > 0 && height > 0)
            {
                IdePreferences.WorkbenchWidth.Value = width;
                IdePreferences.WorkbenchHeight.Value = height;
            }
        }
        IdePreferences.SolutionPanePosition.Value = horizontalSplit.Position;
        IdePreferences.OutputPanePosition.Value = verticalSplit.Position;
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
        toolbar.Append(ToolButton("bug-16", "app.debug", "Start Debugging / Continue (F5)"));
        toolbar.Append(ToolButton("execute-16", "app.run", "Start Without Debugging (Ctrl+F5)"));
        toolbar.Append(ToolButton("stop-16", "app.stop", "Stop (Shift+F5)"));
        toolbar.Append(ToolbarSeparator());
        toolbar.Append(ToolButton("step-over-16", "app.step-over", "Step Over (F10)"));
        toolbar.Append(ToolButton("step-in-16", "app.step-into", "Step Into (F11)"));
        toolbar.Append(ToolButton("step-out-16", "app.step-out", "Step Out (Shift+F11)"));
        return toolbar;
    }

    Gtk.Button ToolButton(string iconName, string actionName, string tooltip)
    {
        var button = Gtk.Button.New();
        button.SetChild(ImageService.CreateImage(iconName));
        // Actionable wiring: the button follows the action's enabled state.
        button.SetActionName(actionName);
        button.SetTooltipText(tooltip);
        button.SetHasFrame(false);
        toolbarButtons.Add((button, iconName));
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
        AddAction("new-application", ShowNewApplicationDialog, "<Control><Shift>n");
        AddAction("open-solution", () => _ = OpenSolutionDialogAsync(), "<Control>o");
        closeSolutionAction = AddAction("close-solution", CloseSolution, null, enabled: false);
        AddAction("save", () => { documentManager.SaveActive(); ShowStatus("Saved"); }, "<Control>s");
        AddAction("save-all", () => { documentManager.SaveAll(); ShowStatus("All files saved"); }, "<Control><Shift>s");
        AddAction("close-file", () =>
        {
            if (documentManager.ActiveDocument is { } document)
                documentManager.CloseDocument(document);
        }, "<Control>w");
        AddAction("options", ShowOptions, "<Control>comma");
        AddAction("quit", () =>
        {
            DebugService.Shutdown(clearBreakpoints: false);
            documentManager.SaveAll();
            SaveUiState();
            application.Quit();
        }, "<Control>q");

        buildAction = AddAction("build", () => _ = BuildAsync(rebuild: false), "<Control><Shift>b", enabled: false);
        rebuildAction = AddAction("rebuild", () => _ = BuildAsync(rebuild: true), null, enabled: false);
        cleanAction = AddAction("clean", () => _ = CleanAsync(), null, enabled: false);
        // VS/MonoDevelop convention: F5 debugs (or continues), Ctrl+F5 runs
        // without debugging.
        debugAction = AddAction("debug", () => _ = DebugAsync(), "F5", enabled: false);
        runAction = AddAction("run", () => _ = RunAsync(), "<Control>F5", enabled: false);
        stepOverAction = AddAction("step-over", () => _ = DebugService.StepOverAsync(), "F10", enabled: false);
        stepIntoAction = AddAction("step-into", () => _ = DebugService.StepIntoAsync(), "F11", enabled: false);
        stepOutAction = AddAction("step-out", () => _ = DebugService.StepOutAsync(), "<Shift>F11", enabled: false);
        stopAction = AddAction("stop", StopRunOrDebug, "<Shift>F5", enabled: false);
        AddAction("toggle-breakpoint", () => documentManager.ActiveDocument?.ToggleBreakpointAtCaret(), "F9");

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
        var newMenu = Gio.Menu.New();
        newMenu.Append("CodeBrix.Platform _Application…", "app.new-application");

        var fileMenu = Gio.Menu.New();
        fileMenu.AppendSubmenu("_New", newMenu);
        fileMenu.Append("_Open Solution…", "app.open-solution");
        fileMenu.Append("Close Solutio_n", "app.close-solution");
        fileMenu.Append("_Save", "app.save");
        fileMenu.Append("Save _All", "app.save-all");
        fileMenu.Append("_Close File", "app.close-file");
        fileMenu.Append("Op_tions…", "app.options");
        fileMenu.Append("_Quit", "app.quit");

        var editMenu = Gio.Menu.New();
        editMenu.Append("Complete _Word", "app.complete");

        var buildMenu = Gio.Menu.New();
        buildMenu.Append("_Build Solution", "app.build");
        buildMenu.Append("_Rebuild Solution", "app.rebuild");
        buildMenu.Append("C_lean Solution", "app.clean");

        var runMenu = Gio.Menu.New();
        runMenu.Append("Start _Debugging / Continue", "app.debug");
        runMenu.Append("_Start Without Debugging", "app.run");
        runMenu.Append("Step _Over", "app.step-over");
        runMenu.Append("Step _Into", "app.step-into");
        runMenu.Append("Step O_ut", "app.step-out");
        runMenu.Append("Toggle _Breakpoint", "app.toggle-breakpoint");
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
        // Start where the user keeps their projects (General options page).
        dialog.SetInitialFolder(Gio.FileHelper.NewForPath(IdeApp.GetProjectsDirectory()));

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
        foreach (var action in new[] { buildAction, rebuildAction, cleanAction, runAction, debugAction, closeSolutionAction })
            action?.SetEnabled(true);
        // The reopened-on-next-start solution; a startup-project choice left
        // over from a different solution is silently blanked by the
        // GetStartupProject validation the Solution pad just ran.
        IdePreferences.LastSolution.Value = (string) fileName.FullPath;
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

    // Resolves the project the Run/Debug commands start, applying the
    // first-run rule: with no explicit choice, the .LinuxX11 head becomes
    // (and is persisted as) the startup project.
    DotNetProject? ResolveStartupProjectForLaunch(Solution solution)
    {
        if (IdeApp.GetStartupProject(solution) is not { } project)
        {
            ShowStatus("The solution has no executable project to run");
            return null;
        }
        if (string.IsNullOrEmpty(IdePreferences.StartupProject.Value) && IdeApp.IsLinuxX11Head(project))
        {
            IdePreferences.StartupProject.Value = (string) project.FileName;
            solutionPad.RefreshStartupProject();
        }
        return project;
    }

    void StopRunOrDebug()
    {
        if (DebugService.IsSessionActive)
            _ = DebugService.StopAsync();
        else
            runCancellation?.Cancel();
    }

    async Task DebugAsync()
    {
        // While paused, F5 continues (the VS convention).
        if (DebugService.IsPaused)
        {
            await DebugService.ContinueAsync();
            return;
        }
        if (DebugService.IsSessionActive || buildService.IsBusy)
            return;
        if (IdeApp.CurrentSolution is not { } solution)
            return;
        if (ResolveStartupProjectForLaunch(solution) is not { } project)
            return;

        documentManager.SaveAll();
        buildOutput.Clear();
        bottomNotebook.SetCurrentPage(0);
        ShowStatus($"Building {project.Name}…");
        var result = await buildService.BuildAsync(project.FileName);
        if (!result.Success)
        {
            ShowStatus("Build failed — debugging not started");
            return;
        }

        applicationOutput.Clear();
        bottomNotebook.SetCurrentPage(1);
        ShowStatus($"Debugging {project.Name}…");
        try
        {
            await DebugService.StartAsync(project);
            stopAction?.SetEnabled(true);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Starting the debugger failed", ex);
            ShowStatus($"Debugging could not start: {ex.Message}");
        }
    }

    void OnDebugPaused(string reason, IReadOnlyList<StackFrameInfo> frames)
    {
        foreach (var action in new[] { stepOverAction, stepIntoAction, stepOutAction })
            action?.SetEnabled(true);
        callStackPad.ShowFrames(frames);
        bottomNotebook.SetCurrentPage(2);

        var topFrame = frames.FirstOrDefault(frame => frame.File.Length > 0);
        if (topFrame != null)
        {
            NavigateTo(topFrame.File, topFrame.Line, markExecution: true);
            ShowStatus($"Paused — {reason} at {Path.GetFileName(topFrame.File)}:{topFrame.Line}");
        }
        else
        {
            ShowStatus($"Paused — {reason}");
        }
    }

    void OnDebugResumed()
    {
        foreach (var action in new[] { stepOverAction, stepIntoAction, stepOutAction })
            action?.SetEnabled(false);
        executionDocument?.ClearExecutionLine();
        executionDocument = null;
        callStackPad.Clear();
        ShowStatus("Running…");
    }

    void OnDebugEnded(int? exitCode)
    {
        OnDebugResumed();
        stopAction?.SetEnabled(false);
        ShowStatus(exitCode is { } code ? $"Debugging ended — exit code {code}" : "Debugging ended");
    }

    void NavigateTo(FilePath file, int line, bool markExecution = false)
    {
        if (!File.Exists(file))
            return;
        try
        {
            var document = documentManager.OpenDocument(file);
            if (markExecution)
            {
                executionDocument?.ClearExecutionLine();
                executionDocument = document;
                document.ShowExecutionLine(line);
            }
            else
            {
                document.ScrollToLine(line);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Could not navigate to {file}:{line}", ex);
        }
    }

    async Task RunAsync()
    {
        if (IdeApp.CurrentSolution is not { } solution || runService.IsBusy || DebugService.IsSessionActive)
            return;
        if (ResolveStartupProjectForLaunch(solution) is not { } project)
            return;

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

    /// <summary>
    /// Restores the solution the user last worked on, or — on a first run,
    /// or when they closed out of their solution before exiting — shows the
    /// New CodeBrix.Platform Application experience. Called at startup when
    /// no solution was given on the command line.
    /// </summary>
    public async Task RestoreStartupSolutionAsync()
    {
        var lastSolution = IdePreferences.LastSolution.Value;
        if (!string.IsNullOrEmpty(lastSolution) && IdeApp.IsOwnProjectFile(lastSolution))
        {
            // Left over from an accidental "dotnet run CodeBrix.Develop.csproj"
            // self-open; never reopen the IDE's own project from memory.
            IdePreferences.LastSolution.Value = lastSolution = "";
        }
        if (!string.IsNullOrEmpty(lastSolution))
        {
            if (File.Exists(lastSolution))
            {
                // A failed load keeps the remembered path: a fixed file
                // reopens on the next start, and the user stays in the
                // empty workbench rather than being treated as a first run.
                await LoadSolutionAsync(lastSolution);
                return;
            }
            IdePreferences.LastSolution.Value = ""; // stale path: silently forget
        }
        ShowNewApplicationDialog();
    }

    /// <summary>
    /// Closes the loaded solution: all open documents are saved and closed,
    /// the type system unloads, and the remembered last-solution and
    /// startup-project choices are blanked. Cancel-free by design — the
    /// blank workbench remains.
    /// </summary>
    void CloseSolution()
    {
        if (IdeApp.CurrentSolution == null)
            return;
        // Any live debug session dies with the solution, and per policy the
        // breakpoints are lost too.
        DebugService.Shutdown(clearBreakpoints: true);
        documentManager.SaveAll();
        foreach (var document in documentManager.Documents.ToList())
            documentManager.CloseDocument(document);
        TypeSystemService.UnloadSolution();
        IdeApp.CurrentSolution = null;
        solutionPad.Clear();
        foreach (var action in new[] { buildAction, rebuildAction, cleanAction, runAction, debugAction,
                     stepOverAction, stepIntoAction, stepOutAction, closeSolutionAction })
            action?.SetEnabled(false);
        window.Title = "CodeBrix Develop";
        IdePreferences.LastSolution.Value = "";
        IdePreferences.StartupProject.Value = "";
        ShowStatus("Ready");
    }

    void ShowNewApplicationDialog()
    {
        var dialog = new NewApplicationDialog(window);
        dialog.Created += slnxPath => _ = LoadSolutionAsync(slnxPath);
        dialog.Present();
    }

    void ShowOptions()
    {
        var dialog = new OptionsDialog(window, IdeOptionsSections.Build());
        dialog.Present();
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

/// <summary>Small helpers for matching the application theme.</summary>
public static class WorkbenchTheme
{
    /// <summary>
    /// Whether the application renders dark: the applied color theme's
    /// darkness, falling back to desktop detection before a theme applies.
    /// </summary>
    public static bool PrefersDark =>
        ThemeService.CurrentTheme?.IsDark ?? ThemeService.SystemPrefersDark();
}
