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
using CodeBrix.Develop.Core.Testing;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Emulation.FrameBuffer;
using CodeBrix.Develop.Emulation.FrameBuffer.Transport;
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
    readonly TestsPad testsPad;
    readonly TestResultsPad testResultsPad;
    readonly Gtk.Notebook leftNotebook;
    readonly DocumentManager documentManager;
    readonly OutputPad buildOutput;
    readonly OutputPad applicationOutput;
    readonly OutputPad nugetOutput;
    readonly OutputPad ideLog;
    readonly CallStackPad callStackPad;
    readonly Gtk.Notebook bottomNotebook;
    readonly Gtk.Label statusLabel;
    EditorDocument? executionDocument;

    readonly Gtk.Paned verticalSplit;
    readonly Gtk.Paned horizontalSplit;
    readonly List<(Gtk.Button Button, string IconName)> toolbarButtons = new();

    readonly BuildService buildService = new BuildService();
    readonly BuildService runService = new BuildService();
    readonly BuildService nugetService = new BuildService();
    readonly SynchronizationContext uiContext;
    CancellationTokenSource? runCancellation;
    CancellationTokenSource? testRunCancellation;
    bool testStatusRefreshQueued;

    // Created on the first Run/Debug of a frame-buffer head and then left
    // open — never hidden — so it keeps the place the user put it until they
    // close it or the application exits.
    FrameBufferEmulatorWindow? frameBufferEmulator;
    bool frameBufferEmulationRunning;

    // The transport of the emulated app currently running (or being debugged);
    // null between launches. The window's Touch handler reads this field, so
    // touch forwarding follows whichever session is live.
    FrameBufferEmulatorSession? frameBufferSession;

    Gio.SimpleAction? buildAction, rebuildAction, cleanAction, runAction, stopAction, closeSolutionAction;
    Gio.SimpleAction? debugAction, stepOverAction, stepIntoAction, stepOutAction;
    Gio.SimpleAction? updateCodeBrixPackagesAction, closeEmulatorAction;
    Gio.SimpleAction? runAllTestsAction, runSelectedTestsAction, debugSelectedTestAction;
    Gio.SimpleAction? runTestAtCaretAction, debugTestAtCaretAction, rediscoverTestsAction;

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

        testsPad = new TestsPad();
        testsPad.NavigateRequested += (file, line) => NavigateTo(file, line);
        testsPad.RunRequested += RunTests;
        testsPad.DebugRequested += DebugTest;

        documentManager = new DocumentManager();

        buildOutput = new OutputPad();
        applicationOutput = new OutputPad();
        nugetOutput = new OutputPad();
        ideLog = new OutputPad(colorizeLogLevels: true);
        callStackPad = new CallStackPad();
        callStackPad.FrameActivated += (file, line) => NavigateTo(file, line);
        testResultsPad = new TestResultsPad();
        testResultsPad.NavigateRequested += (file, line) => NavigateTo(file, line);
        bottomNotebook = Gtk.Notebook.New();
        bottomNotebook.AppendPage(applicationOutput.Widget, Gtk.Label.New("Application Output"));
        bottomNotebook.AppendPage(buildOutput.Widget, Gtk.Label.New("Build Output"));
        bottomNotebook.AppendPage(nugetOutput.Widget, Gtk.Label.New("Nuget Output"));
        bottomNotebook.AppendPage(callStackPad.Widget, Gtk.Label.New("Call Stack"));
        bottomNotebook.AppendPage(testResultsPad.Widget, Gtk.Label.New("Test Results"));
        bottomNotebook.AppendPage(ideLog.Widget, Gtk.Label.New("IDE Log"));
        bottomNotebook.SetVexpand(false);

        buildService.OutputReceived += line => uiContext.Post(_ => buildOutput.AppendLine(line), null);
        runService.OutputReceived += line => uiContext.Post(_ => applicationOutput.AppendLine(line), null);
        nugetService.OutputReceived += line => uiContext.Post(_ => nugetOutput.AppendLine(line), null);
        // The sink replays every line logged before the workbench existed
        // (core runtime init, options auto-backup, ...), then follows along.
        LoggingService.AddSink(line => uiContext.Post(_ => ideLog.AppendLine(line), null));

        // Debugger state changes arrive on background threads.
        DebugService.Paused += (reason, frames) => uiContext.Post(_ => OnDebugPaused(reason, frames), null);
        DebugService.Resumed += () => uiContext.Post(_ => OnDebugResumed(), null);
        DebugService.SessionEnded += exitCode => uiContext.Post(_ => OnDebugEnded(exitCode), null);
        DebugService.OutputReceived += line => uiContext.Post(_ => applicationOutput.AppendLine(line), null);

        // Test-service events arrive on background threads too. The
        // per-test tree rebinds are throttled — a fast run streams hundreds
        // of results a second.
        TestService.TestsChanged += () => uiContext.Post(_ => testsPad.Reload(), null);
        TestService.RunStarted += () => uiContext.Post(_ => testsPad.RefreshStatuses(), null);
        TestService.TestFinished += node =>
        {
            uiContext.Post(_ => testResultsPad.OnTestFinished(node), null);
            QueueTestStatusRefresh();
        };
        TestService.RunFinished += summary => uiContext.Post(_ => OnTestRunFinished(summary), null);
        TestService.OutputReceived += line => uiContext.Post(_ => buildOutput.AppendLine(line), null);

        verticalSplit = Gtk.Paned.New(Gtk.Orientation.Vertical);
        verticalSplit.SetStartChild(documentManager.Widget);
        verticalSplit.SetEndChild(bottomNotebook);
        verticalSplit.SetResizeStartChild(true);
        verticalSplit.SetShrinkStartChild(false);
        verticalSplit.SetPosition(IdePreferences.OutputPanePosition);

        // The Solution and Tests trees share the left dock as notebook tabs.
        leftNotebook = Gtk.Notebook.New();
        leftNotebook.AppendPage(solutionPad.Widget, Gtk.Label.New("Solution"));
        leftNotebook.AppendPage(testsPad.Widget, Gtk.Label.New("Tests"));

        horizontalSplit = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        horizontalSplit.SetStartChild(leftNotebook);
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
            // Loss of power for any emulated app still running: cancelling
            // the run pulls its socket (the head hard-exits on end-of-file).
            runCancellation?.Cancel();
            frameBufferSession?.Shutdown();
            // The emulator is a window of this application: left open it would
            // keep the process alive after the workbench is gone.
            frameBufferEmulator?.SetFrameSource(null);
            frameBufferEmulator?.Dispose();
            frameBufferEmulator = null;
            return false;
        };

        ThemeService.ThemeChanged += OnThemeChanged;
    }

    void OnThemeChanged()
    {
        // Icon variants (~dark) follow the theme: reload the toolbar and
        // pad icons, and restyle the open editors.
        foreach (var (button, iconName) in toolbarButtons)
            button.SetChild(ImageService.CreateImage(iconName));
        solutionPad.RefreshIcons();
        testsPad.RefreshIcons();
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
        SaveFrameBufferEmulatorSize();
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

    // Selects a bottom tab by its widget, so the auto-select behavior
    // survives tab reordering.
    void ShowBottomTab(Gtk.Widget widget) =>
        bottomNotebook.SetCurrentPage(bottomNotebook.PageNum(widget));

    void InstallActions()
    {
        AddAction("new-application", ShowNewApplicationDialog, "<Control><Shift>n");
        AddAction("open-solution", () => _ = OpenSolutionDialogAsync(), "<Control>o");
        closeSolutionAction = AddAction("close-solution", CloseSolution, null, enabled: false);
        AddAction("save", () => { documentManager.SaveActive(); ShowStatus("Saved"); RefreshTests(); }, "<Control>s");
        AddAction("save-all", () => { documentManager.SaveAll(); ShowStatus("All files saved"); RefreshTests(); }, "<Control><Shift>s");
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

        AddAction("complete", () => documentManager.ActiveDocument?.ShowCompletion(), "<Control>space");
        updateCodeBrixPackagesAction = AddAction("update-codebrix-packages", () => _ = UpdateCodeBrixPackagesAsync(), null, enabled: false);
        closeEmulatorAction = AddAction("close-emulator", () => frameBufferEmulator?.Close(), null, enabled: false);
        AddAction("about", ShowAbout);

        // The MonoDevelop convention: Ctrl+T runs every test in the solution.
        runAllTestsAction = AddAction("run-all-tests", () => RunTests(null), "<Control>t", enabled: false);
        runSelectedTestsAction = AddAction("run-selected-tests", RunSelectedTests, null, enabled: false);
        debugSelectedTestAction = AddAction("debug-selected-test", DebugSelectedTest, null, enabled: false);
        // Not Ctrl+Alt+T — desktop environments commonly grab that for
        // "open a terminal", so it would never reach the IDE.
        runTestAtCaretAction = AddAction("run-test-at-caret", RunTestAtCaret, "<Control><Shift>t", enabled: false);
        debugTestAtCaretAction = AddAction("debug-test-at-caret", DebugTestAtCaret, null, enabled: false);
        rediscoverTestsAction = AddAction("rediscover-tests", RefreshTests, null, enabled: false);
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

        var testMenu = Gio.Menu.New();
        testMenu.Append("Run _All Tests", "app.run-all-tests");
        testMenu.Append("Run _Selected Tests", "app.run-selected-tests");
        testMenu.Append("_Debug Selected Test", "app.debug-selected-test");
        testMenu.Append("Run Test at _Caret", "app.run-test-at-caret");
        testMenu.Append("Debug Test at Care_t", "app.debug-test-at-caret");
        testMenu.Append("Re_discover Tests", "app.rediscover-tests");
        testMenu.Append("S_top", "app.stop");

        var toolsMenu = Gio.Menu.New();
        toolsMenu.Append("_Update CodeBrix Package References", "app.update-codebrix-packages");
        toolsMenu.Append("Close _Emulator", "app.close-emulator");

        var helpMenu = Gio.Menu.New();
        helpMenu.Append("_About CodeBrix Develop", "app.about");

        var menubar = Gio.Menu.New();
        menubar.AppendSubmenu("_File", fileMenu);
        menubar.AppendSubmenu("_Edit", editMenu);
        menubar.AppendSubmenu("_Build", buildMenu);
        menubar.AppendSubmenu("_Run", runMenu);
        menubar.AppendSubmenu("Tes_t", testMenu);
        menubar.AppendSubmenu("_Tools", toolsMenu);
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
        if (solution.IsCodeBrixPlatformApplication)
        {
            LoggingService.LogInfo($"Solution '{solution.Name}' is a CodeBrix.Platform application");
            _ = CheckCodeBrixPackagesAsync(solution);
        }
        solutionPad.LoadSolution(solution);
        window.Title = $"{solution.Name} – CodeBrix Develop";
        foreach (var action in new[] { buildAction, rebuildAction, cleanAction, runAction, debugAction, closeSolutionAction })
            action?.SetEnabled(true);
        updateCodeBrixPackagesAction?.SetEnabled(solution.IsCodeBrixPlatformApplication);
        // The Tests pad fills from a fast syntax scan — no build needed.
        SetTestActionsEnabled(TestService.SolutionHasTests(solution));
        _ = TestService.RefreshAsync(solution);
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

    // Silently checks nuget.org for newer versions of the CodeBrix-family
    // packages the solution references and writes a report to the Nuget
    // Output tab. Only when updates are found does it draw attention:
    // status-bar notice + auto-switch to the tab.
    async Task CheckCodeBrixPackagesAsync(Solution solution)
    {
        try
        {
            var perProject = new List<(DotNetProject Project, List<ProjectPackageReference> References)>();
            foreach (var project in solution.Projects)
            {
                var references = project.PackageReferences
                    .Where(reference => NuGetVersionService.IsCodeBrixPackageId(reference.Id))
                    .ToList();
                if (references.Count > 0)
                    perProject.Add((project, references));
            }
            if (perProject.Count == 0)
                return;

            // One query per distinct package, all in parallel; lookups never
            // throw — a failed query yields null.
            var lookups = perProject
                .SelectMany(entry => entry.References.Select(reference => reference.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(id => id, id => NuGetVersionService.GetLatestVersionAsync(id), StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(lookups.Values);

            // The user may have closed or switched solutions while we queried.
            if (!ReferenceEquals(IdeApp.CurrentSolution, solution))
                return;
            RenderCodeBrixPackageReport(solution, perProject,
                lookups.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LoggingService.LogError("CodeBrix package version check failed", ex);
        }
    }

    void RenderCodeBrixPackageReport(
        Solution solution,
        List<(DotNetProject Project, List<ProjectPackageReference> References)> perProject,
        Dictionary<string, string> latestById)
    {
        nugetOutput.Clear();
        nugetOutput.AppendLine($"CodeBrix package versions — solution '{solution.Name}', checked nuget.org {DateTime.Now:yyyy-MM-dd HH:mm}");
        nugetOutput.AppendLine("");

        var width = perProject.SelectMany(entry => entry.References).Max(reference => reference.Id.Length) + 2;
        int total = 0, outdated = 0;
        foreach (var (project, references) in perProject)
        {
            nugetOutput.AppendLine($"{project.Name}:");
            foreach (var reference in references)
            {
                total++;
                var name = ("  " + reference.Id.PadRight(width), OutputColor.Normal);
                var latest = latestById[reference.Id];
                if (latest == null)
                    nugetOutput.AppendSegments(name,
                        (reference.Version.Length > 0 ? reference.Version : "(no version)", OutputColor.Normal),
                        ("  (nuget.org lookup failed)", OutputColor.Warning));
                else if (reference.Version.Length == 0)
                    nugetOutput.AppendSegments(name,
                        ("(no version)", OutputColor.Warning),
                        ($"  latest: {latest}", OutputColor.Good));
                else if (NuGetVersionService.IsUpToDate(reference.Version, latest))
                    nugetOutput.AppendSegments(name, (reference.Version, OutputColor.Good));
                else
                {
                    outdated++;
                    nugetOutput.AppendSegments(name,
                        (reference.Version, OutputColor.Bad),
                        ("  →  ", OutputColor.Normal),
                        (latest, OutputColor.Good));
                }
            }
        }
        nugetOutput.AppendLine("");

        if (outdated > 0)
        {
            nugetOutput.AppendSegments(
                ($"{outdated} of {total} CodeBrix package reference{(total == 1 ? "" : "s")} ", OutputColor.Normal),
                ("not on the latest version", OutputColor.Bad),
                (".", OutputColor.Normal));
            nugetOutput.AppendLine("Select Tools > Update CodeBrix Package References to update them.");
            ShowBottomTab(nugetOutput.Widget);
            ShowStatus($"CodeBrix package updates available — {outdated} reference{(outdated == 1 ? "" : "s")} out of date (see Nuget Output)");
            LoggingService.LogInfo($"CodeBrix package check: {outdated} of {total} references out of date");
        }
        else
        {
            nugetOutput.AppendSegments(
                ($"All {total} CodeBrix package reference{(total == 1 ? " is" : "s are")} ", OutputColor.Normal),
                ("up to date", OutputColor.Good),
                (".", OutputColor.Normal));
            LoggingService.LogInfo($"CodeBrix package check: all {total} references up to date");
        }
    }

    // The Tools > Update CodeBrix Package References command: surveys the
    // CodeBrix-family package references, queries nuget.org for the latest
    // versions (all-or-nothing — any failed lookup cancels the operation),
    // rewrites the outdated versions in the .csproj files, refreshes the
    // in-memory projects and any open (clean) .csproj editor tabs, reports
    // to the Nuget Output tab, and runs dotnet restore.
    async Task UpdateCodeBrixPackagesAsync()
    {
        if (IdeApp.CurrentSolution is not { } solution || !solution.IsCodeBrixPlatformApplication)
            return;

        // Unsaved .csproj edits would be clobbered by the rewrite — cancel
        // before touching anything.
        var unsaved = documentManager.Documents
            .Where(document => document.IsModified && document.FileName.HasExtension(".csproj"))
            .Select(document => document.FileName.FileName)
            .ToList();
        if (unsaved.Count > 0)
        {
            LoggingService.LogError(
                $"Update CodeBrix Package References cannot continue: unsaved changes in {string.Join(", ", unsaved)}. Save the file{(unsaved.Count == 1 ? "" : "s")} and try again.");
            ShowStatus("Update CodeBrix Package References canceled — unsaved .csproj changes (see IDE Log)");
            ShowBottomTab(ideLog.Widget);
            return;
        }

        var dialog = ShowBlockingProgressDialog(out var progressLabel);
        try
        {
            // The Nuget Output still shows the stale open-time "packages out of
            // date" report. Wipe it now, before we query nuget.org and restore,
            // so only this operation's fresh results end up on the tab.
            nugetOutput.Clear();

            var perProject = new List<(DotNetProject Project, List<ProjectPackageReference> References)>();
            foreach (var project in solution.Projects)
            {
                var references = project.PackageReferences
                    .Where(reference => NuGetVersionService.IsCodeBrixPackageId(reference.Id))
                    .ToList();
                if (references.Count > 0)
                    perProject.Add((project, references));
            }
            if (perProject.Count == 0)
                return;

            var ids = perProject
                .SelectMany(entry => entry.References.Select(reference => reference.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            progressLabel.SetText($"Checking nuget.org for {ids.Count} package{(ids.Count == 1 ? "" : "s")}…");
            var lookups = ids.ToDictionary(id => id, id => NuGetVersionService.GetLatestVersionAsync(id), StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(lookups.Values);
            var latestById = lookups.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase);

            // All-or-nothing: a single failed lookup cancels the operation.
            var failed = ids.Where(id => latestById[id] == null).ToList();
            if (failed.Count > 0)
            {
                var message = $"Update CodeBrix Package References canceled: nuget.org lookup failed for {string.Join(", ", failed)}";
                LoggingService.LogError(message);
                nugetOutput.AppendLine($"Update CodeBrix Package References — solution '{solution.Name}', {DateTime.Now:yyyy-MM-dd HH:mm}");
                nugetOutput.AppendLine("");
                nugetOutput.AppendSegments((message, OutputColor.Bad));
                nugetOutput.AppendLine("No project files were changed.");
                ShowBottomTab(nugetOutput.Widget);
                ShowStatus("Update CodeBrix Package References canceled — nuget.org lookups failed");
                return;
            }

            progressLabel.SetText("Updating project files…");
            nugetOutput.AppendLine($"Update CodeBrix Package References — solution '{solution.Name}', {DateTime.Now:yyyy-MM-dd HH:mm}");
            nugetOutput.AppendLine("");

            var width = perProject.SelectMany(entry => entry.References).Max(reference => reference.Id.Length) + 2;
            var changedFiles = new List<FilePath>();
            var updatedCount = 0;
            foreach (var (project, references) in perProject)
            {
                nugetOutput.AppendLine($"{project.Name}:");
                var text = ReadProjectText(project.FileName, out var hadBom);
                var fileChanged = false;
                foreach (var reference in references)
                {
                    var name = ("  " + reference.Id.PadRight(width), OutputColor.Normal);
                    var latest = latestById[reference.Id]!;
                    if (reference.Version.Length == 0)
                    {
                        nugetOutput.AppendSegments(name, ("(no version — skipped)", OutputColor.Warning));
                        continue;
                    }
                    if (NuGetVersionService.IsUpToDate(reference.Version, latest))
                    {
                        nugetOutput.AppendSegments(name, (reference.Version, OutputColor.Good), ("  already latest", OutputColor.Normal));
                        continue;
                    }
                    text = PackageReferenceRewriter.UpdateVersion(text, reference.Id, latest, out var updated);
                    if (updated)
                    {
                        updatedCount++;
                        fileChanged = true;
                        nugetOutput.AppendSegments(name,
                            (latest, OutputColor.Good),
                            ("  updated", OutputColor.Normal),
                            ("  from ", OutputColor.Normal),
                            (reference.Version, OutputColor.Warning));
                    }
                    else
                        nugetOutput.AppendSegments(name,
                            (reference.Version, OutputColor.Bad),
                            ("  could not rewrite the version in the project file", OutputColor.Warning));
                }
                if (fileChanged)
                {
                    WriteProjectText(project.FileName, text, hadBom);
                    changedFiles.Add(project.FileName);
                    project.RefreshFromDisk();
                }
            }

            // Open, clean .csproj tabs pick up the rewritten content.
            foreach (var document in documentManager.Documents)
            {
                if (changedFiles.Contains(document.FileName))
                    document.ReloadFromDisk();
            }

            nugetOutput.AppendLine("");
            if (updatedCount > 0)
            {
                nugetOutput.AppendSegments(
                    ($"Updated {updatedCount} package reference{(updatedCount == 1 ? "" : "s")} in {changedFiles.Count} project file{(changedFiles.Count == 1 ? "" : "s")}.", OutputColor.Good));
                nugetOutput.AppendLine("");
                progressLabel.SetText("Running dotnet restore…");
                var restore = await nugetService.RestoreAsync(solution.FileName);
                nugetOutput.AppendSegments(restore.Success
                    ? ("Restore succeeded.", OutputColor.Good)
                    : ("Restore FAILED — see output above.", OutputColor.Bad));
                ShowStatus($"CodeBrix packages updated — {updatedCount} reference{(updatedCount == 1 ? "" : "s")} in {changedFiles.Count} file{(changedFiles.Count == 1 ? "" : "s")}"
                    + (restore.Success ? ", restore succeeded" : ", restore FAILED"));
                LoggingService.LogInfo($"Update CodeBrix Package References: {updatedCount} references updated in {changedFiles.Count} files; restore {(restore.Success ? "succeeded" : "failed")}");
            }
            else
            {
                nugetOutput.AppendSegments(("All CodeBrix package references are already up to date.", OutputColor.Good));
                ShowStatus("CodeBrix packages are already up to date");
                LoggingService.LogInfo("Update CodeBrix Package References: everything already up to date");
            }
            ShowBottomTab(nugetOutput.Widget);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Update CodeBrix Package References failed", ex);
            ShowStatus("Update CodeBrix Package References failed (see IDE Log)");
            ShowBottomTab(ideLog.Widget);
        }
        finally
        {
            dialog.Destroy();
        }
    }

    // A modal, undecorated-close progress window that blocks the UI while
    // the package update runs; the main loop keeps pumping through the
    // awaits, so the spinner animates and the label updates.
    Gtk.Window ShowBlockingProgressDialog(out Gtk.Label progressLabel)
    {
        var dialog = Gtk.Window.New();
        dialog.Title = "Update CodeBrix Package References";
        dialog.SetTransientFor(window);
        dialog.SetModal(true);
        dialog.SetResizable(false);
        dialog.SetDeletable(false);
        // Swallow close attempts (Alt+F4 etc.) while the operation runs.
        dialog.OnCloseRequest += (_, _) => true;

        var spinner = Gtk.Spinner.New();
        spinner.SetSizeRequest(24, 24);
        spinner.Start();
        progressLabel = Gtk.Label.New("Surveying CodeBrix package references…");
        progressLabel.SetXalign(0);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        box.SetMarginStart(20);
        box.SetMarginEnd(20);
        box.SetMarginTop(16);
        box.SetMarginBottom(16);
        box.Append(spinner);
        box.Append(progressLabel);
        dialog.SetChild(box);
        dialog.Present();
        return dialog;
    }

    // .csproj files often carry a UTF-8 BOM (Visual Studio convention);
    // read and write preserving whichever way the file already is.
    static string ReadProjectText(FilePath file, out bool hadBom)
    {
        var bytes = File.ReadAllBytes(file);
        hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return System.Text.Encoding.UTF8.GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));
    }

    static void WriteProjectText(FilePath file, string text, bool hadBom)
        => File.WriteAllText(file, text, new System.Text.UTF8Encoding(hadBom));

    async Task BuildAsync(bool rebuild)
    {
        if (IdeApp.CurrentSolution is not { } solution || buildService.IsBusy)
            return;
        documentManager.SaveAll();
        buildOutput.Clear();
        ShowBottomTab(buildOutput.Widget);
        ShowStatus(rebuild ? "Rebuilding…" : "Building…");

        var result = rebuild
            ? await buildService.RebuildAsync(solution.FileName)
            : await buildService.BuildAsync(solution.FileName);

        ShowStatus($"{(result.Success ? "Build succeeded" : "Build failed")} — " +
            $"{result.ErrorCount} error{(result.ErrorCount == 1 ? "" : "s")}, " +
            $"{result.WarningCount} warning{(result.WarningCount == 1 ? "" : "s")} " +
            $"({result.Elapsed.TotalSeconds:F1}s)");
        RefreshTests();
    }

    // Rescans the test tree (results of unchanged tests are preserved).
    // Cheap enough to run after every build and save.
    void RefreshTests()
    {
        if (IdeApp.CurrentSolution is { } solution && TestService.SolutionHasTests(solution))
            _ = TestService.RefreshAsync(solution);
    }

    void SetTestActionsEnabled(bool enabled)
    {
        foreach (var action in new[] { runAllTestsAction, runSelectedTestsAction, debugSelectedTestAction,
                     runTestAtCaretAction, debugTestAtCaretAction, rediscoverTestsAction })
            action?.SetEnabled(enabled);
    }

    // Coalesces the per-test tree rebinds a streaming run produces.
    void QueueTestStatusRefresh()
    {
        if (testStatusRefreshQueued)
            return;
        testStatusRefreshQueued = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            uiContext.Post(_ =>
            {
                testStatusRefreshQueued = false;
                testsPad.RefreshStatuses();
            }, null);
        });
    }

    /// <summary>Runs the given test nodes (null/empty runs every test in the solution).</summary>
    public void RunTests(IReadOnlyList<TestNode>? selection) => _ = RunTestsAsync(selection);

    /// <summary>Builds the node's project and debugs its tests (the D.6 args-launch path).</summary>
    public void DebugTest(TestNode node) => _ = DebugTestAsync(node);

    void RunSelectedTests()
    {
        var nodes = testsPad.SelectedNodes;
        if (nodes.Count == 0)
            ShowStatus("Select tests in the Tests pad first");
        else
            RunTests(nodes);
    }

    void DebugSelectedTest()
    {
        var nodes = testsPad.SelectedNodes;
        if (nodes.Count == 0)
            ShowStatus("Select a test in the Tests pad first");
        else
            DebugTest(nodes[0]);
    }

    void RunTestAtCaret()
    {
        if (ResolveTestAtCaret() is { } test)
            RunTests(new[] { test });
    }

    void DebugTestAtCaret()
    {
        if (ResolveTestAtCaret() is { } test)
            DebugTest(test);
    }

    TestNode? ResolveTestAtCaret()
    {
        if (documentManager.ActiveDocument is not { } document)
        {
            ShowStatus("No active document");
            return null;
        }
        var test = TestService.FindTestAtLine(document.FileName, document.GetCaretLine());
        if (test == null)
            ShowStatus("No test found at the caret");
        return test;
    }

    async Task RunTestsAsync(IReadOnlyList<TestNode>? selection)
    {
        if (IdeApp.CurrentSolution == null || TestService.IsRunning || buildService.IsBusy || DebugService.IsSessionActive)
            return;
        documentManager.SaveAll();

        var targets = (selection is { Count: > 0 } ? selection : TestService.Roots)
            .SelectMany(node => node.EnumerateMethods())
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            ShowStatus("No tests found to run");
            return;
        }

        buildOutput.Clear();
        testResultsPad.BeginRun(targets);
        ShowBottomTab(testResultsPad.Widget);
        ShowStatus($"Running {targets.Count} test{(targets.Count == 1 ? "" : "s")}…");
        SetTestActionsEnabled(false);
        stopAction?.SetEnabled(true);
        testRunCancellation = new CancellationTokenSource();
        try
        {
            // Completion UI (results pad, status bar, action re-enable)
            // happens in OnTestRunFinished via the RunFinished event.
            await TestService.RunAsync(selection, testRunCancellation.Token);
        }
        finally
        {
            testRunCancellation.Dispose();
            testRunCancellation = null;
        }
    }

    void OnTestRunFinished(TestRunSummary summary)
    {
        testResultsPad.EndRun(summary);
        testsPad.RefreshStatuses();
        if (!DebugService.IsSessionActive)
            DisableStopUnlessEmulating();
        SetTestActionsEnabled(IdeApp.CurrentSolution is { } solution && TestService.SolutionHasTests(solution));
        if (summary.BuildFailed || summary.Error.Length > 0)
        {
            ShowStatus(summary.Error.Length > 0 ? summary.Error : "Test run failed");
            if (summary.BuildFailed)
                ShowBottomTab(buildOutput.Widget);
        }
        else if (summary.Cancelled)
        {
            ShowStatus("Test run cancelled");
        }
        else
        {
            ShowStatus($"Tests: {summary.Passed} passed, {summary.Failed} failed, {summary.Skipped} skipped " +
                $"({summary.Elapsed.TotalSeconds:F1}s)");
        }
    }

    async Task DebugTestAsync(TestNode node)
    {
        if (DebugService.IsSessionActive || buildService.IsBusy || TestService.IsRunning)
            return;
        documentManager.SaveAll();
        buildOutput.Clear();
        ShowBottomTab(buildOutput.Widget);
        ShowStatus($"Building {node.Project.Name}…");
        var result = await buildService.BuildAsync(node.Project.FileName);
        if (!result.Success)
        {
            ShowStatus("Build failed — test debugging not started");
            return;
        }

        applicationOutput.Clear();
        ShowBottomTab(applicationOutput.Widget);
        ShowStatus($"Debugging {(node.Kind == TestNodeKind.Method ? node.FullName : node.Name)}…");
        try
        {
            // The test executable IS the test host (xUnit v3 runs
            // in-process), so a normal launch with a filter argument debugs
            // exactly this node's tests — breakpoints in test code just work.
            await DebugService.StartAsync(node.Project, TestService.GetFilterArguments(node));
            stopAction?.SetEnabled(true);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Starting the test debug session failed", ex);
            ShowStatus($"Test debugging could not start: {ex.Message}");
        }
    }

    async Task CleanAsync()
    {
        if (IdeApp.CurrentSolution is not { } solution || buildService.IsBusy)
            return;
        buildOutput.Clear();
        ShowBottomTab(buildOutput.Widget);
        ShowStatus("Cleaning…");
        var result = await buildService.CleanAsync(solution.FileName);
        ShowStatus(result.Success ? "Clean succeeded" : "Clean failed");
    }

    // Resolves the project the Run/Debug commands start, applying the
    // first-run rule: with no explicit choice, the OS/session-appropriate head
    // auto-selected by GetStartupProject becomes (and is persisted as) the
    // startup project.
    DotNetProject? ResolveStartupProjectForLaunch(Solution solution)
    {
        if (IdeApp.GetStartupProject(solution) is not { } project)
        {
            ShowStatus("The solution has no executable project to run");
            return null;
        }
        if (string.IsNullOrEmpty(IdePreferences.StartupProject.Value) && IdeApp.IsPlatformHead(project))
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
        else if (TestService.IsRunning)
            testRunCancellation?.Cancel();
        else if (runCancellation != null)
            runCancellation.Cancel();
        else if (frameBufferEmulationRunning)
        {
            // The emulated application stops; its window stays exactly where
            // it is (and, for now, looks no different — it is always black).
            SetFrameBufferEmulationRunning(false);
            ShowStatus("Frame Buffer emulation stopped");
        }
    }

    /// <summary>
    /// Shows the frame-buffer emulator for the given head. A Linux Frame
    /// Buffer head has no frame-buffer device to draw on inside a desktop
    /// session, so Run and Debug deliberately build and launch nothing: they
    /// open this window instead. Every other head builds and launches as
    /// usual.
    /// </summary>
    void ShowFrameBufferEmulator(DotNetProject project)
    {
        if (frameBufferEmulator == null)
        {
            // The orientation and resolution are read HERE, when the window is
            // created — an Options change applies to the next emulator window,
            // not to one already open.
            frameBufferEmulator = new FrameBufferEmulatorWindow(application,
                IdePreferences.FrameBufferScreenOrientation.Value,
                IdePreferences.FrameBufferScreenResolution.Value,
                IdePreferences.FrameBufferWindowWidth, IdePreferences.FrameBufferWindowHeight);
            frameBufferEmulator.Closed += (_, _) => OnFrameBufferEmulatorClosed();
            // Touches (device pixels) go to whatever app is currently live;
            // with no session the finger presses a powered-off screen.
            frameBufferEmulator.Touch += (kind, x, y) => frameBufferSession?.SendTouch(kind, x, y);
            closeEmulatorAction?.SetEnabled(true);
        }

        // Present raises an already-open window to the front without moving it.
        frameBufferEmulator.Present($"{project.Name} — Frame Buffer");
        SetFrameBufferEmulationRunning(true);

        var orientation = frameBufferEmulator.Orientation;
        ShowStatus("Frame Buffer emulation — " +
            $"{frameBufferEmulator.Resolution.GetLabel(orientation)}, {orientation.GetLabel()}");
    }

    /// <summary>
    /// The shared front half of an emulated Run/Debug: saves, shows the
    /// emulator window, verifies the head can be swapped, and builds against
    /// the emulated head package. Returns the emulated device's resolution —
    /// the one the EXISTING window was built with, never what Options
    /// currently says — or null when emulation could not start (the status
    /// and output already say why).
    /// </summary>
    async Task<(int Width, int Height)?> PrepareFrameBufferLaunchAsync(DotNetProject project, CancellationToken cancellationToken)
    {
        documentManager.SaveAll();
        ShowFrameBufferEmulator(project);
        if (frameBufferEmulator == null)
            return null;

        if (!FrameBufferHeadSwap.CanSwap(project))
        {
            applicationOutput.Clear();
            ShowBottomTab(applicationOutput.Widget);
            applicationOutput.AppendLine(
                $"{project.Name} does not reference {FrameBufferHeadSwap.FrameBufferPackageId}, " +
                "so it cannot be built against the emulated frame-buffer head.");
            SetFrameBufferEmulationRunning(false);
            ShowStatus("Frame Buffer emulation could not start");
            return null;
        }

        // Resolution lockstep: the app is launched at the resolution this
        // window was created with; changing Options means closing the
        // emulator and running again.
        var deviceWidth = frameBufferEmulator.Resolution.GetWidth(frameBufferEmulator.Orientation);
        var deviceHeight = frameBufferEmulator.Resolution.GetHeight(frameBufferEmulator.Orientation);

        buildOutput.Clear();
        ShowBottomTab(buildOutput.Widget);
        ShowStatus($"Building {project.Name} for the Frame Buffer emulator…");
        var swapArgument = await FrameBufferHeadSwap.CreateSwapArgumentAsync(project, cancellationToken);
        var result = await buildService.BuildAsync(project.FileName, cancellationToken, swapArgument);
        if (!result.Success)
        {
            ShowStatus(cancellationToken.IsCancellationRequested ? "Build canceled" : "Build failed");
            SetFrameBufferEmulationRunning(false);
            return null;
        }
        return (deviceWidth, deviceHeight);
    }

    async Task RunFrameBufferEmulatedAsync(DotNetProject project)
    {
        FrameBufferEmulatorSession? session = null;
        runCancellation = new CancellationTokenSource();
        try
        {
            if (await PrepareFrameBufferLaunchAsync(project, runCancellation.Token) is not { } device)
                return;

            var executable = await project.GetOutputExecutableAsync(cancellationToken: runCancellation.Token);
            applicationOutput.Clear();
            ShowBottomTab(applicationOutput.Widget);
            ShowStatus($"Running {project.Name} in the Frame Buffer emulator…");

            session = new FrameBufferEmulatorSession(device.Width, device.Height);
            frameBufferSession = session;
            frameBufferEmulator!.SetFrameSource(session);

            using var killCts = new CancellationTokenSource();
            var capturedSession = session;
            using var stopRegistration = runCancellation.Token.Register(() =>
            {
                // Stop is loss of power: closing the socket makes the head
                // hard-exit on end-of-file; the SIGKILL backstop covers an
                // app that somehow survives the unplugging.
                capturedSession.Shutdown();
                killCts.CancelAfter(TimeSpan.FromSeconds(1));
            });

            var exitCode = await runService.RunExecutableAsync(executable, project.BaseDirectory,
                session.EnvironmentVariables, killCts.Token);
            ShowStatus(runCancellation.IsCancellationRequested
                ? "Frame Buffer emulation stopped"
                : $"{project.Name} exited with code {exitCode} — the emulated device powered off");
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Frame Buffer emulation failed", ex);
            ShowStatus($"Frame Buffer emulation failed: {ex.Message}");
        }
        finally
        {
            // Detach before disposing: the window polls the session from its
            // draw tick. The screen going black is the device powering off.
            frameBufferEmulator?.SetFrameSource(null);
            frameBufferSession = null;
            session?.Dispose();
            runCancellation?.Dispose();
            runCancellation = null;
            SetFrameBufferEmulationRunning(false);
        }
    }

    async Task DebugFrameBufferEmulatedAsync(DotNetProject project)
    {
        if (await PrepareFrameBufferLaunchAsync(project, CancellationToken.None) is not { } device)
            return;

        applicationOutput.Clear();
        ShowBottomTab(applicationOutput.Widget);
        ShowStatus($"Debugging {project.Name} in the Frame Buffer emulator…");
        var session = new FrameBufferEmulatorSession(device.Width, device.Height);
        try
        {
            frameBufferSession = session;
            frameBufferEmulator!.SetFrameSource(session);
            await DebugService.StartAsync(project, environment: session.EnvironmentVariables);
            stopAction?.SetEnabled(true);
            // The session is cleaned up in OnDebugEnded, whichever way the
            // debuggee goes away.
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Starting the debugger failed", ex);
            ShowStatus($"Debugging could not start: {ex.Message}");
            CleanupFrameBufferSession();
        }
    }

    // Powers off whatever emulated app session is live: black screen, socket
    // closed (the head hard-exits on end-of-file), transport disposed.
    void CleanupFrameBufferSession()
    {
        if (frameBufferSession is not { } session)
            return;
        frameBufferEmulator?.SetFrameSource(null);
        frameBufferSession = null;
        session.Shutdown();
        session.Dispose();
        SetFrameBufferEmulationRunning(false);
    }

    void OnFrameBufferEmulatorClosed()
    {
        SaveFrameBufferEmulatorSize();
        frameBufferEmulator = null;
        closeEmulatorAction?.SetEnabled(false);
        // Closing the emulator with an app running terminates the app — the
        // user unplugged the device.
        if (frameBufferEmulationRunning)
        {
            if (DebugService.IsSessionActive && frameBufferSession != null)
                _ = DebugService.StopAsync();
            else
                runCancellation?.Cancel();
        }
        SetFrameBufferEmulationRunning(false);
        ShowStatus("Frame Buffer emulator closed");
    }

    // While the emulated application is "running", Stop is available and
    // Run/Debug are not — the same shape as a launched head.
    void SetFrameBufferEmulationRunning(bool running)
    {
        frameBufferEmulationRunning = running;
        stopAction?.SetEnabled(running);
        var canLaunch = !running && IdeApp.CurrentSolution != null;
        runAction?.SetEnabled(canLaunch);
        debugAction?.SetEnabled(canLaunch);
    }

    void SaveFrameBufferEmulatorSize()
    {
        if (frameBufferEmulator == null || !frameBufferEmulator.TryGetSize(out var width, out var height))
            return;
        IdePreferences.FrameBufferWindowWidth.Value = width;
        IdePreferences.FrameBufferWindowHeight.Value = height;
    }

    // Stop stays available while the emulator is running even though no
    // process is.
    void DisableStopUnlessEmulating() =>
        stopAction?.SetEnabled(frameBufferEmulationRunning);

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

        if (StartupHeadPolicy.IsFrameBufferHead(project.Name))
        {
            await DebugFrameBufferEmulatedAsync(project);
            return;
        }

        documentManager.SaveAll();
        buildOutput.Clear();
        ShowBottomTab(buildOutput.Widget);
        ShowStatus($"Building {project.Name}…");
        var result = await buildService.BuildAsync(project.FileName);
        if (!result.Success)
        {
            ShowStatus("Build failed — debugging not started");
            return;
        }

        applicationOutput.Clear();
        ShowBottomTab(applicationOutput.Widget);
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
        ShowBottomTab(callStackPad.Widget);

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
        CleanupFrameBufferSession();
        OnDebugResumed();
        DisableStopUnlessEmulating();
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

        if (StartupHeadPolicy.IsFrameBufferHead(project.Name))
        {
            await RunFrameBufferEmulatedAsync(project);
            return;
        }

        documentManager.SaveAll();
        applicationOutput.Clear();
        ShowBottomTab(applicationOutput.Widget);
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
            runCancellation.Dispose();
            runCancellation = null;
            DisableStopUnlessEmulating();
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
        // Presenting the modal dialog right here — inside the activate
        // callback, before the compositor has actually shown the workbench
        // window — leaves the dialog invisible on Wayland while its modal
        // grab disables the whole main window (the startup "hang"). GTK
        // already considers the window mapped at this point, so waiting for
        // the map signal would not help; wait for the frame clock instead:
        // the first tick only comes once the window is genuinely drawing on
        // screen, and a dialog presented then behaves normally.
        window.AddTickCallback((_, _) =>
        {
            ShowNewApplicationDialog();
            return false; // one-shot
        });
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
        // breakpoints are lost too. Emulation belonged to a head in this
        // solution, so it stops too — the window itself is left alone.
        frameBufferEmulationRunning = false;
        DebugService.Shutdown(clearBreakpoints: true);
        documentManager.SaveAll();
        foreach (var document in documentManager.Documents.ToList())
            documentManager.CloseDocument(document);
        TypeSystemService.UnloadSolution();
        IdeApp.CurrentSolution = null;
        solutionPad.Clear();
        TestService.Clear();
        testResultsPad.Clear();
        // Every output tab describes the now-closed solution; blank them so the
        // stale build/run/NuGet/call-stack text isn't mistaken for the next
        // solution's. The IDE Log is not solution-scoped — it keeps accumulating
        // for the life of the process, so it is deliberately left untouched.
        buildOutput.Clear();
        applicationOutput.Clear();
        nugetOutput.Clear();
        callStackPad.Clear();
        foreach (var action in new[] { buildAction, rebuildAction, cleanAction, runAction, debugAction,
                     stepOverAction, stepIntoAction, stepOutAction, closeSolutionAction, updateCodeBrixPackagesAction })
            action?.SetEnabled(false);
        SetTestActionsEnabled(false);
        window.Title = "CodeBrix Develop";
        IdePreferences.LastSolution.Value = "";
        IdePreferences.StartupProject.Value = "";
        ShowStatus("Ready");
    }

    void ShowNewApplicationDialog()
    {
        var dialog = new NewApplicationDialog(window);
        dialog.Created += (slnxPath, warning) => _ = OpenNewApplicationAsync(slnxPath, warning);
        dialog.Failed += message =>
        {
            // The generated files are still there; the IDE Log says why the
            // process could not be finished.
            ShowStatus(message);
            ShowBottomTab(ideLog.Widget);
        };
        dialog.Present();
    }

    // Opens a just-generated application, then — after the load has had its
    // say on the status bar — reports anything the generation could not
    // finish, such as package versions that could not be looked up.
    async Task OpenNewApplicationAsync(FilePath slnxPath, string? warning)
    {
        await LoadSolutionAsync(slnxPath);
        if (!string.IsNullOrEmpty(warning))
            ShowStatus(warning);
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
