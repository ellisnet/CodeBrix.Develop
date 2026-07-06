//
// SolutionPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Pads.SolutionPad, rebuilt on the
//      GTK 4 ListView + TreeListModel for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Projects;
using Gio = CodeBrix.Develop.UI.Gio;
using GObject = CodeBrix.Develop.UI.GObject;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>
/// The Solution pad: a lazily populated tree of the loaded solution, its
/// projects, and their folders and files.
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

    /// <summary>Raised when the user activates (double-clicks / Enter) a file node.</summary>
    public event Action<FilePath>? FileActivated;

    /// <summary>Creates the pad and its (initially empty) tree.</summary>
    public SolutionPad()
    {
        rootStore = Gio.ListStore.New(SolutionTreeNode.GetGType());
        createChildModel = CreateChildModel;
        var treeModel = Gtk.TreeListModel.New(rootStore, passthrough: false, autoexpand: false, createFunc: createChildModel);
        selection = Gtk.SingleSelection.New(treeModel);

        factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += static (_, args) =>
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
        };
        factory.OnBind += static (_, args) =>
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
    public void RefreshIcons()
    {
        listView.SetFactory(null);
        listView.SetFactory(factory);
    }

    /// <summary>Replaces the tree content with the given solution.</summary>
    public void LoadSolution(Solution solution)
    {
        rootStore.RemoveAll();
        rootStore.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.Solution, $"Solution '{solution.Name}'", solution.FileName));
    }

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
                        children.Append(SolutionTreeNode.Create(SolutionTreeNodeKind.Project, project.Name, project.FileName));
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
