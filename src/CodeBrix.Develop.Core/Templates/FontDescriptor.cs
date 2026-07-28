//
// FontDescriptor.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// A CodeBrix font package's self-description, read from the
/// <c>CODEBRIX-DEVELOP.json</c> file at the root of its NuGet package.
/// <para>
/// This is what lets CodeBrix.Develop wire ANY font package into a generated
/// application without carrying per-font logic: the package states its own
/// identity and how to reference it, and the IDE swaps one descriptor's values
/// for another's. Adding a font is adding a row to
/// <see cref="ApplicationFontCatalog.All"/> — no code here changes.
/// </para>
/// </summary>
public sealed class FontDescriptor
{
    /// <summary>The only schema version this IDE understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The file name, at the root of the font's NuGet package.</summary>
    public const string FileName = "CODEBRIX-DEVELOP.json";

    /// <summary>The descriptor schema version the font package declared.</summary>
    public int SchemaVersion { get; }

    /// <summary>The NuGet package id this descriptor belongs to.</summary>
    public string PackageId { get; }

    /// <summary>
    /// The typographic family name ("Open Sans"). Authoritative for anything
    /// written into generated source — the catalog's label is only a label.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// The "ms-appx:///" URI of the package's primary font, carrying no
    /// "#FamilyName" fragment.
    /// </summary>
    public string FontFamilyUri { get; }

    /// <summary>The App.xaml resource key a generated application uses ("OpenSansFont").</summary>
    public string ResourceKey { get; }

    /// <summary>
    /// Fonts consulted, in order, for characters the primary font has no glyph
    /// for — the companion faces a package ships to extend its script coverage.
    /// Empty when the package has none.
    /// </summary>
    public IReadOnlyList<string> FallbackFontUris { get; }

    /// <summary>
    /// The software-keyboard layout ids this package's glyph coverage supports.
    /// Ids absent from the list are not supported; there is deliberately no
    /// "unsupported" list, so the complement of the platform's layout set is
    /// always the correct answer.
    /// </summary>
    public IReadOnlyList<string> KeyboardLayouts { get; }

    FontDescriptor(int schemaVersion, string packageId, string displayName, string fontFamilyUri,
        string resourceKey, IReadOnlyList<string> fallbackFontUris, IReadOnlyList<string> keyboardLayouts)
    {
        SchemaVersion = schemaVersion;
        PackageId = packageId;
        DisplayName = displayName;
        FontFamilyUri = fontFamilyUri;
        ResourceKey = resourceKey;
        FallbackFontUris = fallbackFontUris;
        KeyboardLayouts = keyboardLayouts;
    }

    /// <summary>
    /// Parses a descriptor. Throws <see cref="InvalidDataException"/> when the
    /// JSON is malformed, declares a schema version this IDE does not
    /// understand, or omits a field the swap cannot proceed without — a font
    /// package that cannot describe itself is not usable, and saying so beats
    /// generating an application with a half-applied font.
    /// </summary>
    public static FontDescriptor Parse(string json, string sourceDescription = null)
    {
        var where = string.IsNullOrEmpty(sourceDescription) ? FileName : sourceDescription;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{where} is not valid JSON: {ex.Message}", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} must contain a JSON object.");

        var schemaVersion = ReadInt(root, "schemaVersion", where);
        if (schemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException(
                $"{where} declares schemaVersion {schemaVersion}; this version of CodeBrix.Develop " +
                $"understands only schemaVersion {SupportedSchemaVersion}. Update CodeBrix.Develop " +
                $"to use this font package.");

        var fontFamilyUri = ReadString(root, "fontFamilyUri", where);
        if (fontFamilyUri.Contains('#'))
            throw new InvalidDataException(
                $"{where} has a \"#FamilyName\" fragment on fontFamilyUri. CodeBrix.Platform strips " +
                $"the fragment when resolving a font, and it prevents the startup font-manifest " +
                $"preload from finding the manifest, so the value must not carry one.");

        return new FontDescriptor(
            schemaVersion,
            ReadString(root, "packageId", where),
            ReadString(root, "displayName", where),
            fontFamilyUri,
            ReadString(root, "resourceKey", where),
            ReadStringArray(root, "fallbackFontUris"),
            ReadStringArray(root, "keyboardLayouts"));
    }

    /// <summary>Reads and parses a descriptor from a file on disk.</summary>
    public static FontDescriptor Load(string path) =>
        Parse(File.ReadAllText(path), path);

    static int ReadInt(JsonElement root, string name, string where)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException($"{where} is missing the numeric \"{name}\" property.");
        return value.GetInt32();
    }

    static string ReadString(JsonElement root, string name, string where)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{where} is missing the \"{name}\" property.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"{where} has an empty \"{name}\" property.");
        return text;
    }

    // Optional by design: a package with no companion fonts simply omits
    // fallbackFontUris, and absent means empty rather than malformed.
    static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
    }
}
