//
// FrameBufferDeviceReadyDialog.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Dialogs;

/// <summary>
/// The "device is ready" summary shown after a FrameBuffer device connects
/// and its .NET runtime is confirmed present (detected or just installed):
/// the little the IDE knows about the hardware — manufacturer/model,
/// resolution, operating system, and .NET version — so the user can see at a
/// glance that the device is connected and ready to run applications.
/// </summary>
public class FrameBufferDeviceReadyDialog
{
    readonly Gtk.Window window;

    /// <summary>Creates the dialog over the given parent window.</summary>
    public FrameBufferDeviceReadyDialog(
        Gtk.Window parent,
        string deviceName,
        string manufacturerModel,
        string resolution,
        string operatingSystem,
        string dotnetVersion)
    {
        window = Gtk.Window.New();
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetTitle("FrameBuffer Device Ready");
        window.SetDefaultSize(460, -1);

        var heading = Gtk.Label.New(null);
        heading.SetMarkup("<b>The FrameBuffer device is connected and .NET is ready.</b>");
        heading.SetXalign(0);
        heading.SetWrap(true);

        var subheading = Gtk.Label.New(deviceName);
        subheading.SetXalign(0);
        subheading.AddCssClass("dim-label");
        subheading.SetMarginBottom(6);

        var grid = Gtk.Grid.New();
        grid.SetRowSpacing(6);
        grid.SetColumnSpacing(12);
        AddRow(grid, 0, "Manufacturer & Model:", manufacturerModel);
        AddRow(grid, 1, "Resolution:", resolution);
        AddRow(grid, 2, "Operating System:", operatingSystem);
        AddRow(grid, 3, ".NET Version:", dotnetVersion);

        var closeButton = Gtk.Button.NewWithLabel("Close");
        closeButton.AddCssClass("suggested-action");
        closeButton.OnClicked += (_, _) => window.Close();
        var buttonRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttonRow.SetHalign(Gtk.Align.End);
        buttonRow.SetMarginTop(8);
        buttonRow.Append(closeButton);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        content.SetMarginTop(14);
        content.SetMarginBottom(14);
        content.Append(heading);
        content.Append(subheading);
        content.Append(grid);
        content.Append(buttonRow);
        window.SetChild(content);
        window.SetDefaultWidget(closeButton);
    }

    /// <summary>Shows the dialog.</summary>
    public void Present() => window.Present();

    static void AddRow(Gtk.Grid grid, int row, string label, string value)
    {
        var name = Gtk.Label.New(label);
        name.SetXalign(0);
        name.AddCssClass("dim-label");

        var content = Gtk.Label.New(value);
        content.SetXalign(0);
        content.SetWrap(true);
        content.SetHexpand(true);

        grid.Attach(name, 0, row, 1, 1);
        grid.Attach(content, 1, row, 1, 1);
    }
}
