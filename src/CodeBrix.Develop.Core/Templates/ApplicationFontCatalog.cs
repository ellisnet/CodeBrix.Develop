//
// ApplicationFontCatalog.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// One font a new application can be generated with, as the IDE knows it before
/// anything is fetched: a label to show and the NuGet package to fetch. Every
/// other fact about the font — how to reference it, what its companion fonts
/// are, which keyboard layouts it covers — comes from the package's own
/// <see cref="FontDescriptor"/>.
/// </summary>
public sealed class ApplicationFontChoice
{
    /// <summary>The label shown in the New Application dialog.</summary>
    public string DisplayName { get; }

    /// <summary>The NuGet package id, exact — used to resolve the descriptor.</summary>
    public string PackageId { get; }

    internal ApplicationFontChoice(string displayName, string packageId)
    {
        DisplayName = displayName;
        PackageId = packageId;
    }
}

/// <summary>
/// The fonts offered by the "New CodeBrix.Platform Application" experience.
/// <para>
/// This list is deliberately the ONLY per-font knowledge in CodeBrix.Develop.
/// It is a curated list rather than a nuget.org prefix query so the dialog can
/// render instantly and offline — a query would have to complete before the
/// font dropdown could be populated, and would silently shrink to nothing when
/// the network is down instead of saying so.
/// </para>
/// <para>
/// Adding a font is adding a row here. The display name is a label only; the
/// descriptor's own displayName is authoritative for anything written into
/// generated source, so a stale label cannot corrupt a generated application.
/// </para>
/// </summary>
public static class ApplicationFontCatalog
{
    /// <summary>
    /// The package the template archive itself is built around. Choosing this
    /// font means no swap at all — the template already says exactly this.
    /// </summary>
    public const string TemplatePackageId = "CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever";

    /// <summary>The fonts offered, in the order they appear to the user.</summary>
    public static IReadOnlyList<ApplicationFontChoice> All { get; } = new[]
    {
        new ApplicationFontChoice("Open Sans", TemplatePackageId),
        new ApplicationFontChoice("Roboto", "CodeBrix.Platform.Fonts.Roboto.OflLicenseForever"),
        new ApplicationFontChoice("Merriweather", "CodeBrix.Platform.Fonts.Merriweather.OflLicenseForever"),
    };

    /// <summary>The default: the font the template archive is built around.</summary>
    public static ApplicationFontChoice Default =>
        All.First(choice => IsTemplateFont(choice.PackageId));

    /// <summary>The choice at a dropdown index, clamped into range.</summary>
    public static ApplicationFontChoice FromIndex(int index) =>
        All[Math.Clamp(index, 0, All.Count - 1)];

    /// <summary>
    /// Whether the package is the one the template is built around — in which
    /// case generation performs no font swap.
    /// </summary>
    public static bool IsTemplateFont(string packageId) =>
        string.Equals(packageId, TemplatePackageId, StringComparison.OrdinalIgnoreCase);
}
