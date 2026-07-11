//
// TestResultsPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.UnitTesting.TestResultsPad, rebuilt on GTK 4
//      for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Testing;
using CodeBrix.Develop.Ide.Themes;
using Gio = CodeBrix.Develop.UI.Gio;
using GObject = CodeBrix.Develop.UI.GObject;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>One row of the Test Results failure/skip list.</summary>
[GObject.Subclass<GObject.Object>]
public partial class TestResultRowNode
{
    /// <summary>The test method the row reports on.</summary>
    public TestNode? Model { get; set; }

    /// <summary>Creates a row for the given method node.</summary>
    public static TestResultRowNode Create(TestNode model)
    {
        var node = NewWithProperties(Array.Empty<GObject.ConstructArgument>());
        node.Model = model;
        return node;
    }
}

/// <summary>
/// The Test Results pad (bottom dock): a live counts strip with a progress
/// bar, and the run's failed/skipped tests with their messages and stack
/// traces. Activating a row navigates to the failing assertion (parsed from
/// the stack trace) or to the test's declaration.
/// </summary>
public class TestResultsPad
{
    readonly Gtk.Box root;
    readonly Gtk.ProgressBar progressBar;
    readonly Gtk.Label summaryLabel;
    readonly Gio.ListStore store;
    readonly Gtk.ListView listView;
    readonly Gtk.SignalListItemFactory factory;
    readonly Gtk.TextView detailView;
    readonly Gtk.TextBuffer detailBuffer;
    readonly Gtk.TextTag headerTag;
    readonly Gtk.TextTag stackTag;

    readonly Dictionary<TestNode, TestResultRowNode> rowsByMethod = new();
    IReadOnlyList<TestNode> targetMethods = Array.Empty<TestNode>();
    bool running;

    /// <summary>Raised when the user activates a result row (file + 1-based line).</summary>
    public event Action<FilePath, int>? NavigateRequested;

    /// <summary>Creates the (initially empty) pad.</summary>
    public TestResultsPad()
    {
        progressBar = Gtk.ProgressBar.New();
        progressBar.SetHexpand(false);
        progressBar.SetSizeRequest(160, -1);
        progressBar.SetValign(Gtk.Align.Center);

        summaryLabel = Gtk.Label.New("No tests have run yet.");
        summaryLabel.SetXalign(0);
        summaryLabel.SetHexpand(true);
        summaryLabel.SetEllipsize(CodeBrix.Develop.UI.Pango.EllipsizeMode.End);

        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        header.SetMarginStart(8);
        header.SetMarginEnd(8);
        header.SetMarginTop(4);
        header.SetMarginBottom(4);
        header.Append(progressBar);
        header.Append(summaryLabel);

        store = Gio.ListStore.New(TestResultRowNode.GetGType());
        var selection = Gtk.SingleSelection.New(store);
        selection.OnSelectionChanged += (_, _) => ShowDetail(SelectedMethod());
        factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += static (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            box.SetMarginStart(8);
            box.SetMarginTop(1);
            box.SetMarginBottom(1);
            var image = Gtk.Image.New();
            image.SetPixelSize(16);
            box.Append(image);
            var label = Gtk.Label.New(null);
            label.SetXalign(0);
            box.Append(label);
            listItem.SetChild(box);
        };
        factory.OnBind += static (_, args) =>
        {
            var listItem = (Gtk.ListItem) args.Object;
            if (listItem.GetItem() is not TestResultRowNode { Model: { } model })
                return;
            var box = (Gtk.Box) listItem.GetChild()!;
            var image = (Gtk.Image) box.GetFirstChild()!;
            var label = (Gtk.Label) image.GetNextSibling()!;
            image.SetFromPaintable(ImageService.GetIcon(TestTreeNode.IconNameFor(model.Status)));
            label.SetText(model.FullName);
        };
        listView = Gtk.ListView.New(selection, factory);
        listView.OnActivate += (_, args) =>
        {
            if (store.GetObject(args.Position) is TestResultRowNode { Model: { } model })
                NavigateToResult(model);
        };
        var listScrolled = Gtk.ScrolledWindow.New();
        listScrolled.SetChild(listView);
        listScrolled.SetHexpand(true);
        listScrolled.SetVexpand(true);

        detailView = Gtk.TextView.New();
        detailView.SetEditable(false);
        detailView.SetCursorVisible(false);
        detailView.SetMonospace(true);
        detailView.SetLeftMargin(6);
        detailView.SetTopMargin(4);
        detailBuffer = detailView.GetBuffer();
        var tagTable = detailBuffer.GetTagTable();
        headerTag = Gtk.TextTag.New(null);
        headerTag.Weight = 700;
        stackTag = Gtk.TextTag.New(null);
        tagTable.Add(headerTag);
        tagTable.Add(stackTag);
        var detailScrolled = Gtk.ScrolledWindow.New();
        detailScrolled.SetChild(detailView);
        detailScrolled.SetHexpand(true);
        detailScrolled.SetVexpand(true);

        var split = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        split.SetStartChild(listScrolled);
        split.SetEndChild(detailScrolled);
        split.SetResizeStartChild(true);
        split.SetShrinkStartChild(false);
        split.SetShrinkEndChild(false);
        split.SetPosition(420);

        root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.AddCssClass("cb-output");
        root.Append(header);
        root.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        root.Append(split);

        ApplyThemeColors();
        // The pad lives as long as the application; no unsubscribe needed.
        ThemeService.ThemeChanged += ApplyThemeColors;
    }

    /// <summary>The widget to place in the bottom pad area.</summary>
    public Gtk.Widget Widget => root;

    /// <summary>
    /// Resets the pad for a starting run targeting the given method nodes
    /// (progress reports against their count). Must be called on the UI thread.
    /// </summary>
    public void BeginRun(IReadOnlyList<TestNode> targets)
    {
        targetMethods = targets;
        running = true;
        rowsByMethod.Clear();
        store.RemoveAll();
        ClearDetail();
        progressBar.SetFraction(0);
        summaryLabel.SetText($"Running {targets.Count} test{(targets.Count == 1 ? "" : "s")}…");
    }

    /// <summary>
    /// Updates counts/progress after a method node's status changed, adding
    /// a failure/skip row when needed. Must be called on the UI thread.
    /// </summary>
    public void OnTestFinished(TestNode method)
    {
        if (method.Status is TestStatus.Failed or TestStatus.Skipped)
        {
            if (rowsByMethod.TryGetValue(method, out _))
            {
                // Row already present — statuses only refine (e.g. a theory's
                // later row flips a skip to a failure); rebind for the icon.
                listView.SetFactory(null);
                listView.SetFactory(factory);
            }
            else
            {
                var row = TestResultRowNode.Create(method);
                rowsByMethod[method] = row;
                store.Append(row);
            }
            // The first row auto-selects without a selection-changed signal,
            // and a selected row's result can refine after the fact (the MTP
            // report pass adds the message/stack) — keep the detail in sync.
            if (SelectedMethod() == method)
                ShowDetail(method);
        }
        if (!running)
            return;

        int done = 0, passed = 0, failed = 0, skipped = 0;
        foreach (var target in targetMethods)
        {
            switch (target.Status)
            {
                case TestStatus.Passed: done++; passed++; break;
                case TestStatus.Failed: done++; failed++; break;
                case TestStatus.Skipped: done++; skipped++; break;
            }
        }
        if (targetMethods.Count > 0)
            progressBar.SetFraction(Math.Min(1.0, (double) done / targetMethods.Count));
        summaryLabel.SetMarkup(
            $"{Colored(passed, "Passed", goodColor)}   {Colored(failed, "Failed", badColor)}   {Colored(skipped, "Skipped", warningColor)}   "
            + $"{done} of {targetMethods.Count}");
    }

    /// <summary>Shows the finished run's summary. Must be called on the UI thread.</summary>
    public void EndRun(TestRunSummary summary)
    {
        running = false;
        progressBar.SetFraction(1.0);
        if (summary.BuildFailed || summary.Error.Length > 0)
        {
            summaryLabel.SetMarkup($"<span foreground=\"{badColor}\">{Escape(summary.Error)}</span>");
            return;
        }
        if (summary.Cancelled)
        {
            summaryLabel.SetText("Test run cancelled.");
            return;
        }
        var elapsed = summary.Elapsed.TotalSeconds;
        summaryLabel.SetMarkup(
            $"{Colored(summary.Passed, "Passed", goodColor)}   {Colored(summary.Failed, "Failed", badColor)}   "
            + $"{Colored(summary.Skipped, "Skipped", warningColor)}   {summary.Total} total   {elapsed:F1}s");
    }

    /// <summary>Empties the pad (the solution closed). Must be called on the UI thread.</summary>
    public void Clear()
    {
        running = false;
        targetMethods = Array.Empty<TestNode>();
        rowsByMethod.Clear();
        store.RemoveAll();
        ClearDetail();
        progressBar.SetFraction(0);
        summaryLabel.SetText("No tests have run yet.");
    }

    TestNode? SelectedMethod()
    {
        var model = (Gtk.SingleSelection) listView.GetModel()!;
        return (model.GetSelectedItem() as TestResultRowNode)?.Model;
    }

    void ShowDetail(TestNode? method)
    {
        ClearDetail();
        if (method?.LastResult is not { } result)
            return;
        AppendDetail($"{method.FullName}", headerTag);
        if (result.DurationSeconds > 0)
            AppendDetail($"{result.DurationSeconds * 1000:F0} ms", stackTag);
        if (result.Message.Length > 0)
        {
            AppendDetail("", null);
            AppendDetail(result.Message, null);
        }
        if (result.StackTrace.Length > 0)
        {
            AppendDetail("", null);
            AppendDetail(result.StackTrace, stackTag);
        }
    }

    void AppendDetail(string text, Gtk.TextTag? tag)
    {
        detailBuffer.GetEndIter(out var end);
        var start = end.GetOffset();
        detailBuffer.Insert(end, text + "\n", -1);
        if (tag != null)
        {
            detailBuffer.GetIterAtOffset(out var from, start);
            detailBuffer.GetEndIter(out var to);
            detailBuffer.ApplyTag(tag, from, to);
        }
    }

    void ClearDetail()
    {
        detailBuffer.GetBounds(out var start, out var end);
        detailBuffer.Delete(start, end);
    }

    void NavigateToResult(TestNode method)
    {
        if (method.LastResult is { } result && result.TryGetFailureLocation(out var file, out var line))
        {
            NavigateRequested?.Invoke(file, line);
            return;
        }
        if (!method.SourceFile.IsNullOrEmpty)
            NavigateRequested?.Invoke(method.SourceFile, method.SourceLine);
    }

    string goodColor = "#388A34";
    string warningColor = "#BF8803";
    string badColor = "#E51400";

    static string Colored(int count, string label, string color)
        => $"<span foreground=\"{color}\">{count} {label}</span>";

    static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    void ApplyThemeColors()
    {
        var theme = ThemeService.CurrentDefinition;
        if (theme == null)
            return;
        var dark = theme.Info.IsDark;
        goodColor = theme.GetColor(dark ? "#89D185" : "#388A34",
            "testing.iconPassed", "terminal.ansiGreen", "charts.green");
        warningColor = theme.GetColor(dark ? "#CCA700" : "#BF8803",
            "testing.iconSkipped", "editorWarning.foreground", "list.warningForeground", "terminal.ansiYellow");
        badColor = theme.GetColor(dark ? "#F14C4C" : "#E51400",
            "testing.iconFailed", "editorError.foreground", "errorForeground", "list.errorForeground");
        stackTag.Foreground = theme.GetColor(dark ? "#9D9D9D" : "#717171",
            "descriptionForeground", "disabledForeground");
    }
}
