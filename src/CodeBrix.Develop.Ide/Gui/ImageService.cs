//
// ImageService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.ImageService and its icon-variant naming
//      scheme, rebuilt on Gdk.Texture for GTK 4)
// SPDX-License-Identifier: MIT
//
// The icon FILES this service loads come unmodified from the MonoDevelop
// project and are assumed LGPL 2.1 — see THIRD-PARTY-NOTICES.txt and
// Assets/MonoDevelopIcons/COPYING.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeBrix.Develop.Core;
using Gdk = CodeBrix.Develop.UI.Gdk;
using GLib = CodeBrix.Develop.UI.GLib;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui;

/// <summary>
/// Resolves logical icon names (e.g. "solution-16") to the best embedded
/// MonoDevelop icon variant for the current theme — following MonoDevelop's
/// file-name convention: name[~dark][~disabled][@2x].png — and loads them
/// as cached <see cref="Gdk.Texture"/> instances.
/// </summary>
public static class ImageService
{
    static readonly Assembly assembly = typeof(ImageService).Assembly;
    static readonly HashSet<string> resourceNames =
        assembly.GetManifestResourceNames().Where(n => n.StartsWith("icons/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    static readonly Dictionary<string, Gdk.Texture?> cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether @2x icon variants are preferred. Set once at startup from the
    /// main window's scale factor.
    /// </summary>
    public static bool HiDpi { get; set; }

    /// <summary>
    /// Returns the texture for a logical icon name, preferring the variant
    /// matching the current theme, or null when the icon does not exist.
    /// </summary>
    public static Gdk.Texture? GetIcon(string name, bool disabled = false)
    {
        var dark = WorkbenchTheme.PrefersDark;
        var key = $"{name}|{(dark ? 'd' : 'l')}|{(disabled ? 'x' : 'o')}|{(HiDpi ? '2' : '1')}";
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var texture = LoadFirstExisting(Candidates(name, dark, disabled));
        cache[key] = texture;
        return texture;
    }

    /// <summary>Creates a 16px-sized <see cref="Gtk.Image"/> for a logical icon name.</summary>
    public static Gtk.Image CreateImage(string name, int pixelSize = 16)
    {
        var image = Gtk.Image.New();
        image.SetPixelSize(pixelSize);
        var texture = GetIcon(name);
        if (texture != null)
            image.SetFromPaintable(texture);
        return image;
    }

    // Most-specific variant first, falling back towards the base file.
    static IEnumerable<string> Candidates(string name, bool dark, bool disabled)
    {
        var suffixes = new List<string>(4);
        if (dark && disabled)
            suffixes.Add("~dark~disabled");
        if (disabled)
            suffixes.Add("~disabled");
        if (dark)
            suffixes.Add("~dark");
        suffixes.Add("");

        foreach (var suffix in suffixes)
        {
            if (HiDpi)
                yield return $"icons/{name}{suffix}@2x.png";
            yield return $"icons/{name}{suffix}.png";
        }
    }

    static Gdk.Texture? LoadFirstExisting(IEnumerable<string> candidates)
    {
        foreach (var resourceName in candidates)
        {
            if (!resourceNames.Contains(resourceName))
                continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    continue;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return Gdk.Texture.NewFromBytes(GLib.Bytes.New(memory.ToArray()));
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"Could not load icon resource {resourceName}: {ex.Message}");
            }
        }
        return null;
    }
}
