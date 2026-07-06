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

    ApplicationFontInfo(ApplicationFont font, string displayName, string packageId,
        string fontFamilyValue, string resourceKey)
    {
        Font = font;
        DisplayName = displayName;
        PackageId = packageId;
        FontFamilyValue = fontFamilyValue;
        ResourceKey = resourceKey;
    }

    /// <summary>All fonts, in the order they are offered to the user.</summary>
    public static IReadOnlyList<ApplicationFontInfo> All { get; } = new[]
    {
        new ApplicationFontInfo(ApplicationFont.OpenSans, "Open Sans",
            "CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever",
            "ms-appx:///CodeBrix.Platform.Fonts.OpenSans/Fonts/OpenSans.ttf",
            "OpenSansFont"),
        new ApplicationFontInfo(ApplicationFont.Roboto, "Roboto",
            "CodeBrix.Platform.Fonts.Roboto.OflLicenseForever",
            "ms-appx:///CodeBrix.Platform.Fonts.Roboto/Fonts/Roboto.ttf#Roboto",
            "RobotoFont"),
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
