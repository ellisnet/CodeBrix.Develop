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
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Options;
using CodeBrix.Develop.Ide;
using CodeBrix.Develop.Ide.Themes;
using Gio = CodeBrix.Develop.UI.Gio;
using Gtk = CodeBrix.Develop.UI.Gtk;

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
