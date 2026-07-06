//
// Program.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Startup / IdeStartup, simplified for
//      CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
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

            // Optional command-line argument: a solution/project to open.
            if (args.Length > 0 && File.Exists(args[0]))
                _ = IdeApp.Workbench!.LoadSolutionAsync(Path.GetFullPath(args[0]));
        };

        LoggingService.LogInfo("CodeBrix Develop starting");
        return application.RunWithSynchronizationContext(null);
    }
}
