//
// FrameBufferOptionsPanel.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System.Linq;
using CodeBrix.Develop.Emulation.FrameBuffer;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// The Frame Buffer options page: the orientation, screen and system language
/// of the emulated device a Linux Frame Buffer head is run and debugged
/// against, and whether that device has a hardware keyboard. Changing the
/// orientation relabels the resolution list in place, so the chosen screen
/// carries over to its counterpart in the new orientation.
/// </summary>
public class FrameBufferOptionsPanel : OptionsPanel
{
    Gtk.DropDown? orientationDropDown;
    Gtk.DropDown? resolutionDropDown;
    Gtk.DropDown? languageDropDown;
    Gtk.CheckButton? hardwareKeyboardCheck;
    FrameBufferOrientation orientation;

    /// <inheritdoc/>
    public override Gtk.Widget CreatePanelWidget()
    {
        orientation = IdePreferences.FrameBufferScreenOrientation.Value;

        var heading = Gtk.Label.New("Frame Buffer emulation");
        heading.AddCssClass("heading");
        heading.SetXalign(0);

        var orientationLabel = Gtk.Label.New("Orientation:");
        orientationLabel.SetXalign(0);

        orientationDropDown = Gtk.DropDown.NewFromStrings(FrameBufferOrientations.Labels.ToArray());
        orientationDropDown.SetHalign(Gtk.Align.Start);
        orientationDropDown.SetSelected((uint) FrameBufferOrientations.IndexOf(orientation));
        orientationDropDown.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                OnOrientationChanged();
        };

        var resolutionLabel = Gtk.Label.New("Resolution:");
        resolutionLabel.SetXalign(0);
        resolutionLabel.SetMarginTop(6);

        resolutionDropDown = Gtk.DropDown.NewFromStrings(
            FrameBufferResolutionInfo.GetLabels(orientation).ToArray());
        resolutionDropDown.SetHalign(Gtk.Align.Start);
        resolutionDropDown.SetSelected(
            (uint) FrameBufferResolutionInfo.IndexOf(IdePreferences.FrameBufferScreenResolution.Value));

        var languageLabel = Gtk.Label.New("Emulator system language:");
        languageLabel.SetXalign(0);
        languageLabel.SetMarginTop(6);

        languageDropDown = Gtk.DropDown.NewFromStrings(FrameBufferLanguageInfo.Labels.ToArray());
        languageDropDown.SetHalign(Gtk.Align.Start);
        languageDropDown.SetSelected(
            (uint) FrameBufferLanguageInfo.IndexOf(IdePreferences.FrameBufferSystemLanguage.Value));

        hardwareKeyboardCheck = Gtk.CheckButton.NewWithLabel("Hardware keyboard support");
        hardwareKeyboardCheck.SetActive(IdePreferences.FrameBufferHardwareKeyboard.Value);
        hardwareKeyboardCheck.SetMarginTop(10);

        var description = Gtk.Label.New(
            "These settings describe the emulated device a Linux Frame Buffer head is\n" +
            "run and debugged against. The emulator window keeps the proportions of the\n" +
            "resolution selected here. Orientation and resolution changes apply to the\n" +
            "next emulator window that opens: an emulator already open stays as it is\n" +
            "until it is closed, from Tools > Close Emulator or the window manager.\n" +
            "\n" +
            "Hardware keyboard support applies at once. With it on, keystrokes go to the\n" +
            "application running in the emulator whenever the emulator window is the\n" +
            "active one, modifier combinations included — the keyboard belongs to the\n" +
            "device while its window has the focus. With it off the device behaves as a\n" +
            "kiosk with no keyboard attached, which is how a Linux Frame Buffer head\n" +
            "usually runs.");
        description.SetXalign(0);
        description.AddCssClass("dim-label");
        description.SetMarginTop(10);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginStart(16);
        box.SetMarginEnd(16);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.Append(heading);
        box.Append(orientationLabel);
        box.Append(orientationDropDown);
        box.Append(resolutionLabel);
        box.Append(resolutionDropDown);
        box.Append(languageLabel);
        box.Append(languageDropDown);
        box.Append(hardwareKeyboardCheck);
        box.Append(description);
        return box;
    }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges() =>
        SelectedOrientation != IdePreferences.FrameBufferScreenOrientation.Value ||
        SelectedResolution != IdePreferences.FrameBufferScreenResolution.Value ||
        SelectedLanguage != IdePreferences.FrameBufferSystemLanguage.Value ||
        SelectedHardwareKeyboard != IdePreferences.FrameBufferHardwareKeyboard.Value;

    /// <inheritdoc/>
    public override void ApplyChanges()
    {
        IdePreferences.FrameBufferScreenOrientation.Value = SelectedOrientation;
        IdePreferences.FrameBufferScreenResolution.Value = SelectedResolution;
        IdePreferences.FrameBufferSystemLanguage.Value = SelectedLanguage;
        IdePreferences.FrameBufferHardwareKeyboard.Value = SelectedHardwareKeyboard;
    }

    FrameBufferOrientation SelectedOrientation => orientationDropDown == null
        ? IdePreferences.FrameBufferScreenOrientation.Value
        : FrameBufferOrientations.FromIndex((int) orientationDropDown.GetSelected());

    FrameBufferResolution SelectedResolution => resolutionDropDown == null
        ? IdePreferences.FrameBufferScreenResolution.Value
        : FrameBufferResolutionInfo.FromIndex((int) resolutionDropDown.GetSelected()).Resolution;

    string SelectedLanguage => languageDropDown == null
        ? IdePreferences.FrameBufferSystemLanguage.Value
        : FrameBufferLanguageInfo.FromIndex((int) languageDropDown.GetSelected()).Code;

    bool SelectedHardwareKeyboard => hardwareKeyboardCheck?.GetActive()
        ?? IdePreferences.FrameBufferHardwareKeyboard.Value;

    // The resolution list holds the same screens in the same order in both
    // orientations, so flipping the orientation only relabels it — keeping
    // the selected position keeps the user on the screen they had chosen.
    void OnOrientationChanged()
    {
        if (orientationDropDown == null || resolutionDropDown == null)
            return;
        var selected = FrameBufferOrientations.FromIndex((int) orientationDropDown.GetSelected());
        if (selected == orientation)
            return;
        orientation = selected;

        var position = resolutionDropDown.GetSelected();
        resolutionDropDown.SetModel(
            Gtk.StringList.New(FrameBufferResolutionInfo.GetLabels(orientation).ToArray()));
        resolutionDropDown.SetSelected(
            (uint) FrameBufferResolutionInfo.IndexOf(
                FrameBufferResolutionInfo.FromIndex((int) position).Resolution));
    }
}
