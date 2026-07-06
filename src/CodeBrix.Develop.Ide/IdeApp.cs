//
// IdeApp.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.IdeApp, simplified for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using CodeBrix.Develop.Core.Projects;
using CodeBrix.Develop.Ide.Gui;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide;

/// <summary>
/// The static root of the IDE: the workbench window and the currently
/// loaded solution.
/// </summary>
public static class IdeApp
{
    /// <summary>The main window, available after <see cref="Initialize"/>.</summary>
    public static Workbench? Workbench { get; private set; }

    /// <summary>The currently loaded solution, or null.</summary>
    public static Solution? CurrentSolution { get; internal set; }

    /// <summary>Creates and presents the workbench for the given application.</summary>
    public static void Initialize(Gtk.Application application)
    {
        Workbench = new Workbench(application);
        Workbench.Present();
    }
}
