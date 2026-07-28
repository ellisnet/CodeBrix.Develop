//
// IdePreferences.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.IdePreferences, simplified for
//      CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using CodeBrix.Develop.Core.Options;
using CodeBrix.Develop.Emulation.FrameBuffer;

namespace CodeBrix.Develop.Ide;

/// <summary>
/// The IDE-level configuration properties, each a typed handle over a value
/// in the portable options.sqlite store. EVERYTHING configurable in
/// CodeBrix.Develop — dialog-exposed options and remembered UI state alike —
/// lives here (or in a sibling property class), never in ad-hoc files.
/// </summary>
public static class IdePreferences
{
    /// <summary>
    /// The id of the selected color theme, or "" while the user has never
    /// chosen one (first run then follows the desktop dark/light preference).
    /// </summary>
    public static readonly ConfigurationProperty<string> ColorTheme =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.ColorTheme", "");

    /// <summary>
    /// Whether the solution name in the window title is spelled out with
    /// spaces — "Doom.Brix" shown as "Doom Brix". Off by default. On, the
    /// IDE's window title no longer contains the solution's name verbatim,
    /// so a search for the window of the application being built cannot
    /// land on the IDE instead.
    /// </summary>
    public static readonly ConfigurationProperty<bool> SpacedSolutionTitle =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.SpacedSolutionTitle", false);

    /// <summary>
    /// The folder where the user's projects normally live, or "" to use the
    /// user's Documents folder. Read it through
    /// <see cref="IdeApp.GetProjectsDirectory"/>, which handles the blank
    /// default and silently blanks a folder that no longer exists.
    /// </summary>
    public static readonly ConfigurationProperty<string> ProjectsFolder =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.ProjectsFolder", "");

    /// <summary>
    /// How many automatic startup backups of options.sqlite to retain;
    /// 0 disables the automatic backup entirely.
    /// </summary>
    public static readonly ConfigurationProperty<int> AutoBackupRetention =
        ConfigurationProperty.Create(OptionsStore.AutoBackupRetentionKey, OptionsStore.DefaultAutoBackupRetention);

    /// <summary>
    /// The full path of the last solution the user worked on, reopened on
    /// the next start; "" when no solution was open when the application
    /// closed (the next start then shows the New Application experience).
    /// </summary>
    public static readonly ConfigurationProperty<string> LastSolution =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.LastSolution", "");

    /// <summary>
    /// The full path of the project file chosen via "Set as Startup Project",
    /// or "" to run the solution's default (first executable) project. Read
    /// it through <see cref="IdeApp.GetStartupProject"/>, which silently
    /// blanks a value that is invalid or not part of the open solution.
    /// </summary>
    public static readonly ConfigurationProperty<string> StartupProject =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.StartupProject", "");

    /// <summary>The remembered workbench window width.</summary>
    public static readonly ConfigurationProperty<int> WorkbenchWidth =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.Workbench.Width", 1360);

    /// <summary>The remembered workbench window height.</summary>
    public static readonly ConfigurationProperty<int> WorkbenchHeight =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.Workbench.Height", 880);

    /// <summary>Whether the workbench window was maximized.</summary>
    public static readonly ConfigurationProperty<bool> WorkbenchMaximized =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.Workbench.Maximized", false);

    /// <summary>The remembered position of the Solution pad splitter.</summary>
    public static readonly ConfigurationProperty<int> SolutionPanePosition =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.Workbench.SolutionPanePosition", 300);

    /// <summary>The remembered position of the output-pads splitter.</summary>
    public static readonly ConfigurationProperty<int> OutputPanePosition =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.Workbench.OutputPanePosition", 600);

    /// <summary>The id of the Options page shown when the dialog last closed.</summary>
    public static readonly ConfigurationProperty<string> OptionsLastPage =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.OptionsDialog.LastPage", "");

    /// <summary>
    /// How the emulated frame-buffer device is held. Stored by name, so the
    /// enum's members must not be renamed.
    /// </summary>
    public static readonly ConfigurationProperty<FrameBufferOrientation> FrameBufferScreenOrientation =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.Orientation",
            FrameBufferOrientation.Portrait);

    /// <summary>
    /// The screen the emulated frame-buffer device has, as a size class plus
    /// its portrait dimensions. Stored by name, so the enum's members must
    /// not be renamed.
    /// </summary>
    public static readonly ConfigurationProperty<FrameBufferResolution> FrameBufferScreenResolution =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.Resolution",
            FrameBufferResolution.SevenInch720x1280);

    /// <summary>
    /// The system language of the emulated frame-buffer device, as one of the
    /// codes in <see cref="FrameBufferLanguageInfo.All"/> —
    /// <see cref="FrameBufferLanguageInfo.SystemDefaultCode"/> (the default)
    /// to follow the host's own language. Stored only — nothing reads it yet.
    /// </summary>
    public static readonly ConfigurationProperty<string> FrameBufferSystemLanguage =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.SystemLanguage",
            FrameBufferLanguageInfo.SystemDefaultCode);

    /// <summary>
    /// Whether the emulated frame-buffer device confines the application to the
    /// fonts it actually ships, rather than letting the host desktop's installed
    /// fonts fill in what the application has no font for. On by default: the
    /// host is a full desktop and the device is not, so without it the emulator
    /// shows text a real device could not display.
    /// </summary>
    public static readonly ConfigurationProperty<bool> FrameBufferFontIsolation =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.FontIsolation", true);

    /// <summary>
    /// Whether the emulated frame-buffer device has a hardware keyboard.
    /// Stored only — nothing reads it yet.
    /// </summary>
    public static readonly ConfigurationProperty<bool> FrameBufferHardwareKeyboard =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.HardwareKeyboard", false);

    /// <summary>
    /// The remembered LONGER side of the frame-buffer emulator's screen area (the
    /// window is this plus its bezel); 0 until it has been shown once. Stored
    /// orientation-independently as a long and a short side — the same way
    /// FrameBufferResolutionInfo stores the device — so that neither an Options
    /// orientation change nor a rotated emulator window can carry an orientation
    /// into the next window: taking the longer and shorter sides IS the
    /// normalization. Its POSITION is deliberately absent: GTK 4 has no
    /// window-positioning API, so a window's place on screen can neither be read
    /// nor restored (which also means a remembered emulator can never come back
    /// off-screen).
    /// </summary>
    public static readonly ConfigurationProperty<int> FrameBufferWindowLongSide =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.Window.LongSide", 0);

    /// <summary>
    /// The remembered SHORTER side of the frame-buffer emulator's screen area;
    /// 0 until it has been shown once.
    /// </summary>
    public static readonly ConfigurationProperty<int> FrameBufferWindowShortSide =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.Window.ShortSide", 0);

    /// <summary>
    /// The width/height pair the emulator's size used to be stored as, kept only
    /// to seed <see cref="FrameBufferWindowLongSide"/> and
    /// <see cref="FrameBufferWindowShortSide"/> the first time an older stored
    /// size is read. Taking the larger and smaller of the two is correct for
    /// every value ever written under the old scheme, so nobody loses a
    /// remembered size.
    /// </summary>
    public static readonly ConfigurationProperty<int> FrameBufferWindowLegacyWidth =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.Window.Width", 0);

    /// <inheritdoc cref="FrameBufferWindowLegacyWidth"/>
    public static readonly ConfigurationProperty<int> FrameBufferWindowLegacyHeight =
        ConfigurationProperty.Create("CodeBrix.Develop.Ide.FrameBuffer.Window.Height", 0);
}
