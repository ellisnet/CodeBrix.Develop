//
// TerminalView.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from the author's Lily.Shell TerminalView TerminalControl,
//      from a CodeBrix.Platform Skia canvas to a GTK 4 DrawingArea with
//      Cairo/Pango rendering)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Terminal.Engine;
using Cairo = CodeBrix.Develop.UI.Cairo;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gio = CodeBrix.Develop.UI.Gio;
using GLib = CodeBrix.Develop.UI.GLib;
using Gtk = CodeBrix.Develop.UI.Gtk;
using Pango = CodeBrix.Develop.UI.Pango;
using PangoCairo = CodeBrix.Develop.UI.PangoCairo;
using TerminalBuffer = CodeBrix.Terminal.Engine.Buffer; //Required: 'Buffer' alone is ambiguous with System.Buffer
using TerminalEngine = CodeBrix.Terminal.Engine.Terminal; //Required: 'Terminal' alone names this namespace

namespace CodeBrix.Develop.Ide.Gui.Terminal; //was previously: Lily.Shell.TerminalView;

/// <summary>
/// A terminal view: renders a CodeBrix.Terminal buffer as a fixed monospace
/// cell grid on a GTK 4 DrawingArea and turns keyboard input into VT byte
/// sequences. Wire <see cref="InputEmitted"/> to the shell's input and call
/// <see cref="Feed"/> (on the UI thread) with the shell's output — the view
/// is the screen and keyboard half of a terminal, the way a pty master
/// would see it. Mouse selection follows the terminal convention: drag or
/// double-click selects; right-click opens a Copy/Paste menu; Ctrl+Shift+C
/// copies, Ctrl+Shift+V pastes, Shift+PageUp/PageDown page through the
/// scrollback.
/// </summary>
public sealed class TerminalView
{
    const string FontName = "monospace 11";
    const uint BlinkIntervalMs = 500;
    const uint DragScrollIntervalMs = 90;
    const int DoubleClickMs = 400;

    static readonly TerminalColor DefaultForeground = new(0xff, 0xff, 0xff);
    static readonly TerminalColor DefaultBackground = new(0x00, 0x00, 0x00);

    readonly Gtk.DrawingArea area;
    readonly TerminalEngine terminal;
    readonly SelectionService selection;
    readonly Pango.FontDescription normalFont;
    readonly Pango.FontDescription boldFont;
    readonly Pango.FontDescription italicFont;
    readonly Pango.FontDescription boldItalicFont;
    readonly Gtk.PopoverMenu contextMenu;
    readonly Gio.SimpleAction copyAction;
    // The GLib timeout callbacks, held in fields so the delegates the native
    // side calls back into can never be collected while scheduled.
    readonly GLib.SourceFunc blinkTick;
    GLib.SourceFunc? dragScrollTick;

    CellMetrics? metrics;
    bool selecting;
    bool dragScrollRunning;
    (int Column, int Row) lastDragCell = (-1, -1);
    double dragStartX;
    double dragStartY;
    double lastPointerX;
    double lastPointerY;
    double scrollAccumulator;
    long lastClickTick;
    (int Column, int Row) lastClickCell = (-1, -1);
    bool followTail = true;
    bool blinkOn = true;
    bool focused;

    /// <summary>Creates the view with an 80x25 terminal that resizes to fit.</summary>
    public TerminalView()
    {
        area = Gtk.DrawingArea.New();
        area.SetHexpand(true);
        area.SetVexpand(true);
        area.SetFocusable(true);
        area.SetDrawFunc(OnDraw);
        area.OnResize += (_, _) => RecalculateGrid();

        terminal = new TerminalEngine(new ViewDelegate(this), new TerminalOptions
        {
            Cols = 80,
            Rows = 25,
            //The remote shell emits explicit CR+LF; double conversion would add blank rows
            ConvertEol = false,
        });

        selection = new SelectionService(terminal);
        selection.SelectionChanged += () =>
        {
            copyAction?.SetEnabled(selection.Active);
            area.QueueDraw();
        };

        normalFont = Pango.FontDescription.FromString(FontName);
        boldFont = Variant(normalFont, bold: true, italic: false);
        italicFont = Variant(normalFont, bold: false, italic: true);
        boldItalicFont = Variant(normalFont, bold: true, italic: true);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, args) => OnKeyPressed(args.Keyval, args.State);
        area.AddController(keys);

        var focus = Gtk.EventControllerFocus.New();
        focus.OnEnter += (_, _) => { focused = true; blinkOn = true; area.QueueDraw(); };
        focus.OnLeave += (_, _) => { focused = false; area.QueueDraw(); };
        area.AddController(focus);

        var wheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        wheel.OnScroll += (_, args) => OnWheel(args.Dy);
        area.AddController(wheel);

        var drag = Gtk.GestureDrag.New();
        drag.SetButton(1);
        drag.OnDragBegin += (_, args) => OnDragBegin(args.StartX, args.StartY);
        drag.OnDragUpdate += (_, args) => OnDragUpdate(args.OffsetX, args.OffsetY);
        drag.OnDragEnd += (_, _) => selecting = false;
        drag.OnCancel += (_, _) => selecting = false;
        area.AddController(drag);

        //Right-click opens the Copy/Paste menu. Copy is enabled only while a
        //selection exists; Paste quietly does nothing on an empty clipboard.
        copyAction = Gio.SimpleAction.New("copy", null);
        copyAction.SetEnabled(false);
        copyAction.OnActivate += (_, _) => CopySelection();
        var pasteAction = Gio.SimpleAction.New("paste", null);
        pasteAction.OnActivate += (_, _) => _ = PasteAsync();
        var menuActions = Gio.SimpleActionGroup.New();
        menuActions.AddAction(copyAction);
        menuActions.AddAction(pasteAction);
        area.InsertActionGroup("terminal", menuActions);

        var menuModel = Gio.Menu.New();
        menuModel.Append("_Copy", "terminal.copy");
        menuModel.Append("_Paste", "terminal.paste");
        contextMenu = Gtk.PopoverMenu.NewFromModel(menuModel);
        // Parented to the view once and never unparented: a popover torn down
        // in its own "closed" handler loses its action muxer before the
        // clicked item's action resolves (see SolutionPad).
        contextMenu.SetParent(area);

        var rightClick = Gtk.GestureClick.New();
        rightClick.SetButton(3);
        rightClick.OnPressed += (_, args) => ShowContextMenu(args.X, args.Y);
        area.AddController(rightClick);

        blinkTick = () =>
        {
            blinkOn = !blinkOn;
            area.QueueDraw();
            return true;
        };
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, BlinkIntervalMs, blinkTick);
    }

    /// <summary>The widget to place in the workbench.</summary>
    public Gtk.Widget Widget => area;

    /// <summary>
    /// Raised with VT-encoded input for the shell: keystrokes, pasted text,
    /// and the terminal's own status responses. Raised on the UI thread.
    /// </summary>
    public event Action<string>? InputEmitted;

    /// <summary>Raised with (columns, rows) when the widget size changes the grid.</summary>
    public event Action<int, int>? GridResized;

    /// <summary>The terminal's current column count.</summary>
    public int Columns => terminal.Cols;

    /// <summary>The terminal's current row count.</summary>
    public int Rows => terminal.Rows;

    /// <summary>Gives the view keyboard focus.</summary>
    public void GrabFocus() => area.GrabFocus();

    /// <summary>Feeds shell output into the terminal. UI thread only.</summary>
    public void Feed(byte[] data, int length)
    {
        if (length <= 0) { return; }
        terminal.Feed(data, length);
        if (followTail) { SnapToTail(); }
        area.QueueDraw();
    }

    /// <summary>Resets the terminal to its just-created state. UI thread only.</summary>
    public void Clear()
    {
        selection.SelectNone();
        //The engine's full reset is internal, reachable only as ESC c (RIS)
        terminal.Feed("\x1bc");
        followTail = true;
        SnapToTail();
        area.QueueDraw();
    }

    static Pango.FontDescription Variant(Pango.FontDescription baseFont, bool bold, bool italic)
    {
        var font = baseFont.Copy()
            ?? throw new InvalidOperationException("The terminal font description could not be copied");
        if (bold) { font.SetWeight(Pango.Weight.Bold); }
        if (italic) { font.SetStyle(Pango.Style.Italic); }
        return font;
    }

    void RaiseInput(string data)
    {
        //Typing snaps the view back to the live tail, like every terminal
        followTail = true;
        SnapToTail();
        blinkOn = true;
        InputEmitted?.Invoke(data);
        area.QueueDraw();
    }

    void SnapToTail()
    {
        var buffer = terminal.Buffer;
        buffer.YDisp = buffer.YBase;
    }

    void ScrollViewBy(int lines)
    {
        var buffer = terminal.Buffer;
        var target = Math.Clamp(buffer.YDisp - lines, 0, buffer.YBase);
        if (target == buffer.YDisp) { return; }

        buffer.YDisp = target;
        followTail = target == buffer.YBase;
        area.QueueDraw();
    }

    void RecalculateGrid()
    {
        var width = area.GetWidth();
        var height = area.GetHeight();
        if (width < 1 || height < 1) { return; }

        var cell = EnsureMetrics();
        var cols = Math.Max(4, (int) (width / cell.Width));
        var rows = Math.Max(2, (int) (height / cell.Height));

        if (cols != terminal.Cols || rows != terminal.Rows)
        {
            terminal.Resize(cols, rows);
            if (followTail) { SnapToTail(); }
            GridResized?.Invoke(cols, rows);
        }

        area.QueueDraw();
    }

    CellMetrics EnsureMetrics() =>
        metrics ??= CellMetrics.Measure(area.GetPangoContext(), normalFont);

    bool OnKeyPressed(uint keyval, Gdk.ModifierType state)
    {
        var control = state.HasFlag(Gdk.ModifierType.ControlMask);
        var shift = state.HasFlag(Gdk.ModifierType.ShiftMask);

        //Ctrl+Shift+C / Ctrl+Shift+V never reach the shell as input
        if (control && shift && (int) keyval is Gdk.Constants.KEY_C or Gdk.Constants.KEY_c)
        {
            CopySelection();
            return true;
        }
        if (control && shift && (int) keyval is Gdk.Constants.KEY_V or Gdk.Constants.KEY_v)
        {
            _ = PasteAsync();
            return true;
        }

        //Shift+PageUp/PageDown page through the scrollback
        if (shift && (int) keyval is Gdk.Constants.KEY_Page_Up or Gdk.Constants.KEY_Page_Down)
        {
            var page = Math.Max(1, terminal.Rows - 1);
            ScrollViewBy((int) keyval == Gdk.Constants.KEY_Page_Up ? page : -page);
            return true;
        }

        var encoded = TerminalKeyEncoder.Encode(keyval, state, terminal.ApplicationCursor);
        if (encoded == null) { return false; }

        RaiseInput(encoded);
        return true;
    }

    bool OnWheel(double dy)
    {
        //Fractional (smooth-scroll) deltas accumulate until they make a line
        scrollAccumulator += dy;
        var steps = (int) scrollAccumulator;
        if (steps != 0)
        {
            scrollAccumulator -= steps;
            ScrollViewBy(-steps * 3);
        }
        return true;
    }

    void OnDragBegin(double x, double y)
    {
        area.GrabFocus();
        dragStartX = x;
        dragStartY = y;
        var cell = SelectionGeometry.ToCell(x, y, EnsureMetrics(), terminal.Cols, terminal.Rows);
        var now = Environment.TickCount64;

        if (now - lastClickTick < DoubleClickMs && cell == lastClickCell)
        {
            //Double-click: word/expression selection. NOTE the engine's
            //  (col, row) parameter order - unlike its (row, col) siblings.
            selection.SelectWordOrExpression(cell.Column, cell.Row);
            lastClickTick = 0;
            selecting = false;
        }
        else
        {
            if (selection.Active) { selection.SelectNone(); }
            selection.SetSoftStart(cell.Row, cell.Column);
            selecting = true;
            lastDragCell = cell;
            lastClickTick = now;
            lastClickCell = cell;
        }
    }

    void OnDragUpdate(double offsetX, double offsetY)
    {
        if (!selecting) { return; }

        var x = dragStartX + offsetX;
        var y = dragStartY + offsetY;
        lastPointerX = x;
        lastPointerY = y;

        //Dragging beyond the top/bottom edge scrolls the view while held there
        if ((y < 0 || y > area.GetHeight()) && !dragScrollRunning)
        {
            StartDragScroll();
        }

        ExtendSelectionTo(x, y);
    }

    void ExtendSelectionTo(double x, double y)
    {
        var cell = SelectionGeometry.ToCell(x, y, EnsureMetrics(), terminal.Cols, terminal.Rows);
        if (cell == lastDragCell && selection.Active) { return; }

        if (!selection.Active) { selection.StartSelection(); }
        selection.DragExtend(cell.Row, cell.Column);
        lastDragCell = cell;
    }

    void StartDragScroll()
    {
        dragScrollRunning = true;
        dragScrollTick = () =>
        {
            if (!selecting || (lastPointerY >= 0 && lastPointerY <= area.GetHeight()))
            {
                dragScrollRunning = false;
                return false;
            }
            //Positive lines scroll back toward the scrollback top
            ScrollViewBy(lastPointerY < 0 ? 1 : -1);
            ExtendSelectionTo(lastPointerX, lastPointerY);
            return true;
        };
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, DragScrollIntervalMs, dragScrollTick);
    }

    void ShowContextMenu(double x, double y)
    {
        area.GrabFocus();
        copyAction.SetEnabled(selection.Active);
        contextMenu.SetPointingTo(new Gdk.Rectangle
        {
            X = (int) x,
            Y = (int) y,
            Width = 1,
            Height = 1,
        });
        contextMenu.Popup();
    }

    void CopySelection()
    {
        if (!selection.Active) { return; }

        var text = selection.GetSelectedText();
        if (!string.IsNullOrEmpty(text)) { area.GetClipboard().SetText(text); }
    }

    async Task PasteAsync()
    {
        try
        {
            var text = await area.GetClipboard().ReadTextAsync();
            if (string.IsNullOrEmpty(text)) { return; }

            //Terminal input newlines are carriage returns
            RaiseInput(text.Replace("\r\n", "\r").Replace('\n', '\r'));
        }
        catch (Exception ex)
        {
            LoggingService.LogInfo($"Terminal paste unavailable: {ex.Message}");
        }
    }

    void OnDraw(Gtk.DrawingArea drawingArea, Cairo.Context cr, int width, int height)
    {
        DefaultBackground.Apply(cr);
        cr.Paint();

        var cell = EnsureMetrics();
        var buffer = terminal.Buffer;
        var layout = PangoCairo.Functions.CreateLayout(cr);

        for (var row = 0; row < terminal.Rows; row++)
        {
            var lineIndex = buffer.YDisp + row;
            if (lineIndex >= buffer.Lines.Length) { break; }

            DrawLine(cr, layout, RunBuilder.BuildRuns(buffer.Lines[lineIndex]), row * cell.Height, cell);
        }

        if (selection.Active) { DrawSelection(cr, buffer, cell); }

        DrawCursor(cr, layout, buffer, cell);
    }

    void DrawLine(Cairo.Context cr, Pango.Layout layout, List<TextRunSegment> segments,
        double top, CellMetrics cell)
    {
        foreach (var segment in segments)
        {
            var style = AttributeDecoder.Decode(segment.Attribute, DefaultForeground, DefaultBackground);
            var left = segment.StartColumn * cell.Width;
            var width = segment.CellCount * cell.Width;

            if (style.HasVisibleBackground(DefaultBackground))
            {
                style.Background.Apply(cr);
                cr.Rectangle(left, top, width, cell.Height);
                cr.Fill();
            }

            if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                layout.SetFontDescription(PickFont(style));
                layout.SetText(segment.Text, -1);
                style.Foreground.Apply(cr);
                cr.MoveTo(left, top);
                PangoCairo.Functions.ShowLayout(cr, layout);
            }

            if (style.Underline || style.CrossedOut)
            {
                style.Foreground.Apply(cr);
                cr.LineWidth = 1;

                if (style.Underline)
                {
                    var y = top + cell.Baseline + 2;
                    cr.MoveTo(left, y);
                    cr.LineTo(left + width, y);
                    cr.Stroke();
                }

                if (style.CrossedOut)
                {
                    var y = top + cell.Height * 0.5;
                    cr.MoveTo(left, y);
                    cr.LineTo(left + width, y);
                    cr.Stroke();
                }
            }
        }
    }

    Pango.FontDescription PickFont(CellStyle style) => (style.Bold, style.Italic) switch
    {
        (true, true) => boldItalicFont,
        (true, false) => boldFont,
        (false, true) => italicFont,
        _ => normalFont,
    };

    void DrawSelection(Cairo.Context cr, TerminalBuffer buffer, CellMetrics cell)
    {
        var start = selection.Start;
        var end = selection.End;
        //The translucent overlay painted over selected cells
        cr.SetSourceRgba(0x4d / 255.0, 0x8b / 255.0, 0xd8 / 255.0, 0x66 / 255.0);

        for (var row = 0; row < terminal.Rows; row++)
        {
            if (SelectionGeometry.TryGetRowSpan(start.X, start.Y, end.X, end.Y,
                buffer.YDisp + row, terminal.Cols, out var first, out var last))
            {
                cr.Rectangle(first * cell.Width, row * cell.Height,
                    (last - first + 1) * cell.Width, cell.Height);
                cr.Fill();
            }
        }
    }

    void DrawCursor(Cairo.Context cr, Pango.Layout layout, TerminalBuffer buffer, CellMetrics cell)
    {
        if (terminal.CursorHidden) { return; }

        var screenRow = buffer.YBase + buffer.Y - buffer.YDisp;
        if (screenRow < 0 || screenRow >= terminal.Rows) { return; }

        var left = buffer.X * cell.Width;
        var top = screenRow * cell.Height;

        if (!focused)
        {
            //Steady hollow cursor while unfocused
            DefaultForeground.Apply(cr);
            cr.LineWidth = 1;
            cr.Rectangle(left + 0.5, top + 0.5, cell.Width - 1, cell.Height - 1);
            cr.Stroke();
            return;
        }

        if (!blinkOn) { return; }

        DefaultForeground.Apply(cr);
        cr.Rectangle(left, top, cell.Width, cell.Height);
        cr.Fill();

        //Repaint the character under the block in the background color
        var lineIndex = buffer.YBase + buffer.Y;
        if (lineIndex < buffer.Lines.Length && buffer.X < buffer.Lines[lineIndex].Length)
        {
            var text = RunBuilder.CellText(buffer.Lines[lineIndex][buffer.X]);
            if (!string.IsNullOrWhiteSpace(text))
            {
                layout.SetFontDescription(normalFont);
                layout.SetText(text, -1);
                DefaultBackground.Apply(cr);
                cr.MoveTo(left, top);
                PangoCairo.Functions.ShowLayout(cr, layout);
            }
        }
    }

    sealed class ViewDelegate : ITerminalDelegate
    {
        readonly TerminalView owner;

        public ViewDelegate(TerminalView owner) => this.owner = owner;

        public void ShowCursor(TerminalEngine source) => owner.area.QueueDraw();

        public void SetTerminalTitle(TerminalEngine source, string title)
        {
        }

        public void SetTerminalIconTitle(TerminalEngine source, string title)
        {
        }

        public void SizeChanged(TerminalEngine source)
        {
            //Escape-sequence-driven resize is not supported; the grid follows the widget size
        }

        public void Send(byte[] data) =>
            owner.InputEmitted?.Invoke(Encoding.UTF8.GetString(data));

        public string? WindowCommand(TerminalEngine source, WindowManipulationCommand command,
            params int[] args) => null;

        public bool IsProcessTrusted() => true;
    }
}
