//
// SemanticStyleMap.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (maps Roslyn classification type names onto TextMate scopes the same
//      way VS Code's semantic-token defaults do, so semantic highlighting
//      is always rendered in the active theme's own colors)
// SPDX-License-Identifier: MIT
//

using System.Collections.Generic;

namespace CodeBrix.Develop.Ide.Themes;

/// <summary>
/// Resolves Roslyn classification types ("class name", "parameter name",
/// "keyword - control", ...) to token styles of the active VS Code theme.
/// Classifications the theme has no rule for resolve to null — the editor
/// then simply keeps the lexical (style-scheme) color, so semantic
/// highlighting can never fight the selected theme.
/// </summary>
public static class SemanticStyleMap
{
    // Roslyn classification type → TextMate candidate scopes, best first.
    // Only classifications that ADD semantic information are listed; purely
    // lexical ones (keyword, string, comment, ...) stay with GtkSourceView.
    static readonly Dictionary<string, string[]> scopeMap = new()
    {
        ["class name"] = new[] { "entity.name.type.class", "entity.name.type", "entity.name.class", "support.class" },
        ["record class name"] = new[] { "entity.name.type.class", "entity.name.type", "entity.name.class", "support.class" },
        ["struct name"] = new[] { "entity.name.type.struct", "entity.name.type", "support.class" },
        ["record struct name"] = new[] { "entity.name.type.struct", "entity.name.type", "support.class" },
        ["interface name"] = new[] { "entity.name.type.interface", "entity.name.type", "support.class" },
        ["enum name"] = new[] { "entity.name.type.enum", "entity.name.type", "support.class" },
        ["delegate name"] = new[] { "entity.name.type.delegate", "entity.name.type", "support.class" },
        ["type parameter name"] = new[] { "entity.name.type.parameter", "entity.name.type" },
        ["namespace name"] = new[] { "entity.name.namespace", "entity.name.type" },
        ["method name"] = new[] { "entity.name.function.member", "entity.name.function", "support.function" },
        ["extension method name"] = new[] { "entity.name.function.extension", "entity.name.function", "support.function" },
        ["property name"] = new[] { "variable.other.property", "variable.other.object.property", "variable" },
        ["field name"] = new[] { "variable.other.field", "variable.other", "variable" },
        ["constant name"] = new[] { "variable.other.constant", "constant.other" },
        ["enum member name"] = new[] { "variable.other.enummember", "constant.other.enum", "constant.other" },
        ["event name"] = new[] { "variable.other.event", "variable.other", "variable" },
        ["local name"] = new[] { "variable.other.local", "variable.other.readwrite", "variable" },
        ["parameter name"] = new[] { "variable.parameter", "variable" },
        ["label name"] = new[] { "entity.name.label" },
        ["keyword - control"] = new[] { "keyword.control" },
        ["string - escape character"] = new[] { "constant.character.escape" },
    };

    /// <summary>
    /// The classification types this map can style (used to create the
    /// editor's semantic text tags).
    /// </summary>
    public static IEnumerable<string> Classifications => scopeMap.Keys;

    /// <summary>
    /// Resolves the theme's token style for a classification, or null when
    /// either the classification is not semantic or the theme does not
    /// color it.
    /// </summary>
    public static VSCodeTheme.TokenStyle? Resolve(VSCodeTheme theme, string classification) =>
        scopeMap.TryGetValue(classification, out var scopes) ? theme.MatchToken(scopes) : null;
}
