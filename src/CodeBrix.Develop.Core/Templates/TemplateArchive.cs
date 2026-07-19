//
// TemplateArchive.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Supplies the TemplateApp.zip archive that new-application generation
/// extracts, and locates the on-disk templates folder the background updater
/// writes to. The active archive is the on-disk baseline
/// templates/TemplateApp.zip, superseded by the downloaded copy under
/// templates/updated/ when <see cref="TemplateUpdateState.UseUpdatedCopy"/> is
/// set and that copy is a valid archive (otherwise the state reverts to the
/// baseline). There is no embedded copy: the on-disk baseline is the single
/// source of truth.
/// </summary>
public static class TemplateArchive
{
    /// <summary>
    /// The application-name token baked into the template's folder names,
    /// file names, and file contents; replaced with the chosen app name.
    /// </summary>
    public const string TemplateToken = "TemplateApp";

    /// <summary>The template archive file name.</summary>
    public const string ArchiveFileName = "TemplateApp.zip";

    /// <summary>The folder (relative to the templates folder) holding the downloaded copy.</summary>
    public const string UpdatedFolderName = "updated";

    /// <summary>
    /// Walks up from the running executable's folder until it finds a
    /// "templates" folder containing TemplateApp.zip, and returns that folder;
    /// null when none is found (for example when run outside the repo). This
    /// is the folder the updater writes templates/updated/ into.
    /// </summary>
    public static string FindTemplatesDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, "templates");
            if (File.Exists(Path.Combine(candidate, ArchiveFileName)))
                return candidate;
            var parent = Directory.GetParent(directory)?.FullName;
            if (parent == null || parent == directory)
                break;
            directory = parent;
        }
        return null;
    }

    /// <summary>The full path of the downloaded updated archive, or null when the templates folder is not found.</summary>
    public static string GetUpdatedArchivePath()
    {
        var templates = FindTemplatesDirectory();
        return templates == null ? null : Path.Combine(templates, UpdatedFolderName, ArchiveFileName);
    }

    /// <summary>
    /// Returns the bytes of the active template archive: the downloaded copy
    /// when it is active and valid, otherwise the on-disk baseline. An
    /// unusable updated copy reverts <see cref="TemplateUpdateState"/> to the
    /// baseline. Throws when the templates folder cannot be located (there is
    /// no embedded fallback).
    /// </summary>
    public static byte[] GetActiveArchiveBytes()
    {
        var templates = FindTemplatesDirectory();
        if (templates == null)
            throw new InvalidOperationException(
                $"Could not locate a 'templates' folder containing {ArchiveFileName} by walking up from {AppContext.BaseDirectory}.");

        if (TemplateUpdateState.UseUpdatedCopy)
        {
            var updatedPath = Path.Combine(templates, UpdatedFolderName, ArchiveFileName);
            if (TryReadValidArchive(updatedPath, out var updatedBytes))
                return updatedBytes;

            LoggingService.LogWarning(
                "The updated template archive is missing or invalid; reverting to the baseline template.");
            TemplateUpdateState.RevertToBaseline();
        }

        return File.ReadAllBytes(Path.Combine(templates, ArchiveFileName));
    }

    /// <summary>
    /// Reads a file and confirms it is a usable template archive (opens as a
    /// zip and contains a .slnx). Returns false — without throwing — when the
    /// file is missing, unreadable, or not a valid archive.
    /// </summary>
    public static bool TryReadValidArchive(string path, out byte[] bytes)
    {
        bytes = null;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            var data = File.ReadAllBytes(path);
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                if (!zip.Entries.Any(entry => entry.FullName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            bytes = data;
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Template archive '{path}' could not be read: {ex.Message}");
            return false;
        }
    }
}
