//
// ApplicationFont.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// The fonts a generated CodeBrix.Platform application can use as its
/// default text font, in the order they are offered to the user.
/// </summary>
public enum ApplicationFont
{
    /// <summary>Open Sans (the default).</summary>
    OpenSans,
    /// <summary>Roboto.</summary>
    Roboto,
}

/// <summary>
/// The per-font facts that drive project generation: which font package the
/// .Core project references and how the XAML and code reference the font.
/// </summary>
public sealed class ApplicationFontInfo
{
    /// <summary>The font this describes.</summary>
    public ApplicationFont Font { get; }

    /// <summary>The label shown to the user (e.g. "Open Sans").</summary>
    public string DisplayName { get; }

    /// <summary>The font package the generated .Core project references.</summary>
    public string PackageId { get; }

    /// <summary>The ms-appx FontFamily value that loads the packaged .ttf.</summary>
    public string FontFamilyValue { get; }

    /// <summary>The App.xaml resource key the views reference (e.g. "OpenSansFont").</summary>
    public string ResourceKey { get; }

    /// <summary>
    /// The package version written when this font is chosen, or null for the
    /// default font — whose version comes from the template archive. Every
    /// font added here other than the default MUST supply one: choosing a
    /// non-default font swaps the package id in the template's text, but the
    /// template only ever carries the default font's version.
    /// </summary>
    public string FallbackVersion { get; }

    ApplicationFontInfo(ApplicationFont font, string displayName, string packageId,
        string fontFamilyValue, string resourceKey, string fallbackVersion)
    {
        Font = font;
        DisplayName = displayName;
        PackageId = packageId;
        FontFamilyValue = fontFamilyValue;
        ResourceKey = resourceKey;
        FallbackVersion = fallbackVersion;
    }

    /// <summary>The default font — the one the template archive is built around.</summary>
    public const ApplicationFont DefaultFont = ApplicationFont.OpenSans;

    /// <summary>All fonts, in the order they are offered to the user.</summary>
    public static IReadOnlyList<ApplicationFontInfo> All { get; } = new[]
    {
        new ApplicationFontInfo(ApplicationFont.OpenSans, "Open Sans",
            "CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever",
            "ms-appx:///CodeBrix.Platform.Fonts.OpenSans/Fonts/OpenSans.ttf",
            "OpenSansFont",
            // The default font: its version comes from the template archive.
            null),
        new ApplicationFontInfo(ApplicationFont.Roboto, "Roboto",
            "CodeBrix.Platform.Fonts.Roboto.OflLicenseForever",
            "ms-appx:///CodeBrix.Platform.Fonts.Roboto/Fonts/Roboto.ttf#Roboto",
            "RobotoFont",
            "1.0.181.661"),
    };

    /// <summary>Returns the info record for the given font.</summary>
    public static ApplicationFontInfo Get(ApplicationFont font)
    {
        foreach (var info in All)
        {
            if (info.Font == font)
                return info;
        }
        throw new ArgumentOutOfRangeException(nameof(font), font, "Unknown application font");
    }
}
