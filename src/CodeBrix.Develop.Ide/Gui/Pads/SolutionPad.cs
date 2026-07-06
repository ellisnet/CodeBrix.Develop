//
// SolutionPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Pads.SolutionPad, rebuilt on the
//      GTK 4 ListView + TreeListModel for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Projects;
using Gio = CodeBrix.Develop.UI.Gio;
using GObject = CodeBrix.Develop.UI.GObject;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>
/// The Solution pad: a lazily populated tree of the loaded solution, its
/// projects, and their folders and files. The project the Run command
/// starts (the startup project) is shown in bold, and executable project
/// nodes offer "Set as Startup Project" on right-click.
/// </summary>
public class SolutionPad
{
    readonly Gtk.ScrolledWindow scrolled;
    readonly Gtk.ListView listView;
    readonly Gio.ListStore rootStore;
    readonly Gtk.SingleSelection selection;
    readonly Gtk.SignalListItemFactory factory;
    // The create-model callback is marshalled to native code; keep a strong
    // reference so the delegate outlives the tree model.
    readonly Gtk.TreeListModelCreateModelFunc createChildModel;
    // Same rule for the per-row right-click gestures: without a managed
    // reference their signal closures can be collected and never fire.
    readonly List<Gtk.GestureClick> rowGestures = new();

    // The full path of the effective startup project ("" when no solution),
    // cached so every row bind does not re-run the preference validation.
    string startupProjectPath = "";

    /// <summary>Raised when the user activates (double-clicks / Enter) a file node.</summary>
    public event Action<FilePath>? FileActivated;

    /// <summary>
    /// Raised after the user picked "Set as Startup Project" on a project
    /// node (the preference is already updated).
    /// </summary>
    public event Action? StartupProjectChanged;

    /// <summary>Creates the pad and its (initially empty) tree.</summary>
    public SolutionPad()
    {
        rootStore = Gio.ListStore.New(SolutionTreeNode.GetGType());
        createChildModel = CreateChildModel;
        var treeModel = Gtk.TreeListModel.New(rootStore, passthrough: false, autoexpand: false, createFunc: createChildModel);
        selection = Gtk.SingleSelection.New(treeModel);

        factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            var expander = Gtk.TreeExpander.New();
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            var image = Gtk.Image.New();
            image.SetPixelSize(16);
            box.Append(image);
            var label = Gtk.Label.New(null);
            label.SetXalign(0);
            box.Append(label);
            expander.SetChild(box);
            listItem.SetChild(expander);

            // Right-click on an executable project row offers "Set as
            // Startup Project" — the node is resolved at click time because
            // the recycled row widget is rebound as the user scrolls.
            var rightClick = Gtk.GestureClick.New();
            rightClick.SetButton(3);
            // Capture phase: the ListView's own click handling must not
            // swallow the press before it reaches the row content.
            rightClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
            rightClick.OnPressed += (_, _) =>
            {
                if (listItem.GetItem() is Gtk.TreeListRow row && row.GetItem() is SolutionTreeNode node)
                    ShowProjectContextMenu(node, expander);
            };
            expander.AddController(rightClick);
            rowGestures.Add(rightClick);
        };
        factory.OnBind += (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            if (listItem.GetItem() is not Gtk.TreeListRow row || row.GetItem() is not SolutionTreeNode node)
                return;
            var expander = (Gtk.TreeExpander) listItem.GetChild()!;
            expander.SetListRow(row);
            var box = (Gtk.Box) expander.GetChild()!;
            var image = (Gtk.Image) box.GetFirstChild()!;
            var label = (Gtk.Label) image.GetNextSibling()!;
            image.SetFromPaintable(ImageService.GetIcon(node.IconName));
            if (node.Kind == SolutionTreeNodeKind.Project && node.Path == startupProjectPath)
                label.SetMarkup($"<b>{MarkupEscape(node.Title)}</b>");
            else
                label.SetText(node.Title);
        };

        listView = Gtk.ListView.New(selection, factory);
        listView.OnActivate += (_, args) =>
        {
            var treeListModel = (Gtk.TreeListModel) selection.GetModel()!;
            if (treeListModel.GetRow(args.Position) is not Gtk.TreeListRow row)
                return;
            if (row.GetItem() is not SolutionTreeNode node)
                return;
            if (node.Kind == SolutionTreeNodeKind.File)
                FileActivated?.Invoke(node.FilePath);
            else
                row.SetExpanded(!row.GetExpanded());
        };

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(listView);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);
        scrolled.AddCssClass("cb-sidebar");
    }

    /// <summary>The widget to place in the workbench.</summary>
    public Gtk.Widget Widget => scrolled;

    /// <summary>
    /// Rebinds all visible rows so their icons reload for the current theme
    /// (called after a color-theme change flips the dark/light icon variants).
    /// </summary>
    public void RefreshIcons() => RebindRows();

    /// <summary>Replaces the tree content with the given solution.</summary>
    public void LoadSolution(Solution solution)
    {
        rootStore.RemoveAll();
        rootStore.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.Solution, $"Solution '{solution.Name}'", solution.FileName));
        RefreshStartupProject();
    }

    /// <summary>Empties the tree (the solution was closed).</summary>
    public void Clear()
    {
        rootStore.RemoveAll();
        startupProjectPath = "";
    }

    /// <summary>
    /// Re-reads the effective startup project and updates the bold
    /// indicator on the visible rows.
    /// </summary>
    public void RefreshStartupProject()
    {
        var startup = IdeApp.GetStartupProject(IdeApp.CurrentSolution);
        startupProjectPath = startup == null ? "" : (string) startup.FileName;
        RebindRows();
    }

    // Resetting the factory makes the ListView re-run OnBind for every
    // visible row.
    void RebindRows()
    {
        listView.SetFactory(null);
        listView.SetFactory(factory);
    }

    void ShowProjectContextMenu(SolutionTreeNode node, Gtk.Widget anchor)
    {
        if (node.Kind != SolutionTreeNodeKind.Project || IdeApp.CurrentSolution is not { } solution)
            return;
        var project = solution.Projects.FirstOrDefault(candidate =>
            string.Equals((string) candidate.FileName, node.Path, StringComparison.Ordinal));
        if (project is not { IsExecutable: true })
            return;

        var setStartupButton = Gtk.Button.NewWithLabel("Set as Startup Project");
        setStartupButton.SetHasFrame(false);
        var popover = Gtk.Popover.New();
        popover.SetChild(setStartupButton);
        popover.SetParent(anchor);
        popover.OnClosed += (_, _) => popover.Unparent();
        setStartupButton.OnClicked += (_, _) =>
        {
            IdePreferences.StartupProject.Value = node.Path;
            popover.Popdown();
            RefreshStartupProject();
            StartupProjectChanged?.Invoke();
        };
        popover.Popup();
    }

    static string MarkupEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static Gio.ListModel? CreateChildModel(GObject.Object item)
    {
        if (item is not SolutionTreeNode node || !node.HasChildren)
            return null;

        var children = Gio.ListStore.New(SolutionTreeNode.GetGType());
        try
        {
            switch (node.Kind)
            {
                case SolutionTreeNodeKind.Solution:
                    var solution = IdeApp.CurrentSolution;
                    if (solution == null)
                        return null;
                    foreach (var project in solution.Projects)
                    {
                        if (project.SolutionFolder.Length == 0)
                            children.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.Project, project.Name, project.FileName));
                    }
                    // Solution folders (collapsed until expanded) follow the
                    // root-level projects.
                    foreach (var folderName in solution.SolutionFolderNames)
                        children.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.SolutionFolder, folderName, default));
                    break;

                case SolutionTreeNodeKind.SolutionFolder:
                    if (IdeApp.CurrentSolution is not { } currentSolution)
                        return null;
                    foreach (var project in currentSolution.Projects)
                    {
                        if (project.SolutionFolder == node.Title)
                            children.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.Project, project.Name, project.FileName));
                    }
                    break;

                case SolutionTreeNodeKind.Project:
                case SolutionTreeNodeKind.Folder:
                    var directory = node.Kind == SolutionTreeNodeKind.Project
                        ? node.FilePath.ParentDirectory
                        : node.FilePath;
                    foreach (var sub in DotNetProject.GetVisibleDirectories(directory))
                        children.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.Folder, sub.FileName, sub));
                    foreach (var file in DotNetProject.GetVisibleFiles(directory))
                        children.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.File, file.FileName, file));
                    break;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Failed to expand {node.Path}", ex);
        }
        return children;
    }
}
