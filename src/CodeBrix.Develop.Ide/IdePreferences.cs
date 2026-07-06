//
// IdePreferences.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.IdePreferences, simplified for
//      CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using CodeBrix.Develop.Core.Options;

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
    /// How many automatic startup backups of options.sqlite to retain;
    /// 0 disables the automatic backup entirely.
    /// </summary>
    public static readonly ConfigurationProperty<int> AutoBackupRetention =
        ConfigurationProperty.Create(OptionsStore.AutoBackupRetentionKey, OptionsStore.DefaultAutoBackupRetention);

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
}
