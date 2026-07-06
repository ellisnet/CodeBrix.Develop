//
// ThemeService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by VS Code's workbench theme service, built on GTK 4 CSS
//      providers and GtkSourceView style schemes for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;
using GtkSource = CodeBrix.Develop.UI.GtkSource;

namespace CodeBrix.Develop.Ide.Themes;

/// <summary>
/// Owns the application's color theme: the catalog of available VS Code
/// themes, the currently applied theme (persisted portably in
/// options.sqlite), the generated GTK CSS, and the generated GtkSourceView
/// editor style schemes. Applying a theme re-renders the whole application.
/// </summary>
public static class ThemeService
{
    // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION: above the GTK theme, below user overrides.
    const uint StyleProviderPriorityApplication = 600;

    static readonly ThemeInfo[] catalog =
    {
        new("2026-dark", "2026 Dark", "themes/2026-dark.json", isDark: true),
        new("2026-light", "2026 Light", "themes/2026-light.json", isDark: false),
        new("abyss", "Abyss", "themes/abyss-color-theme.json", isDark: true),
        new("dark-modern", "Dark Modern", "themes/dark_modern.json", isDark: true),
        new("dark-plus", "Dark+", "themes/dark_plus.json", isDark: true),
        new("dark-vs", "Dark (Visual Studio)", "themes/dark_vs.json", isDark: true),
        new("kimbie-dark", "Kimbie Dark", "themes/kimbie-dark-color-theme.json", isDark: true),
        new("light-modern", "Light Modern", "themes/light_modern.json", isDark: false),
        new("light-plus", "Light+", "themes/light_plus.json", isDark: false),
        new("light-vs", "Light (Visual Studio)", "themes/light_vs.json", isDark: false),
        new("monokai", "Monokai", "themes/monokai-color-theme.json", isDark: true),
        new("monokai-dimmed", "Monokai Dimmed", "themes/dimmed-monokai-color-theme.json", isDark: true),
        new("quiet-light", "Quiet Light", "themes/quietlight-color-theme.json", isDark: false),
        new("red", "Red", "themes/red-color-theme.json", isDark: true),
        new("solarized-dark", "Solarized Dark", "themes/solarized-dark-color-theme.json", isDark: true),
        new("solarized-light", "Solarized Light", "themes/solarized-light-color-theme.json", isDark: false),
        new("tomorrow-night-blue", "Tomorrow Night Blue", "themes/tomorrow-night-blue-color-theme.json", isDark: true),
    };

    static readonly Dictionary<string, VSCodeTheme> loadedThemes = new(StringComparer.Ordinal);
    static Gtk.CssProvider? currentProvider;

    /// <summary>All available themes, ordered for display.</summary>
    public static IReadOnlyList<ThemeInfo> Themes => catalog;

    /// <summary>The currently applied theme; null before <see cref="Initialize"/>.</summary>
    public static ThemeInfo? CurrentTheme { get; private set; }

    /// <summary>Raised after a theme has been applied (including at startup).</summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// The folder the generated GtkSourceView style schemes are written to —
    /// derived cache data, deliberately NOT in the options folder, which
    /// holds nothing but options.sqlite and its backups.
    /// </summary>
    public static string SchemeDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeBrix.Develop", "style-schemes");

    /// <summary>Looks up a theme by its stored id, or null when unknown.</summary>
    public static ThemeInfo? Find(string? id) => catalog.FirstOrDefault(theme => theme.Id == id);

    /// <summary>
    /// The theme id used when options.sqlite holds no explicit choice yet
    /// (first run): Dark Modern or Light Modern, following the desktop's
    /// dark/light preference.
    /// </summary>
    public static string DefaultThemeId => SystemPrefersDark() ? "dark-modern" : "light-modern";

    /// <summary>
    /// Generates the editor style schemes, registers their search path, and
    /// applies the persisted (or detected first-run) theme. Must run on the
    /// GTK main loop after Gtk has initialized and before the first window
    /// renders.
    /// </summary>
    public static void Initialize()
    {
        WriteEditorSchemes();
        GtkSource.StyleSchemeManager.GetDefault().AppendSearchPath(SchemeDirectory);

        var storedId = IdePreferences.ColorTheme.Value;
        var theme = Find(storedId);
        if (theme == null)
        {
            if (!string.IsNullOrEmpty(storedId))
                LoggingService.LogWarning($"Stored color theme '{storedId}' is unknown; using the default");
            theme = Find(DefaultThemeId);
        }
        Apply(theme!.Id);
    }

    /// <summary>
    /// Applies a theme to the running application: swaps the generated CSS
    /// provider, flips GTK's dark hint, and notifies listeners (editors,
    /// icon consumers) so everything re-renders. Does NOT persist the
    /// choice — set <see cref="IdePreferences.ColorTheme"/> for that.
    /// </summary>
    public static void Apply(string themeId)
    {
        var info = Find(themeId) ?? throw new ArgumentException($"Unknown theme '{themeId}'", nameof(themeId));
        var definition = GetDefinition(info);

        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(ThemeCssGenerator.Generate(definition));

        var display = Gdk.Display.GetDefault();
        if (display != null)
        {
            if (currentProvider != null)
                Gtk.StyleContext.RemoveProviderForDisplay(display, currentProvider);
            Gtk.StyleContext.AddProviderForDisplay(display, provider, StyleProviderPriorityApplication);
        }
        currentProvider = provider;

        if (Gtk.Settings.GetDefault() is { } settings)
            settings.GtkApplicationPreferDarkTheme = info.IsDark;

        CurrentTheme = info;
        LoggingService.LogInfo($"Color theme applied: {info.Name}");
        ThemeChanged?.Invoke();
    }

    /// <summary>The GtkSourceView style scheme of the current theme, or null.</summary>
    public static GtkSource.StyleScheme? GetEditorScheme() =>
        CurrentTheme == null ? null : GtkSource.StyleSchemeManager.GetDefault().GetScheme(CurrentTheme.EditorSchemeId);

    /// <summary>
    /// Best-effort detection of the desktop's dark/light preference, used
    /// only for the first-run default before a theme is stored.
    /// </summary>
    public static bool SystemPrefersDark()
    {
        var settings = Gtk.Settings.GetDefault();
        if (settings == null)
            return false;
        if (settings.GtkApplicationPreferDarkTheme)
            return true;
        return settings.GtkThemeName?.Contains("dark", StringComparison.OrdinalIgnoreCase) == true;
    }

    static VSCodeTheme GetDefinition(ThemeInfo info)
    {
        if (!loadedThemes.TryGetValue(info.Id, out var definition))
        {
            definition = VSCodeTheme.Load(info);
            loadedThemes[info.Id] = definition;
        }
        return definition;
    }

    static void WriteEditorSchemes()
    {
        Directory.CreateDirectory(SchemeDirectory);
        foreach (var info in catalog)
        {
            try
            {
                var xml = EditorSchemeGenerator.Generate(GetDefinition(info));
                File.WriteAllText(Path.Combine(SchemeDirectory, $"{info.EditorSchemeId}.xml"), xml);
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Could not generate editor scheme for theme '{info.Name}'", ex);
            }
        }
    }
}
