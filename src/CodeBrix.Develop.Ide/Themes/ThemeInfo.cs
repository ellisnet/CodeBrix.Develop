//
// ThemeInfo.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Ide.Themes;

/// <summary>
/// A catalog entry for one available color theme: identity and metadata
/// only; the full definition is loaded on demand from the embedded
/// VS Code theme JSON.
/// </summary>
public sealed class ThemeInfo
{
    /// <summary>The stable identifier stored in options.sqlite (e.g. "dark-modern").</summary>
    public string Id { get; }

    /// <summary>The display name shown in the Options dialog (e.g. "Dark Modern").</summary>
    public string Name { get; }

    /// <summary>The embedded-resource name of the theme definition JSON.</summary>
    public string ResourceName { get; }

    /// <summary>Whether this is a dark theme (drives icon variants and GTK's dark hint).</summary>
    public bool IsDark { get; }

    /// <summary>The GtkSourceView style-scheme id generated for this theme.</summary>
    public string EditorSchemeId => $"codebrix-{Id}";

    /// <summary>Creates a catalog entry.</summary>
    public ThemeInfo(string id, string name, string resourceName, bool isDark)
    {
        Id = id;
        Name = name;
        ResourceName = resourceName;
        IsDark = isDark;
    }
}
