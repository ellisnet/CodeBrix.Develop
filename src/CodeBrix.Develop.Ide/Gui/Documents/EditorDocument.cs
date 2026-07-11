//
// EditorDocument.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Document + SourceEditorView, rebuilt
//      on GtkSourceView 5 for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Debugging;
using CodeBrix.Develop.Core.Testing;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Ide.Debugging;
using CodeBrix.Develop.Ide.Gui.Completion;
using CodeBrix.Develop.Ide.Gui.Pads;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;
using GtkSource = CodeBrix.Develop.UI.GtkSource;

namespace CodeBrix.Develop.Ide.Gui.Documents;

/// <summary>
/// An open source-file document: a GtkSourceView editor bound to a file on
/// disk, with syntax highlighting and Roslyn code completion for C#.
/// </summary>
public class EditorDocument
{
    const string BreakpointCategory = "codebrix-breakpoint";
    const string ExecutionCategory = "codebrix-execution";
    // One mark category per test status, so each gets its own gutter icon.
    const string TestCategoryPrefix = "codebrix-test-";
    static readonly TestStatus[] testMarkStatuses =
    {
        TestStatus.NotRun, TestStatus.Running, TestStatus.Passed,
        TestStatus.Failed, TestStatus.Skipped, TestStatus.Mixed,
    };

    readonly Gtk.ScrolledWindow scrolled;
    readonly GtkSource.View view;
    readonly GtkSource.Buffer buffer;
    readonly CompletionController completion;
    readonly EditorAnnotations annotations;
    // Controllers marshalled to native code: keep strong references.
    readonly Gtk.GestureClick gutterClick;
    readonly Gtk.EventControllerMotion hoverMotion;
    readonly Gtk.EventControllerScroll wheelScroll;
    readonly Gtk.ShortcutController completionKeys;
    readonly Action<FilePath> breakpointsChangedHandler;
    readonly Action testsChangedHandler;
    readonly Action<TestNode> testFinishedHandler;
    Gtk.Popover? testPopover;
    (TestNode Test, int Line, int AnchorY)? pendingTestPopover;
    readonly Gtk.Popover hoverPopover;
    readonly Gtk.Label hoverLabel;
    readonly Gtk.EventControllerMotion popoverMotion;
    readonly System.Threading.SynchronizationContext? uiContext;
    string? hoverExpression;
    string? hoverDiagnostic;
    bool pointerInPopover;

    /// <summary>The file shown in this document.</summary>
    public FilePath FileName { get; }

    /// <summary>Raised when the buffer's modified state flips.</summary>
    public event Action? ModifiedChanged;

    /// <summary>Whether the buffer has unsaved changes.</summary>
    public bool IsModified => buffer.GetModified();

    /// <summary>Whether this document contains C# source (and gets Roslyn services).</summary>
    public bool IsCSharp => FileName.HasExtension(".cs");

    /// <summary>Whether this document contains XAML (and gets XAML intelligence).</summary>
    public bool IsXaml => FileName.HasExtension(".xaml") || FileName.HasExtension(".axaml");

    /// <summary>The widget to place in the document area.</summary>
    public Gtk.Widget Widget => scrolled;

    /// <summary>Loads the given file into a new editor document.</summary>
    public EditorDocument(FilePath fileName)
    {
        FileName = fileName.FullPath;
        uiContext = System.Threading.SynchronizationContext.Current;

        buffer = GtkSource.Buffer.New(null);
        ApplyLanguage();
        ApplyStyleScheme();

        buffer.SetText(File.ReadAllText(FileName), -1);
        buffer.SetModified(false);
        buffer.OnModifiedChanged += (_, _) => ModifiedChanged?.Invoke();

        view = GtkSource.View.NewWithBuffer(buffer);
        view.SetMonospace(true);
        view.SetShowLineNumbers(true);
        view.SetHighlightCurrentLine(true);
        view.SetAutoIndent(true);
        view.SetIndentWidth(4);
        view.SetTabWidth(4);
        view.SetInsertSpacesInsteadOfTabs(true);
        view.SetLeftMargin(4);

        completion = new CompletionController(
            view, FileName, IsCSharp, IsXaml,
            GetText, GetCaretUtf16Offset, GetCaretRectangle, CommitCompletionItem);
        annotations = new EditorAnnotations(buffer, FileName, IsCSharp, IsXaml, GetText);

        // Completion-as-you-type: the buffer signals feed the controller
        // (insert/delete arrive before the change lands, changed after) and
        // the annotation layer refreshes on a debounce.
        buffer.OnInsertText += (_, args) => completion.NotifyInsert(args.Text);
        buffer.OnDeleteRange += (_, _) => completion.NotifyDelete();
        buffer.OnMarkSet += (_, args) =>
        {
            if (args.Mark.GetName() == "insert")
                completion.NotifyCaretMoved();
        };
        buffer.OnChanged += (_, _) =>
        {
            completion.ProcessEdit();
            annotations.ScheduleRefresh();
        };

        // While the completion list is open, navigation/commit keys belong
        // to it. A capture-phase shortcut controller consumes them only
        // when the controller reports the key as handled (gir.core signal
        // handlers cannot return "handled", shortcuts can).
        completionKeys = Gtk.ShortcutController.New();
        completionKeys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        foreach (var key in new[] { "Up", "Down", "Page_Up", "Page_Down", "Return", "KP_Enter", "Tab", "Escape" })
        {
            var keyName = key;
            completionKeys.AddShortcut(Gtk.Shortcut.New(
                Gtk.ShortcutTrigger.ParseString(keyName),
                Gtk.CallbackAction.New((_, _) => completion.HandleKey(keyName))));
        }
        view.AddController(completionKeys);

        // Wheel scrolling moves the text under the popups; dismiss them.
        wheelScroll = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.BothAxes);
        wheelScroll.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        wheelScroll.OnScroll += (_, _) =>
        {
            completion.DismissAll();
            return false; // observe only; scrolling proceeds normally
        };
        view.AddController(wheelScroll);

        // Debugging: breakpoint + execution marks in the gutter, click on a
        // line number to toggle a breakpoint, hover a variable for its value
        // while paused.
        view.SetShowLineMarks(true);
        var breakpointAttributes = GtkSource.MarkAttributes.New();
        if (ImageService.GetPixbuf("gutter-breakpoint-15") is { } breakpointPixbuf)
            breakpointAttributes.SetPixbuf(breakpointPixbuf);
        view.SetMarkAttributes(BreakpointCategory, breakpointAttributes, 10);

        var executionAttributes = GtkSource.MarkAttributes.New();
        if (ImageService.GetPixbuf("gutter-execution-15") is { } executionPixbuf)
            executionAttributes.SetPixbuf(executionPixbuf);
        var executionBackground = new Gdk.RGBA();
        if (executionBackground.Parse("rgba(255, 220, 0, 0.22)"))
            executionAttributes.SetBackground(executionBackground);
        view.SetMarkAttributes(ExecutionCategory, executionAttributes, 20);

        // Test-status marks (one category per status), below breakpoints in
        // priority so a breakpoint icon wins on a shared line.
        foreach (var status in testMarkStatuses)
        {
            var testAttributes = GtkSource.MarkAttributes.New();
            if (ImageService.GetPixbuf(TestTreeNode.IconNameFor(status)) is { } testPixbuf)
                testAttributes.SetPixbuf(testPixbuf);
            view.SetMarkAttributes(TestCategoryPrefix + status, testAttributes, 5);
        }

        // Attached to the view (not the gutter widget — its internal
        // renderers swallow presses) in the capture phase; a press whose x
        // falls inside the gutter's width toggles the breakpoint.
        gutterClick = Gtk.GestureClick.New();
        gutterClick.SetButton(1);
        gutterClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        gutterClick.OnPressed += (_, args) => OnViewPressed(args.X, args.Y);
        // The test Run/Debug popover opens on RELEASE: a popover popped up
        // during the press is torn down again when the release breaks into
        // its freshly acquired grab.
        gutterClick.OnReleased += (_, _) =>
        {
            if (pendingTestPopover is { } pending)
            {
                pendingTestPopover = null;
                ShowTestPopover(pending.Test, pending.Line, pending.AnchorY);
            }
        };
        view.AddController(gutterClick);

        hoverMotion = Gtk.EventControllerMotion.New();
        hoverMotion.OnMotion += (_, args) => OnPointerMotion(args.X, args.Y);
        // Leaving the view often means entering the value popover (to select
        // or copy the value) — hide only if the pointer is not in it shortly.
        hoverMotion.OnLeave += (_, _) => _ = HideHoverAfterDelayAsync();
        view.AddController(hoverMotion);

        // Built up front like the completion popup — parenting a popover
        // lazily from an async continuation does not render reliably.
        hoverLabel = Gtk.Label.New(null);
        hoverLabel.SetSelectable(true);
        hoverLabel.SetWrap(true);
        hoverLabel.SetMaxWidthChars(100);
        hoverLabel.SetXalign(0);
        var copyButton = Gtk.Button.NewWithLabel("Copy");
        copyButton.SetHalign(Gtk.Align.End);
        copyButton.OnClicked += (_, _) => view.GetClipboard().SetText(hoverLabel.GetText());
        var hoverBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        hoverBox.SetMarginStart(8);
        hoverBox.SetMarginEnd(8);
        hoverBox.SetMarginTop(6);
        hoverBox.SetMarginBottom(6);
        hoverBox.Append(hoverLabel);
        hoverBox.Append(copyButton);
        hoverPopover = Gtk.Popover.New();
        hoverPopover.SetChild(hoverBox);
        hoverPopover.SetAutohide(false); // never steal focus from the editor
        hoverPopover.SetParent(view);
        popoverMotion = Gtk.EventControllerMotion.New();
        popoverMotion.OnEnter += (_, _) => pointerInPopover = true;
        popoverMotion.OnLeave += (_, _) =>
        {
            pointerInPopover = false;
            HideHoverPopover();
        };
        hoverBox.AddController(popoverMotion);

        breakpointsChangedHandler = OnBreakpointsChanged;
        DebugService.Breakpoints.Changed += breakpointsChangedHandler;
        ApplyBreakpointMarks();

        // Test-status gutter marks follow the test tree; the service raises
        // its events on background threads.
        testsChangedHandler = () => uiContext?.Post(_ => ApplyTestMarks(), null);
        testFinishedHandler = node =>
        {
            if (node.SourceFile == FileName)
                uiContext?.Post(_ => ApplyTestMarks(), null);
        };
        TestService.TestsChanged += testsChangedHandler;
        TestService.TestFinished += testFinishedHandler;
        TestService.RunStarted += testsChangedHandler;
        ApplyTestMarks();

        scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(view);
        scrolled.SetHexpand(true);
        scrolled.SetVexpand(true);

        // First semantic/diagnostic pass for the freshly loaded file (it
        // waits quietly until the type system is ready).
        annotations.ScheduleRefresh();
    }

    /// <summary>Detaches shared-event subscriptions; call when the document closes.</summary>
    public void OnClosed()
    {
        DebugService.Breakpoints.Changed -= breakpointsChangedHandler;
        TestService.TestsChanged -= testsChangedHandler;
        TestService.TestFinished -= testFinishedHandler;
        TestService.RunStarted -= testsChangedHandler;
    }

    /// <summary>The caret's 1-based line.</summary>
    public int GetCaretLine()
    {
        buffer.GetIterAtMark(out var caret, buffer.GetInsert());
        return caret.GetLine() + 1;
    }

    /// <summary>Toggles a breakpoint on the caret's line (the F9 command).</summary>
    public void ToggleBreakpointAtCaret()
    {
        buffer.GetIterAtMark(out var caret, buffer.GetInsert());
        ToggleBreakpoint(caret.GetLine() + 1);
    }

    void OnViewPressed(double x, double y)
    {
        // Only presses on the gutter (line numbers + marks) are handled here.
        var gutterWidth = view.GetGutter(Gtk.TextWindowType.Left).GetWidth();
        if (x < 0 || x >= gutterWidth)
            return;
        view.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int) x, (int) y, out _, out var bufferY);
        view.GetLineAtY(out var iter, bufferY, out _);
        var line = iter.GetLine() + 1; // TextIter lines are 0-based

        // A line holding a test's gutter marker offers Run/Debug (plus the
        // breakpoint toggle); any other gutter line toggles the breakpoint.
        var test = TestService.GetTestsInFile(FileName).FirstOrDefault(t => t.SourceLine == line);
        if (test != null)
            pendingTestPopover = (test, line, (int) y);
        else
            ToggleBreakpoint(line);
    }

    void ShowTestPopover(TestNode test, int line, int anchorY)
    {
        testPopover?.Popdown();
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        var popover = Gtk.Popover.New();
        popover.SetChild(box);
        popover.SetParent(view);
        popover.OnClosed += (_, _) => popover.Unparent();
        AppendPopoverButton(box, popover, "Run Test", () => IdeApp.Workbench?.RunTests(new[] { test }));
        AppendPopoverButton(box, popover, "Debug Test", () => IdeApp.Workbench?.DebugTest(test));
        AppendPopoverButton(box, popover, "Toggle Breakpoint", () => ToggleBreakpoint(line));
        popover.SetPointingTo(new Gdk.Rectangle { X = 0, Y = anchorY, Width = 1, Height = 1 });
        testPopover = popover;
        popover.Popup();
    }

    static void AppendPopoverButton(Gtk.Box box, Gtk.Popover popover, string label, Action action)
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

    // Replaces the test-status gutter marks from the current test tree.
    void ApplyTestMarks()
    {
        buffer.GetBounds(out var start, out var end);
        foreach (var status in testMarkStatuses)
            buffer.RemoveSourceMarks(start, end, TestCategoryPrefix + status);
        var lineCount = buffer.GetLineCount();
        foreach (var test in TestService.GetTestsInFile(FileName))
        {
            if (test.SourceLine < 1 || test.SourceLine > lineCount)
                continue;
            buffer.GetIterAtLine(out var iter, test.SourceLine - 1);
            buffer.CreateSourceMark(null, TestCategoryPrefix + test.Status, iter);
        }
    }

    void ToggleBreakpoint(int line)
    {
        // Removing an existing breakpoint is always allowed; setting a new
        // one requires the line to hold breakable code — a breakpoint on a
        // blank or comment-only line would never be hit anyway.
        if (!DebugService.Breakpoints.IsSet(FileName, line)
            && !TypeSystemService.IsBreakableLine(FileName, GetText(), line))
        {
            IdeApp.Workbench?.ShowStatus($"Line {line} has no executable code — breakpoint not set");
            return;
        }
        DebugService.Breakpoints.Toggle(FileName, line);
    }

    void OnBreakpointsChanged(FilePath file)
    {
        if (file == FileName)
            ApplyBreakpointMarks();
    }

    void ApplyBreakpointMarks()
    {
        buffer.GetBounds(out var start, out var end);
        buffer.RemoveSourceMarks(start, end, BreakpointCategory);
        var lineCount = buffer.GetLineCount();
        foreach (var line in DebugService.Breakpoints.GetLines(FileName))
        {
            if (line < 1 || line > lineCount)
                continue;
            buffer.GetIterAtLine(out var iter, line - 1);
            buffer.CreateSourceMark(null, BreakpointCategory, iter);
        }
    }

    /// <summary>
    /// Marks the given 1-based line as the paused execution location:
    /// gutter arrow, line background, cursor placed, and scrolled into view.
    /// </summary>
    public void ShowExecutionLine(int line)
    {
        ClearExecutionLine();
        if (line < 1 || line > buffer.GetLineCount())
            return;
        buffer.GetIterAtLine(out var iter, line - 1);
        buffer.CreateSourceMark(null, ExecutionCategory, iter);
        ScrollToLine(line);
    }

    /// <summary>Places the cursor on the 1-based line and scrolls it into view.</summary>
    public void ScrollToLine(int line)
    {
        if (line < 1 || line > buffer.GetLineCount())
            return;
        buffer.GetIterAtLine(out var iter, line - 1);
        buffer.PlaceCursor(iter);
        view.ScrollToIter(iter, withinMargin: 0.15, useAlign: false, xalign: 0, yalign: 0);
    }

    /// <summary>Removes the execution-location mark (the debuggee resumed).</summary>
    public void ClearExecutionLine()
    {
        buffer.GetBounds(out var start, out var end);
        buffer.RemoveSourceMarks(start, end, ExecutionCategory);
        HideHoverPopover();
    }

    void OnPointerMotion(double x, double y)
    {
        if (!IsCSharp || !DebugService.IsPaused)
        {
            ShowDiagnosticHover(x, y);
            return;
        }

        view.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int) x, (int) y, out var bufferX, out var bufferY);
        view.GetIterAtLocation(out var iter, bufferX, bufferY);
        var lineStart = iter.Copy();
        lineStart.SetLineOffset(0);
        var lineEnd = iter.Copy();
        if (!lineEnd.EndsLine())
            lineEnd.ForwardToLineEnd();
        var lineText = buffer.GetText(lineStart, lineEnd, includeHiddenChars: true);
        var expression = HoverExpression.At(lineText, iter.GetLineOffset());

        if (expression == hoverExpression)
            return;
        hoverExpression = expression;
        if (expression == null)
        {
            HideHoverPopover();
            return;
        }

        // Anchor the value bubble to the hovered line's rectangle, NOT the
        // raw pointer position: a bubble opening under the pointer makes the
        // pointer "leave" the view, which would immediately hide it again.
        view.GetIterLocation(iter, out var location);
        view.BufferToWindowCoords(Gtk.TextWindowType.Widget, location.X, location.Y, out var anchorX, out var anchorY);
        _ = ShowHoverValueAsync(expression, anchorX, anchorY, location.Height);
    }

    async Task ShowHoverValueAsync(string expression, int anchorX, int anchorY, int lineHeight)
    {
        try
        {
            var result = await DebugService.EvaluateAsync(expression);
            if (result == null)
                return;
            // GTK work must happen on the main loop; the await above may
            // have resumed on the debugger's read thread.
            (uiContext ?? throw new InvalidOperationException("No UI context")).Post(_ =>
            {
                // The pointer may have moved on (or the debuggee resumed) meanwhile.
                if (expression != hoverExpression || !DebugService.IsPaused)
                    return;
                hoverLabel.SetText(result.Success ? $"{expression} = {result.Text}" : result.Text);
                var rect = new Gdk.Rectangle { X = anchorX, Y = anchorY, Width = 1, Height = lineHeight };
                hoverPopover.SetPointingTo(rect);
                hoverPopover.Popup();
            }, null);
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Hover evaluation of '{expression}' failed", ex);
        }
    }

    async Task HideHoverAfterDelayAsync()
    {
        await Task.Delay(300);
        uiContext?.Post(_ =>
        {
            if (!pointerInPopover)
                HideHoverPopover();
        }, null);
    }

    // Hovering a squiggled span shows its diagnostic message in the same
    // bubble the debugger uses for values.
    void ShowDiagnosticHover(double x, double y)
    {
        if (!annotations.IsActive)
        {
            HideHoverPopover();
            return;
        }
        view.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int) x, (int) y, out var bufferX, out var bufferY);
        view.GetIterAtLocation(out var iter, bufferX, bufferY);
        var message = annotations.GetDiagnosticMessageAt(iter.GetOffset());
        if (message == hoverDiagnostic)
            return;
        hoverDiagnostic = message;
        if (message == null)
        {
            HideHoverPopover();
            return;
        }
        view.GetIterLocation(iter, out var location);
        view.BufferToWindowCoords(Gtk.TextWindowType.Widget, location.X, location.Y, out var anchorX, out var anchorY);
        hoverLabel.SetText(message);
        var rect = new Gdk.Rectangle { X = anchorX, Y = anchorY, Width = 1, Height = location.Height };
        hoverPopover.SetPointingTo(rect);
        hoverPopover.Popup();
    }

    void HideHoverPopover()
    {
        hoverExpression = null;
        hoverDiagnostic = null;
        hoverPopover?.Popdown();
    }

    // XML-based file types GtkSourceView's stock globs don't recognize;
    // highlighted with its xml language definition.
    static readonly string[] xmlExtensions =
    {
        ".xaml", ".axaml", ".csproj", ".fsproj", ".vbproj", ".shproj",
        ".projitems", ".props", ".targets", ".slnx", ".nuspec", ".resx", ".config",
    };

    void ApplyLanguage()
    {
        var manager = GtkSource.LanguageManager.GetDefault();
        var language = manager.GuessLanguage(FileName.FileName, null);
        if (language == null && Array.Exists(xmlExtensions, FileName.HasExtension))
            language = manager.GetLanguage("xml");
        if (language != null)
            buffer.SetLanguage(language);
    }

    void ApplyStyleScheme()
    {
        // The current color theme's generated scheme, with the stock
        // Adwaita schemes as a safety net.
        var scheme = Themes.ThemeService.GetEditorScheme()
            ?? GtkSource.StyleSchemeManager.GetDefault().GetScheme(WorkbenchTheme.PrefersDark ? "Adwaita-dark" : "Adwaita");
        if (scheme != null)
            buffer.SetStyleScheme(scheme);
    }

    /// <summary>Re-applies the current color theme's editor scheme and annotation colors.</summary>
    public void RefreshStyleScheme()
    {
        ApplyStyleScheme();
        annotations.OnThemeChanged();
    }

    /// <summary>Gives keyboard focus to the editor.</summary>
    public void Focus() => view.GrabFocus();

    /// <summary>The full buffer text.</summary>
    public string GetText()
    {
        buffer.GetBounds(out var start, out var end);
        return buffer.GetText(start, end, includeHiddenChars: true);
    }

    /// <summary>Writes the buffer to disk if modified.</summary>
    public void Save()
    {
        if (!IsModified)
            return;
        File.WriteAllText(FileName, GetText());
        buffer.SetModified(false);
    }

    /// <summary>
    /// Replaces the buffer with the file's current on-disk content, e.g.
    /// after an external tool rewrote it. Only call on unmodified documents
    /// — any in-editor changes are discarded.
    /// </summary>
    public void ReloadFromDisk()
    {
        buffer.SetText(File.ReadAllText(FileName), -1);
        buffer.SetModified(false);
    }

    /// <summary>
    /// Explicitly requests code completion at the caret (Ctrl+Space); the
    /// as-you-type path runs through the controller's buffer signals.
    /// </summary>
    public void ShowCompletion() => completion.Invoke();

    // The caret's offset in UTF-16 code units (what Roslyn and the XAML
    // services count in); a C# string's Length is UTF-16 too.
    int GetCaretUtf16Offset()
    {
        buffer.GetIterAtMark(out var caret, buffer.GetInsert());
        buffer.GetBounds(out var start, out _);
        return buffer.GetText(start, caret, includeHiddenChars: true).Length;
    }

    Gdk.Rectangle GetCaretRectangle()
    {
        buffer.GetIterAtMark(out var caret, buffer.GetInsert());
        view.GetIterLocation(caret, out var location);
        view.BufferToWindowCoords(Gtk.TextWindowType.Widget, location.X, location.Y, out var caretX, out var caretY);
        return new Gdk.Rectangle { X = caretX, Y = caretY, Width = 1, Height = location.Height };
    }

    void CommitCompletionItem(CodeCompletionItem item)
    {
        var text = GetText();
        // The item's span was computed against an earlier snapshot; the user
        // may have typed more of the word since. Replace through the caret.
        var endUtf16 = Math.Max(item.ReplacementStart + item.ReplacementLength, GetCaretUtf16Offset());
        var startChar = CharOffsetFromUtf16(text, item.ReplacementStart);
        var endChar = CharOffsetFromUtf16(text, endUtf16);

        buffer.GetIterAtOffset(out var replaceStart, startChar);
        buffer.GetIterAtOffset(out var replaceEnd, endChar);
        buffer.SelectRange(replaceStart, replaceEnd);
        buffer.DeleteSelection(interactive: false, defaultEditable: true);
        buffer.InsertAtCursor(item.InsertionText, -1);
        if (item.CaretBack > 0)
        {
            buffer.GetIterAtMark(out var caret, buffer.GetInsert());
            caret.BackwardChars(item.CaretBack);
            buffer.PlaceCursor(caret);
        }
        view.GrabFocus();
    }

    // GTK buffer offsets count Unicode code points, while Roslyn spans count
    // UTF-16 code units; surrogate pairs make the two diverge.
    static int CharOffsetFromUtf16(string text, int utf16Offset)
    {
        var charOffset = 0;
        for (var i = 0; i < utf16Offset && i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
                charOffset++;
        }
        return charOffset;
    }
}
