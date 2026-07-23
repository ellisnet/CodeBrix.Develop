//
// Program.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Startup / IdeStartup, simplified for
//      CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Options;
using CodeBrix.Develop.Core.Templates;
using CodeBrix.Develop.Ide;
using CodeBrix.Develop.Ide.Themes;
using Gio = CodeBrix.Develop.UI.Gio;
using GLib = CodeBrix.Develop.UI.GLib;
using Gtk = CodeBrix.Develop.UI.Gtk;
using GtkSource = CodeBrix.Develop.UI.GtkSource;

namespace CodeBrix.Develop;

static class Program
{
    static int Main(string[] args)
    {
        // Locate the SDK's MSBuild before anything touches Roslyn workspaces.
        Runtime.Initialize();

        // Open (or silently create) options.sqlite and run the startup
        // auto-backup + pruning — before any UI exists, so everything below
        // can read configuration.
        PropertyService.Initialize();

        // Record this machine's operating system, CPU architecture, and desktop
        // session type as hidden options (overwriting the previous run's), then
        // report them — before anything reads them (e.g. startup-head selection).
        EnvironmentInfo.DetectStoreAndReport();

        // Launch-time background check for a newer New-Application template on
        // CodeBrix.Platform main (throttled, best-effort, never blocks startup).
        TemplateUpdater.StartBackgroundCheck();

        // Last-resort exception handling. Without a handler, an exception
        // escaping a GTK signal handler terminates the process at the native
        // boundary (the binding prints it and calls Environment.Exit). Log it
        // to the console + IDE Log and keep the IDE alive instead.
        GLib.UnhandledException.SetHandler(ex =>
        {
            try
            {
                LoggingService.LogError("Unhandled exception", ex);
                IdeApp.Workbench?.ShowStatus("An internal error occurred — see the IDE Log tab for details");
            }
            catch
            {
                // never let the handler itself take the process down
            }
        });
        // Fire-and-forget task faults surface at garbage collection; fatal
        // CLR-level crashes at least leave their details in the log.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            LoggingService.LogError("Unobserved task exception", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LoggingService.LogError($"Fatal unhandled exception: {e.ExceptionObject}");

        // Gtk.Application only initializes the Gtk module implicitly; the
        // GtkSource module must be initialized explicitly so its types are
        // registered for dynamically wrapped return values — without this,
        // Buffer.CreateSourceMark's GtkSourceMark comes back wrapped as a
        // Gtk.TextMark and the typed cast crashes (the breakpoint-click bug).
        GtkSource.Module.Initialize();

        var application = Gtk.Application.New("com.codebrix.develop", Gio.ApplicationFlags.NonUnique);
        application.OnActivate += (sender, eventArgs) =>
        {
            // The color theme must be applied before the first window renders.
            ThemeService.Initialize();

            IdeApp.Initialize((Gtk.Application) sender);

            // A solution/project on the command line wins (unless it is the
            // IDE's own csproj, forwarded by "dotnet run"); otherwise reopen
            // the last solution, or show the New Application experience.
            if (args.Length > 0 && File.Exists(args[0]) && !IdeApp.IsOwnProjectFile(args[0]))
                _ = IdeApp.Workbench!.LoadSolutionAsync(Path.GetFullPath(args[0]));
            else
                _ = IdeApp.Workbench!.RestoreStartupSolutionAsync();
        };

        LoggingService.LogInfo("CodeBrix Develop starting");
        var exitCode = application.RunWithSynchronizationContext(null);

        // A restart request (e.g. to adopt an imported options file) is
        // honored only after the GTK main loop — and with it every handle on
        // options.sqlite — has fully wound down.
        if (IdeApp.RestartRequested && Environment.ProcessPath is { } processPath)
        {
            LoggingService.LogInfo("CodeBrix Develop restarting");
            PropertyService.Store.Dispose(); // the new process takes over options.sqlite
            var startInfo = new ProcessStartInfo(processPath) { UseShellExecute = false };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);
            Process.Start(startInfo);
        }
        return exitCode;
    }
}
