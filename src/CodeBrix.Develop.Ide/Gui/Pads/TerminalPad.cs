//
// TerminalPad.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Ide.Gui.Terminal;
using CodeBrix.Develop.Ide.Remote;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Pads;

/// <summary>
/// The Terminal tab of the bottom pane: a placeholder until the first
/// Tools &gt; SSH to Device connection, then the interactive SSH terminal.
/// One session at a time — attaching a new session replaces (and disposes)
/// the previous one. When a session ends its output stays on screen, with
/// the reason appended, until the next connection clears it.
/// </summary>
public class TerminalPad
{
    readonly Gtk.Stack stack;
    readonly TerminalView view;
    readonly SynchronizationContext uiContext;
    SshTerminalSession? session;

    /// <summary>Creates the pad; <paramref name="uiContext"/> posts to the GTK main loop.</summary>
    public TerminalPad(SynchronizationContext uiContext)
    {
        this.uiContext = uiContext;

        var placeholder = Gtk.Label.New("No active SSH session — use Tools > SSH to Device… to connect.");
        placeholder.AddCssClass("dim-label");

        view = new TerminalView();
        view.InputEmitted += text => session?.Send(text);
        view.GridResized += (columns, rows) => session?.Resize(columns, rows);

        stack = Gtk.Stack.New();
        stack.AddNamed(placeholder, "placeholder");
        stack.AddNamed(view.Widget, "terminal");
        stack.SetVisibleChildName("placeholder");
    }

    /// <summary>The widget to place in the workbench.</summary>
    public Gtk.Widget Widget => stack;

    /// <summary>Raised on the UI thread with status-bar-worthy session news.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised on the UI thread when the active session disconnects.</summary>
    public event Action? SessionEnded;

    /// <summary>The terminal's current column count, for sizing a new PTY.</summary>
    public int Columns => view.Columns;

    /// <summary>The terminal's current row count, for sizing a new PTY.</summary>
    public int Rows => view.Rows;

    /// <summary>
    /// Takes ownership of a connected session: the previous session (if any)
    /// is disposed, the terminal is cleared, and the tab switches from the
    /// placeholder to the terminal. Must be called on the UI thread.
    /// </summary>
    public void AttachSession(SshTerminalSession newSession)
    {
        DisposeSession();
        session = newSession;
        view.Clear();

        newSession.DataReceived += data => uiContext.Post(_ =>
        {
            if (ReferenceEquals(session, newSession))
                view.Feed(data, data.Length);
        }, null);
        newSession.Disconnected += reason => uiContext.Post(_ =>
            OnDisconnected(newSession, reason), null);

        stack.SetVisibleChildName("terminal");
        // The widget was possibly never allocated before now; whatever grid
        // size it has is the PTY's until the first resize catches up.
        newSession.Resize(view.Columns, view.Rows);
        view.GrabFocus();
    }

    /// <summary>Disposes any active session without ceremony (application exit).</summary>
    public void Shutdown() => DisposeSession();

    /// <summary>Gives the terminal view keyboard focus (e.g. after a paste).</summary>
    public void FocusTerminal() => view.GrabFocus();

    void OnDisconnected(SshTerminalSession endedSession, string reason)
    {
        if (!ReferenceEquals(session, endedSession))
            return;
        session = null;
        // Session teardown blocks on the network; the UI thread only kicks
        // it off. The output stays readable on the dead terminal.
        _ = Task.Run(endedSession.Dispose);
        var notice = $"[SSH session ended — {reason}]";
        var bytes = Encoding.UTF8.GetBytes($"\r\n\x1b[0m{notice}\r\n");
        view.Feed(bytes, bytes.Length);
        StatusChanged?.Invoke($"SSH disconnected from {endedSession.DisplayName} — {reason}");
        SessionEnded?.Invoke();
    }

    void DisposeSession()
    {
        if (session is not { } old)
            return;
        session = null;
        _ = Task.Run(old.Dispose);
    }
}
