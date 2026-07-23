//
// IdeApp.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.IdeApp, simplified for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core;
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

    /// <summary>The GTK application, available after <see cref="Initialize"/>.</summary>
    public static Gtk.Application? Application { get; private set; }

    /// <summary>The currently loaded solution, or null.</summary>
    public static Solution? CurrentSolution { get; internal set; }

    /// <summary>
    /// True after <see cref="Restart"/> was called: the process should
    /// relaunch itself once the GTK main loop has exited.
    /// </summary>
    public static bool RestartRequested { get; private set; }

    /// <summary>Creates and presents the workbench for the given application.</summary>
    public static void Initialize(Gtk.Application application)
    {
        Application = application;
        Workbench = new Workbench(application);
        Workbench.Present();
    }

    /// <summary>
    /// Quits the application and asks the entry point to relaunch it — used
    /// after staging an imported options file, which is only adopted at
    /// startup.
    /// </summary>
    public static void Restart()
    {
        RestartRequested = true;
        Application?.Quit();
    }

    /// <summary>
    /// The project the Run command starts for the given solution: the project
    /// chosen via "Set as Startup Project" when it is still a member of this
    /// solution, executable, present on disk, and able to run on the current
    /// operating system and desktop session — otherwise that stored choice is
    /// silently cleared. With no usable stored choice, a CodeBrix.Platform head
    /// is auto-selected by the OS/session preference order (see
    /// <see cref="StartupHeadPolicy"/>), and a solution with no recognized
    /// heads falls back to its first executable project.
    /// </summary>
    public static DotNetProject? GetStartupProject(Solution? solution)
    {
        if (solution == null)
            return null;

        var operatingSystem = EnvironmentInfo.CurrentOperatingSystem;
        var sessionType = EnvironmentInfo.CurrentDesktopSessionType;

        var configured = IdePreferences.StartupProject.Value;
        if (!string.IsNullOrEmpty(configured))
        {
            var match = solution.Projects.FirstOrDefault(project =>
                string.Equals((string) project.FileName, configured, StringComparison.Ordinal));
            if (match is { IsExecutable: true } && File.Exists(configured)
                && StartupHeadPolicy.CanRun(match.Name, operatingSystem, sessionType))
                return match;
            // Missing, non-executable, or a head that can't run on this machine:
            // clear the stored choice and fall through to auto-selection.
            IdePreferences.StartupProject.Value = "";
        }

        var heads = solution.Projects
            .Where(project => project.IsExecutable && StartupHeadPolicy.IsHead(project.Name))
            .ToList();
        if (heads.Count > 0)
        {
            foreach (var kind in StartupHeadPolicy.AutoStartupPreference(operatingSystem, sessionType))
            {
                var head = heads.FirstOrDefault(candidate =>
                    string.Equals(StartupHeadPolicy.GetHeadKind(candidate.Name), kind, StringComparison.Ordinal));
                if (head != null)
                    return head;
            }
            return null; // a head-bearing solution with no head runnable on this machine
        }

        // A solution with no recognized heads keeps the plain default.
        return solution.StartupProject;
    }

    /// <summary>Whether the project is a recognized CodeBrix.Platform head (by naming convention).</summary>
    public static bool IsPlatformHead(DotNetProject project) => StartupHeadPolicy.IsHead(project.Name);

    /// <summary>
    /// Whether the given project's "Set as Startup Project" command should be
    /// available on this machine: any non-head executable, or a
    /// CodeBrix.Platform head that can run on the current operating system and
    /// desktop session.
    /// </summary>
    public static bool CanBeStartupProject(DotNetProject project) =>
        project is { IsExecutable: true }
        && StartupHeadPolicy.CanRun(project.Name,
            EnvironmentInfo.CurrentOperatingSystem, EnvironmentInfo.CurrentDesktopSessionType);

    /// <summary>
    /// Whether the path is the IDE's own executable project file.
    /// "dotnet run CodeBrix.Develop.csproj" forwards the project file to the
    /// application as an argument — auto-opening the IDE's own project is
    /// never what the user meant, so such a path is ignored at startup.
    /// </summary>
    public static bool IsOwnProjectFile(string path) =>
        string.Equals(Path.GetFileName(path), "CodeBrix.Develop.csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folder where the user's projects normally live: the configured
    /// project folder location when set, otherwise the user's Documents
    /// folder. A configured folder that does not exist (for example after
    /// importing options from another machine) is silently blanked, falling
    /// back to Documents.
    /// </summary>
    public static string GetProjectsDirectory()
    {
        var configured = IdePreferences.ProjectsFolder.Value;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured))
                return Path.GetFullPath(configured);
            IdePreferences.ProjectsFolder.Value = "";
        }
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : documents;
    }
}
