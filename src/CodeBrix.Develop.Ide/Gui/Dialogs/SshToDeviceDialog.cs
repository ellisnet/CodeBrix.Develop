//
// SshToDeviceDialog.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Remote;
using CodeBrix.Develop.Ide.Remote;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Dialogs;

/// <summary>
/// The Tools &gt; SSH to Device dialog: hostname/address (optionally
/// "host:port", default port 22), user, password (masked), and the
/// "Use as FrameBuffer Device" choice. All four values are remembered in
/// options.sqlite the moment Connect is clicked — success or not — and
/// prefill the dialog next time. Connect opens the SSH session and hands it
/// to <see cref="Connected"/>; a failure is reported in the dialog, which
/// stays open for another try.
/// </summary>
public class SshToDeviceDialog
{
    readonly Gtk.Window window;
    readonly Gtk.Entry hostEntry;
    readonly Gtk.Entry userEntry;
    readonly Gtk.PasswordEntry passwordEntry;
    readonly Gtk.CheckButton frameBufferCheck;
    readonly Gtk.CheckButton debuggingCheck;
    readonly Gtk.CheckButton orientationCheck;
    readonly Gtk.Label hostErrorLabel;
    readonly Gtk.Label errorLabel;
    readonly Gtk.Label progressLabel;
    readonly Gtk.Button connectButton;
    readonly Gtk.Button cancelButton;
    readonly int terminalColumns;
    readonly int terminalRows;
    bool connecting;

    /// <summary>
    /// Raised with the connected session after the dialog closes itself.
    /// The receiver takes ownership of the session.
    /// </summary>
    public event Action<SshTerminalSession>? Connected;

    /// <summary>
    /// Creates the dialog over the given parent window. The terminal grid
    /// size is what the new session's PTY is opened at.
    /// </summary>
    public SshToDeviceDialog(Gtk.Window parent, int terminalColumns, int terminalRows)
    {
        this.terminalColumns = terminalColumns;
        this.terminalRows = terminalRows;

        window = Gtk.Window.New();
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetTitle("SSH to Device");
        window.SetDefaultSize(460, -1);

        var hostLabel = Gtk.Label.New("Hostname/address:");
        hostLabel.SetXalign(0);
        hostEntry = Gtk.Entry.New();
        hostEntry.SetHexpand(true);
        hostEntry.SetText(IdePreferences.SshDeviceHost.Value);
        WatchText(hostEntry);
        var hostHint = Gtk.Label.New("A name or address, with an optional port: device.local, 10.0.0.9:2222.");
        hostHint.SetXalign(0);
        hostHint.AddCssClass("dim-label");
        hostErrorLabel = Gtk.Label.New(null);
        hostErrorLabel.SetXalign(0);
        hostErrorLabel.SetWrap(true);
        hostErrorLabel.AddCssClass("cb-error");
        hostErrorLabel.SetVisible(false);

        var userLabel = Gtk.Label.New("User:");
        userLabel.SetXalign(0);
        userEntry = Gtk.Entry.New();
        userEntry.SetHexpand(true);
        userEntry.SetText(IdePreferences.SshDeviceUser.Value);
        WatchText(userEntry);

        var passwordLabel = Gtk.Label.New("Password:");
        passwordLabel.SetXalign(0);
        passwordEntry = Gtk.PasswordEntry.New();
        passwordEntry.SetHexpand(true);
        passwordEntry.SetShowPeekIcon(true);
        passwordEntry.SetText(IdePreferences.SshDevicePassword.Value);

        frameBufferCheck = Gtk.CheckButton.NewWithLabel("Use as FrameBuffer Device");
        frameBufferCheck.SetActive(IdePreferences.SshDeviceUseAsFrameBuffer.Value);

        // "Enable for SSH Debugging" and "Enable Orientation Changes" sit under,
        // and depend on, the FrameBuffer choice: only selectable while that box
        // is checked, and forced off when it is not.
        debuggingCheck = Gtk.CheckButton.NewWithLabel("Enable for SSH Debugging");
        debuggingCheck.SetMarginStart(24);
        debuggingCheck.SetSensitive(frameBufferCheck.GetActive());
        debuggingCheck.SetActive(frameBufferCheck.GetActive() && IdePreferences.SshDeviceEnableDebugging.Value);
        orientationCheck = Gtk.CheckButton.NewWithLabel("Enable Orientation Changes");
        orientationCheck.SetMarginStart(24);
        orientationCheck.SetSensitive(frameBufferCheck.GetActive());
        orientationCheck.SetActive(frameBufferCheck.GetActive() && IdePreferences.SshDeviceEnableOrientation.Value);
        frameBufferCheck.OnToggled += (_, _) =>
        {
            var on = frameBufferCheck.GetActive();
            debuggingCheck.SetSensitive(on);
            orientationCheck.SetSensitive(on);
            if (!on)
            {
                debuggingCheck.SetActive(false);
                orientationCheck.SetActive(false);
            }
        };

        errorLabel = Gtk.Label.New(null);
        errorLabel.SetXalign(0);
        errorLabel.SetWrap(true);
        errorLabel.AddCssClass("cb-error");
        errorLabel.SetVisible(false);

        progressLabel = Gtk.Label.New(null);
        progressLabel.SetXalign(0);

        cancelButton = Gtk.Button.NewWithLabel("Cancel");
        cancelButton.OnClicked += (_, _) => window.Close();
        connectButton = Gtk.Button.NewWithLabel("Connect");
        connectButton.AddCssClass("suggested-action");
        connectButton.OnClicked += (_, _) => _ = ConnectAsync();
        var buttonRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttonRow.SetHalign(Gtk.Align.End);
        buttonRow.Append(cancelButton);
        buttonRow.Append(connectButton);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        content.SetMarginTop(14);
        content.SetMarginBottom(14);
        content.Append(hostLabel);
        content.Append(hostEntry);
        content.Append(hostHint);
        content.Append(hostErrorLabel);
        content.Append(userLabel);
        content.Append(userEntry);
        content.Append(passwordLabel);
        content.Append(passwordEntry);
        content.Append(frameBufferCheck);
        content.Append(debuggingCheck);
        content.Append(orientationCheck);
        content.Append(errorLabel);
        content.Append(progressLabel);
        content.Append(buttonRow);
        window.SetChild(content);
        window.SetDefaultWidget(connectButton);

        Validate();
    }

    /// <summary>Shows the dialog.</summary>
    public void Present() => window.Present();

    void WatchText(Gtk.Entry entry)
    {
        entry.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "text")
                Validate();
        };
    }

    // The single validation pass: an empty host or user just disables
    // Connect; a host that cannot be parsed is flagged on the entry itself.
    void Validate()
    {
        if (connecting)
            return;

        var host = hostEntry.GetText().Trim();
        var hostProblem = host.Length > 0 && SshHostEndpoint.TryParse(host) == null
            ? "This is not a usable host — expected a name or address, optionally with \":port\" (1-65535)."
            : null;
        if (hostProblem == null)
            hostEntry.RemoveCssClass("error");
        else
            hostEntry.AddCssClass("error");
        hostErrorLabel.SetText(hostProblem ?? "");
        hostErrorLabel.SetVisible(hostProblem != null);

        connectButton.SetSensitive(
            host.Length > 0 && hostProblem == null && userEntry.GetText().Trim().Length > 0);
    }

    async Task ConnectAsync()
    {
        var host = hostEntry.GetText().Trim();
        var user = userEntry.GetText().Trim();
        var password = passwordEntry.GetText();
        if (connecting || SshHostEndpoint.TryParse(host) is not { } endpoint || user.Length == 0)
            return;

        // Remembered from the moment of the attempt — a typo the user fixes
        // next time should not cost them the rest of the form.
        IdePreferences.SshDeviceHost.Value = host;
        IdePreferences.SshDeviceUser.Value = user;
        IdePreferences.SshDevicePassword.Value = password;
        IdePreferences.SshDeviceUseAsFrameBuffer.Value = frameBufferCheck.GetActive();
        // Debugging depends on FrameBuffer; never store it on without its parent.
        IdePreferences.SshDeviceEnableDebugging.Value =
            frameBufferCheck.GetActive() && debuggingCheck.GetActive();
        IdePreferences.SshDeviceEnableOrientation.Value =
            frameBufferCheck.GetActive() && orientationCheck.GetActive();

        connecting = true;
        SetBusy(true);
        errorLabel.SetText("");
        errorLabel.SetVisible(false);
        progressLabel.SetText($"Connecting to {endpoint.Host}…");

        var session = new SshTerminalSession(endpoint.Host, endpoint.Port, user, password);
        try
        {
            await Task.Run(() => session.Connect(terminalColumns, terminalRows));
            window.Close();
            Connected?.Invoke(session);
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"SSH connection to {endpoint.Host}:{endpoint.Port} failed", ex);
            _ = Task.Run(session.Dispose);
            progressLabel.SetText("");
            errorLabel.SetText($"Could not connect: {ex.Message}");
            errorLabel.SetVisible(true);
            connecting = false;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        hostEntry.SetSensitive(!busy);
        userEntry.SetSensitive(!busy);
        passwordEntry.SetSensitive(!busy);
        frameBufferCheck.SetSensitive(!busy);
        // Only re-enable debugging if its parent is checked.
        debuggingCheck.SetSensitive(!busy && frameBufferCheck.GetActive());
        orientationCheck.SetSensitive(!busy && frameBufferCheck.GetActive());
        connectButton.SetSensitive(!busy);
        cancelButton.SetSensitive(!busy);
    }
}
