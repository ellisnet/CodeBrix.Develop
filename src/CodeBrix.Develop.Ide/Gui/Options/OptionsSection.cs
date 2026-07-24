//
// OptionsSection.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop's OptionsDialogSection/OptionsPanelNode
//      extension nodes, registered in code instead of add-in XML)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Ide.Gui.Options;

/// <summary>
/// One node of the Options dialog's section tree: a top-level category
/// (label only) or a selectable page (icon + panel factory). MonoDevelop
/// declared this tree in add-in XML; CodeBrix.Develop registers it in code —
/// see <see cref="IdeOptionsSections"/>.
/// </summary>
public sealed class OptionsSection
{
    /// <summary>The stable id, used for last-visited-page memory.</summary>
    public string Id { get; }

    /// <summary>The label shown in the section list and page header.</summary>
    public string Label { get; }

    /// <summary>The logical icon name shown next to the label, or null.</summary>
    public string? IconName { get; }

    /// <summary>Creates the section's panel; null for category headers.</summary>
    public Func<IOptionsPanel>? PanelFactory { get; }

    /// <summary>Child sections (pages of a category).</summary>
    public List<OptionsSection> Children { get; } = new();

    /// <summary>Creates a category header (no panel of its own).</summary>
    public OptionsSection(string id, string label)
        : this(id, label, null, null)
    {
    }

    /// <summary>Creates a selectable page section.</summary>
    public OptionsSection(string id, string label, string? iconName, Func<IOptionsPanel>? panelFactory)
    {
        Id = id;
        Label = label;
        IconName = iconName;
        PanelFactory = panelFactory;
    }
}

/// <summary>
/// The in-code registration of the global Options dialog's section tree
/// (MonoDevelop's /MonoDevelop/Ide/GlobalOptionsDialog extension point,
/// reduced to a method). Add new pages here.
/// </summary>
public static class IdeOptionsSections
{
    /// <summary>Builds the section tree for the global Options dialog.</summary>
    public static IReadOnlyList<OptionsSection> Build() => new[]
    {
        new OptionsSection("Environment", "Environment")
        {
            Children =
            {
                new OptionsSection("General", "General", "prefs-generic-16",
                    () => new GeneralOptionsPanel()),
                new OptionsSection("Appearance", "Appearance", "prefs-visual-style-16",
                    () => new AppearanceOptionsPanel()),
                new OptionsSection("Backup", "Backup", "prefs-maintenance-16",
                    () => new BackupOptionsPanel()),
            },
        },
        new OptionsSection("RunAndDebug", "Run and Debug")
        {
            Children =
            {
                new OptionsSection("FrameBuffer", "Frame Buffer", "prefs-debugger-16",
                    () => new FrameBufferOptionsPanel()),
            },
        },
    };
}
