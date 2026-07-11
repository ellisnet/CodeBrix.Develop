//
// TestsPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.UnitTesting.TestPad, rebuilt on the GTK 4
//      ListView + TreeListModel for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Testing;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gio = CodeBrix.Develop.UI.Gio;
using GObject = CodeBrix.Develop.UI.GObject;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>
/// One row of the Tests pad tree, wrapping a UI-free <see cref="TestNode"/>
/// as a GObject list-model item.
/// </summary>
[GObject.Subclass<GObject.Object>]
public partial class TestTreeNode
{
    /// <summary>The wrapped test-tree node.</summary>
    public TestNode? Model { get; set; }

    /// <summary>Creates a row for the given test node.</summary>
    public static TestTreeNode Create(TestNode model)
    {
        var node = NewWithProperties(Array.Empty<GObject.ConstructArgument>());
        node.Model = model;
        return node;
    }

    /// <summary>The themed icon name for a test status.</summary>
    public static string IconNameFor(TestStatus status) => status switch
    {
        TestStatus.Running => "unit-running-16",
        TestStatus.Passed => "unit-success-16",
        TestStatus.Failed => "unit-failed-16",
        TestStatus.Skipped => "unit-skipped-16",
        TestStatus.Mixed => "unit-mixed-results-16",
        _ => "unit-not-yet-run-16",
    };
}

/// <summary>
/// The Tests pad: the solution's automated tests as a live tree — project →
/// namespace → class → method — with per-node red/green/yellow status icons,
/// a Run/Debug/Stop toolbar, and a text filter. Shares the left dock with
/// the Solution pad (a Solution | Tests notebook).
/// </summary>
public class TestsPad
{
    readonly Gtk.Box root;
    readonly Gtk.ScrolledWindow scrolled;
    readonly Gtk.ListView listView;
    readonly Gtk.SignalListItemFactory factory;
    readonly Gtk.SearchEntry filterEntry;
    // Callbacks marshalled to native code: keep strong references so their
    // delegates outlive the widgets (same rule as SolutionPad).
    readonly Gtk.TreeListModelCreateModelFunc createChildModel;
    readonly List<Gtk.GestureClick> rowGestures = new();
    Gtk.MultiSelection selection;
    string filterText = "";

    /// <summary>Raised when the user activates a test method (navigate to its source).</summary>
    public event Action<FilePath, int>? NavigateRequested;

    /// <summary>Raised by the context menu's Run entry with the node to run.</summary>
    public event Action<IReadOnlyList<TestNode>>? RunRequested;

    /// <summary>Raised by the context menu's Debug entry with the node to debug.</summary>
    public event Action<TestNode>? DebugRequested;

    /// <summary>Creates the pad and its (initially empty) tree.</summary>
    public TestsPad()
    {
        createChildModel = CreateChildModel;

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

            var rightClick = Gtk.GestureClick.New();
            rightClick.SetButton(3);
            rightClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
            rightClick.OnPressed += (_, _) =>
            {
                if (listItem.GetItem() is Gtk.TreeListRow row && row.GetItem() is TestTreeNode { Model: { } model })
                    ShowContextMenu(model, expander);
            };
            expander.AddController(rightClick);
            rowGestures.Add(rightClick);
        };
        factory.OnBind += (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            if (listItem.GetItem() is not Gtk.TreeListRow row || row.GetItem() is not TestTreeNode { Model: { } model })
                return;
            var expander = (Gtk.TreeExpander) listItem.GetChild()!;
            expander.SetListRow(row);
            var box = (Gtk.Box) expander.GetChild()!;
            var image = (Gtk.Image) box.GetFirstChild()!;
            var label = (Gtk.Label) image.GetNextSibling()!;
            image.SetFromPaintable(ImageService.GetIcon(TestTreeNode.IconNameFor(model.Status)));
            label.SetText(TitleFor(model));
        };

        // The model is created empty here and rebuilt by Reload().
        selection = Gtk.MultiSelection.New(CreateTreeModel());
        listView = Gtk.ListView.New(selection, factory);
        listView.OnActivate += (_, args) => OnRowActivated(args.Position);

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(listView);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);
        scrolled.AddCssClass("cb-sidebar");

        filterEntry = Gtk.SearchEntry.New();
        filterEntry.SetHexpand(true);
        filterEntry.SetTooltipText("Filter tests");
        filterEntry.OnSearchChanged += (_, _) =>
        {
            filterText = filterEntry.GetText() ?? "";
            Reload();
        };

        root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(BuildToolbar());
        root.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        root.Append(scrolled);
    }

    /// <summary>The widget to place in the workbench.</summary>
    public Gtk.Widget Widget => root;

    readonly List<(Gtk.Button Button, string IconName)> toolbarButtons = new();

    Gtk.Widget BuildToolbar()
    {
        var toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
        toolbar.AddCssClass("toolbar");
        toolbar.Append(ToolButton("run-unit-tests-16", "app.run-all-tests", "Run All Tests (Ctrl+T)"));
        toolbar.Append(ToolButton("execute-16", "app.run-selected-tests", "Run Selected Tests"));
        toolbar.Append(ToolButton("bug-16", "app.debug-selected-test", "Debug Selected Test"));
        toolbar.Append(ToolButton("stop-16", "app.stop", "Stop (Shift+F5)"));
        toolbar.Append(ToolButton("refresh-16", "app.rediscover-tests", "Rediscover Tests"));
        toolbar.Append(filterEntry);
        return toolbar;
    }

    Gtk.Button ToolButton(string iconName, string actionName, string tooltip)
    {
        var button = Gtk.Button.New();
        button.SetChild(ImageService.CreateImage(iconName));
        button.SetActionName(actionName);
        button.SetTooltipText(tooltip);
        button.SetHasFrame(false);
        toolbarButtons.Add((button, iconName));
        return button;
    }

    static string TitleFor(TestNode model)
    {
        if (model.Kind == TestNodeKind.Method && model.LastResult is { Status: TestStatus.Passed or TestStatus.Failed } result)
            return $"{model.Name} ({result.DurationSeconds * 1000:F0} ms)";
        return model.Name;
    }

    Gtk.TreeListModel CreateTreeModel()
    {
        var rootStore = Gio.ListStore.New(TestTreeNode.GetGType());
        foreach (var node in TestService.Roots)
        {
            if (MatchesFilter(node))
                rootStore.Append(TestTreeNode.Create(node));
        }
        // While filtering, every match auto-expands into view; the normal
        // tree opens collapsed to the project level.
        return Gtk.TreeListModel.New(rootStore, passthrough: false, autoexpand: filterText.Length > 0, createFunc: createChildModel);
    }

    /// <summary>Rebuilds the tree from the current <see cref="TestService.Roots"/>.</summary>
    public void Reload()
    {
        var treeModel = CreateTreeModel();
        selection = Gtk.MultiSelection.New(treeModel);
        listView.SetModel(selection);
        // Reveal each project's namespaces by default (single top-level
        // expansion, matching the Solution pad's opening behavior). Rows
        // shift as children insert, so expand back to front.
        if (filterText.Length == 0)
        {
            var projectCount = (int) treeModel.GetNItems();
            for (var i = projectCount - 1; i >= 0; i--)
                treeModel.GetRow((uint) i)?.SetExpanded(true);
        }
    }

    /// <summary>Empties the pad (the solution closed).</summary>
    public void Clear()
    {
        filterText = "";
        filterEntry.SetText("");
        Reload();
    }

    /// <summary>
    /// Rebinds the visible rows so status icons and durations refresh
    /// (called on run progress and after theme changes).
    /// </summary>
    public void RefreshStatuses()
    {
        listView.SetFactory(null);
        listView.SetFactory(factory);
    }

    /// <summary>Rebinds the visible rows for the current theme's icon variants.</summary>
    public void RefreshIcons()
    {
        foreach (var (button, iconName) in toolbarButtons)
            button.SetChild(ImageService.CreateImage(iconName));
        RefreshStatuses();
    }

    /// <summary>The test nodes of the currently selected rows (empty when nothing is selected).</summary>
    public IReadOnlyList<TestNode> SelectedNodes
    {
        get
        {
            var nodes = new List<TestNode>();
            var count = selection.GetNItems();
            for (uint i = 0; i < count; i++)
            {
                if (!selection.IsSelected(i))
                    continue;
                if (selection.GetObject(i) is Gtk.TreeListRow row && row.GetItem() is TestTreeNode { Model: { } model })
                    nodes.Add(model);
            }
            return nodes;
        }
    }

    void OnRowActivated(uint position)
    {
        var treeListModel = (Gtk.TreeListModel) selection.GetModel()!;
        if (treeListModel.GetRow(position) is not Gtk.TreeListRow row)
            return;
        if (row.GetItem() is not TestTreeNode { Model: { } model })
            return;
        if (model.Kind == TestNodeKind.Method && !model.SourceFile.IsNullOrEmpty)
            NavigateRequested?.Invoke(model.SourceFile, model.SourceLine);
        else
            row.SetExpanded(!row.GetExpanded());
    }

    void ShowContextMenu(TestNode model, Gtk.Widget anchor)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        var popover = Gtk.Popover.New();
        popover.SetChild(box);
        popover.SetParent(anchor);
        popover.OnClosed += (_, _) => popover.Unparent();

        var runLabel = model.Kind == TestNodeKind.Method ? "Run Test" : "Run Tests";
        AppendMenuButton(box, popover, runLabel, () => RunRequested?.Invoke(new[] { model }));
        var debugLabel = model.Kind == TestNodeKind.Method ? "Debug Test" : "Debug Tests";
        AppendMenuButton(box, popover, debugLabel, () => DebugRequested?.Invoke(model));
        if (model.Kind == TestNodeKind.Method && !model.SourceFile.IsNullOrEmpty)
            AppendMenuButton(box, popover, "Go to Test", () => NavigateRequested?.Invoke(model.SourceFile, model.SourceLine));
        popover.Popup();
    }

    static void AppendMenuButton(Gtk.Box box, Gtk.Popover popover, string label, Action action)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.SetHasFrame(false);
        if (button.GetChild() is Gtk.Label buttonLabel)
            buttonLabel.SetXalign(0);
        button.OnClicked += (_, _) =>
        {
            popover.Popdown();
            action();
        };
        box.Append(button);
    }

    // The subtree rooted at the node contains a test whose full name
    // matches the filter (case-insensitive substring).
    bool MatchesFilter(TestNode node)
    {
        if (filterText.Length == 0)
            return true;
        if (node.FullName.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            return true;
        return node.Children.Any(MatchesFilter);
    }

    Gio.ListModel? CreateChildModel(GObject.Object item)
    {
        if (item is not TestTreeNode { Model: { } model } || model.Children.Count == 0)
            return null;
        var children = Gio.ListStore.New(TestTreeNode.GetGType());
        foreach (var child in model.Children)
        {
            if (MatchesFilter(child))
                children.Append(TestTreeNode.Create(child));
        }
        return children;
    }
}
