//
// VSCodeTheme.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//
// Parses the Visual Studio Code color-theme JSON files embedded under
// Assets/VSCodeThemes/ (see THIRD-PARTY-NOTICES.txt). The theme FILES are
// unmodified upstream data; this parser is original CodeBrix.Develop code.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace CodeBrix.Develop.Ide.Themes;

/// <summary>
/// A fully resolved VS Code color theme: the merged workbench color table
/// and TextMate token-color rules of the theme file and its include chain.
/// </summary>
public sealed class VSCodeTheme
{
    static readonly Assembly assembly = typeof(VSCodeTheme).Assembly;
    static readonly JsonDocumentOptions jsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly Dictionary<string, string> colors;
    readonly List<TokenRule> tokenRules;

    /// <summary>The catalog entry this definition was loaded for.</summary>
    public ThemeInfo Info { get; }

    /// <summary>The foreground of the theme's scope-less default token rule, if any.</summary>
    public string? DefaultTokenForeground { get; }

    /// <summary>One TextMate token-color rule: scopes plus style settings.</summary>
    public sealed class TokenRule
    {
        /// <summary>The scopes the rule applies to.</summary>
        public required IReadOnlyList<string> Scopes { get; init; }

        /// <summary>The foreground color, when the rule sets one.</summary>
        public string? Foreground { get; init; }

        /// <summary>The font style ("italic", "bold underline", …), when the rule sets one.</summary>
        public string? FontStyle { get; init; }
    }

    /// <summary>The resolved style for a token scope.</summary>
    public sealed class TokenStyle
    {
        /// <summary>The foreground color, normalized for GTK/GtkSourceView.</summary>
        public string? Foreground { get; init; }

        /// <summary>Whether the token renders bold.</summary>
        public bool Bold { get; init; }

        /// <summary>Whether the token renders italic.</summary>
        public bool Italic { get; init; }

        /// <summary>Whether the token renders underlined.</summary>
        public bool Underline { get; init; }
    }

    VSCodeTheme(ThemeInfo info, Dictionary<string, string> colors, List<TokenRule> tokenRules, string? defaultTokenForeground)
    {
        Info = info;
        this.colors = colors;
        this.tokenRules = tokenRules;
        DefaultTokenForeground = defaultTokenForeground;
    }

    /// <summary>
    /// Loads and fully resolves a theme (following its "include" chain)
    /// from the embedded theme resources.
    /// </summary>
    public static VSCodeTheme Load(ThemeInfo info)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        var rules = new List<TokenRule>();
        string? defaultForeground = null;
        LoadInto(info.ResourceName, colors, rules, ref defaultForeground);
        return new VSCodeTheme(info, colors, rules, defaultForeground);
    }

    static void LoadInto(string resourceName, Dictionary<string, string> colors, List<TokenRule> rules, ref string? defaultForeground)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded theme resource '{resourceName}' not found");
        using var document = JsonDocument.Parse(stream, jsonOptions);
        var root = document.RootElement;

        // Parent first, so this theme's own values win.
        if (root.TryGetProperty("include", out var include) && include.GetString() is { Length: > 0 } parentFile)
            LoadInto("themes/" + Path.GetFileName(parentFile), colors, rules, ref defaultForeground);

        if (root.TryGetProperty("colors", out var colorTable) && colorTable.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in colorTable.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } color)
                    colors[entry.Name] = color;
            }
        }

        if (root.TryGetProperty("tokenColors", out var tokenColors) && tokenColors.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleElement in tokenColors.EnumerateArray())
            {
                if (ruleElement.ValueKind != JsonValueKind.Object
                    || !ruleElement.TryGetProperty("settings", out var settings)
                    || settings.ValueKind != JsonValueKind.Object)
                    continue;

                string? foreground = null, fontStyle = null;
                if (settings.TryGetProperty("foreground", out var fg) && fg.ValueKind == JsonValueKind.String)
                    foreground = fg.GetString();
                if (settings.TryGetProperty("fontStyle", out var fs) && fs.ValueKind == JsonValueKind.String)
                    fontStyle = fs.GetString();

                var scopes = ReadScopes(ruleElement);
                if (scopes.Count == 0)
                {
                    // A scope-less rule is the theme's default text style.
                    defaultForeground = foreground ?? defaultForeground;
                    continue;
                }
                if (foreground != null || fontStyle != null)
                    rules.Add(new TokenRule { Scopes = scopes, Foreground = foreground, FontStyle = fontStyle });
            }
        }
    }

    static List<string> ReadScopes(JsonElement rule)
    {
        var scopes = new List<string>();
        if (!rule.TryGetProperty("scope", out var scope))
            return scopes;
        if (scope.ValueKind == JsonValueKind.String)
            AddScopeText(scopes, scope.GetString());
        else if (scope.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in scope.EnumerateArray())
                if (element.ValueKind == JsonValueKind.String)
                    AddScopeText(scopes, element.GetString());
        }
        return scopes;
    }

    static void AddScopeText(List<string> scopes, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        // A scope string may hold a comma-separated list. Descendant
        // selectors (space-separated) match a token in context; the last
        // element is what the token itself must match.
        foreach (var part in text.Split(','))
        {
            var scope = part.Trim();
            var lastSpace = scope.LastIndexOf(' ');
            if (lastSpace >= 0)
                scope = scope[(lastSpace + 1)..];
            if (scope.Length > 0)
                scopes.Add(scope);
        }
    }

    /// <summary>
    /// Returns the first defined workbench color among the given keys,
    /// normalized for GTK CSS, or the fallback when none is defined.
    /// </summary>
    public string GetColor(string fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (colors.TryGetValue(key, out var value) && NormalizeColor(value) is { } normalized)
                return normalized;
        }
        return NormalizeColor(fallback) ?? fallback;
    }

    /// <summary>Whether the theme defines any of the given workbench colors.</summary>
    public bool HasColor(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (colors.ContainsKey(key))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the token style for the first of the candidate scopes that
    /// any token rule matches, or null when none match.
    /// </summary>
    public TokenStyle? MatchToken(params string[] candidateScopes)
    {
        foreach (var candidate in candidateScopes)
        {
            var style = MatchSingle(candidate);
            if (style != null)
                return style;
        }
        return null;
    }

    TokenStyle? MatchSingle(string candidate)
    {
        TokenRule? best = null;
        var bestScore = -1;
        foreach (var rule in tokenRules)
        {
            foreach (var scope in rule.Scopes)
            {
                // Exact match beats rule-is-prefix-of-candidate (TextMate
                // semantics) beats borrowing a more specific rule; among
                // prefixes the longest wins, and later rules win ties.
                int score;
                if (scope == candidate)
                    score = 2000;
                else if (candidate.StartsWith(scope + ".", StringComparison.Ordinal))
                    score = 1000 + scope.Length;
                else if (scope.StartsWith(candidate + ".", StringComparison.Ordinal))
                    score = 500 - (scope.Length - candidate.Length);
                else
                    continue;
                if (score >= bestScore)
                {
                    bestScore = score;
                    best = rule;
                }
            }
        }
        if (best == null)
            return null;

        var fontStyle = best.FontStyle ?? "";
        return new TokenStyle
        {
            Foreground = NormalizeColor(best.Foreground),
            Bold = fontStyle.Contains("bold", StringComparison.Ordinal),
            Italic = fontStyle.Contains("italic", StringComparison.Ordinal),
            Underline = fontStyle.Contains("underline", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Normalizes a VS Code color value for GTK: hex colors with alpha
    /// ("#RRGGBBAA") become "rgba(r, g, b, a)" — which both GTK CSS and
    /// GtkSourceView accept — and plain hex passes through.
    /// </summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        color = color.Trim();
        if (!color.StartsWith('#'))
            return color;

        var hex = color[1..];
        return hex.Length switch
        {
            3 or 6 => color,
            4 => Rgba(Expand(hex[0]), Expand(hex[1]), Expand(hex[2]), Expand(hex[3])),
            8 => Rgba(Hex2(hex, 0), Hex2(hex, 2), Hex2(hex, 4), Hex2(hex, 6)),
            _ => null,
        };

        static int Expand(char c) => Hex(c) * 17;
        static int Hex(char c) => Convert.ToInt32(c.ToString(), 16);
        static int Hex2(string s, int index) => int.Parse(s.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static string Rgba(int r, int g, int b, int a) =>
            string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {a / 255.0:0.###})");
    }
}
