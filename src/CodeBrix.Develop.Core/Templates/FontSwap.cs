//
// FontSwap.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// The font substitution a generated application needs: replace everything the
/// template says about its own font with what the chosen font's package says
/// about itself.
/// <para>
/// Both sides are <see cref="FontDescriptor"/>s read from the font packages
/// themselves, which is what keeps per-font knowledge out of CodeBrix.Develop.
/// The template is built around one font; choosing that same font needs no swap
/// at all, and <see cref="FontSwap.For"/> returns null for it.
/// </para>
/// </summary>
public sealed class FontSwap
{
    /// <summary>What the template currently says (the template's own font).</summary>
    public FontDescriptor From { get; }

    /// <summary>What the generated application should say instead.</summary>
    public FontDescriptor To { get; }

    FontSwap(FontDescriptor from, FontDescriptor to)
    {
        From = from;
        To = to;
    }

    /// <summary>
    /// The swap from the template's font to the chosen one, or null when they
    /// are the same font and the template already says the right thing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The two descriptors collide on a value the swap replaces by text, which
    /// would corrupt the generated application.
    /// </exception>
    public static FontSwap For(FontDescriptor templateFont, FontDescriptor chosenFont)
    {
        ArgumentNullException.ThrowIfNull(templateFont);
        ArgumentNullException.ThrowIfNull(chosenFont);

        if (string.Equals(templateFont.PackageId, chosenFont.PackageId, StringComparison.OrdinalIgnoreCase))
            return null;

        // These four values are replaced as raw text across the generated
        // files. If the chosen font reuses one of the template font's, the
        // replacement is either a no-op or self-referential — either way the
        // generated application would be subtly wrong, and a clear failure now
        // beats an application that almost works.
        var collisions = new List<string>();
        if (string.Equals(templateFont.ResourceKey, chosenFont.ResourceKey, StringComparison.Ordinal))
            collisions.Add($"resourceKey \"{chosenFont.ResourceKey}\"");
        if (string.Equals(templateFont.DisplayName, chosenFont.DisplayName, StringComparison.Ordinal))
            collisions.Add($"displayName \"{chosenFont.DisplayName}\"");
        if (string.Equals(templateFont.FontFamilyUri, chosenFont.FontFamilyUri, StringComparison.Ordinal))
            collisions.Add($"fontFamilyUri \"{chosenFont.FontFamilyUri}\"");
        if (collisions.Count > 0)
            throw new InvalidOperationException(
                $"The {chosenFont.DisplayName} font package declares the same " +
                $"{string.Join(", ", collisions)} as the template's own font " +
                $"({templateFont.DisplayName}), so it cannot be swapped in.");

        return new FontSwap(templateFont, chosenFont);
    }

    /// <summary>
    /// Applies the swap to one generated file's text. Replaces the package id,
    /// the font URI, the App.xaml resource key and the display name, and — for
    /// a font that ships companion faces — registers those companions next to
    /// the default-font assignment in App.xaml.cs.
    /// </summary>
    public string Apply(string text, string relativePath)
    {
        var swapped = text
            .Replace(From.PackageId, To.PackageId)
            .Replace(From.FontFamilyUri, To.FontFamilyUri)
            .Replace(From.ResourceKey, To.ResourceKey)
            .Replace(From.DisplayName, To.DisplayName);

        return IsApplicationCodeBehind(relativePath)
            ? InsertFallbackRegistration(swapped)
            : swapped;
    }

    // The generated App.xaml.cs, wherever the application name puts it.
    static bool IsApplicationCodeBehind(string relativePath) =>
        relativePath.EndsWith("App.xaml.cs", StringComparison.OrdinalIgnoreCase);

    // Adds the companion-font registration immediately after the template's
    // DefaultTextFontFamily assignment. Anchoring on that statement means the
    // template needs no placeholder — it stays a working sample that compiles
    // as-is — and a font with no companions leaves the file untouched.
    string InsertFallbackRegistration(string text)
    {
        if (To.FallbackFontUris.Count == 0)
            return text;

        const string anchor = "FeatureConfiguration.Font.DefaultTextFontFamily";
        var anchorIndex = text.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
            return text;

        var statementEnd = text.IndexOf(';', anchorIndex);
        if (statementEnd < 0)
            return text;

        var lineEnd = text.IndexOf('\n', statementEnd);
        var insertAt = lineEnd < 0 ? statementEnd + 1 : lineEnd + 1;

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = IndentOfLineContaining(text, anchorIndex);

        var registration = new System.Text.StringBuilder();
        registration.Append(newLine);
        registration.Append(indent).Append("//Fonts consulted for characters the default font has no glyph for").Append(newLine);
        registration.Append(indent).Append("global::CodeBrix.Platform.UI.FeatureConfiguration.Font.FallbackFontFamilies =").Append(newLine);
        registration.Append(indent).Append('[').Append(newLine);
        foreach (var uri in To.FallbackFontUris)
        {
            registration.Append(indent).Append("    \"").Append(uri).Append("\",").Append(newLine);
        }
        registration.Append(indent).Append("];").Append(newLine);

        return text.Insert(insertAt, registration.ToString());
    }

    static string IndentOfLineContaining(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Min(index, text.Length - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var end = lineStart;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
        {
            end++;
        }
        return text.Substring(lineStart, end - lineStart);
    }
}
