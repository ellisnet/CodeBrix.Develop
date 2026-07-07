//
// XamlLanguageService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.TypeSystem;
using Microsoft.CodeAnalysis;

namespace CodeBrix.Develop.Core.Xaml;

/// <summary>
/// The IDE-facing entry point for XAML editing intelligence: resolves a
/// .xaml file to its project's <see cref="XamlMetadataIndex"/> (cached per
/// project) and exposes validation and completion over it.
/// </summary>
public static class XamlLanguageService
{
    static readonly ConcurrentDictionary<ProjectId, Task<XamlMetadataIndex>> indexes =
        new ConcurrentDictionary<ProjectId, Task<XamlMetadataIndex>>();

    static XamlLanguageService()
    {
        TypeSystemService.WorkspaceUnloaded += () =>
        {
            indexes.Clear();
            XamlResourceKeyCache.Clear();
        };
    }

    /// <summary>Whether XAML intelligence can run (a Roslyn workspace is loaded).</summary>
    public static bool IsAvailable => TypeSystemService.IsWorkspaceLoaded;

    /// <summary>
    /// Computes live diagnostics (parse errors, unknown elements/properties,
    /// invalid enum values) for the given XAML document text.
    /// </summary>
    public static async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(FilePath file, string bufferText, CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(file, cancellationToken).ConfigureAwait(false);
        if (index == null)
            return Array.Empty<DiagnosticInfo>();
        return XamlValidator.Validate(bufferText, index, cancellationToken);
    }

    /// <summary>
    /// Computes completion items at the given UTF-16 offset. Returns an
    /// empty list when the position does not support completion.
    /// </summary>
    public static async Task<IReadOnlyList<CodeCompletionItem>> GetCompletionsAsync(
        FilePath file, string bufferText, int offset, XamlCompletionReason reason, char typedChar,
        CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(file, cancellationToken).ConfigureAwait(false);
        if (index == null)
            return Array.Empty<CodeCompletionItem>();
        var resourceKeys = XamlResourceKeyCache.GetKeys(ProjectDirectories());
        return XamlCompletionService.GetCompletions(
            bufferText, offset, index, reason, typedChar, resourceKeys, cancellationToken);
    }

    static IEnumerable<string> ProjectDirectories()
    {
        var solution = TypeSystemService.CurrentRoslynSolution;
        if (solution == null)
            return Array.Empty<string>();
        return solution.Projects
            .Select(p => p.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.Ordinal);
    }

    static Task<XamlMetadataIndex> GetIndexAsync(FilePath xamlFile, CancellationToken cancellationToken)
    {
        var solution = TypeSystemService.CurrentRoslynSolution;
        if (solution == null)
            return Task.FromResult<XamlMetadataIndex>(null);

        var project = FindProject(solution, xamlFile);
        if (project == null)
            return Task.FromResult<XamlMetadataIndex>(null);

        return indexes.GetOrAdd(project.Id, _ => BuildIndexAsync(project));

        static async Task<XamlMetadataIndex> BuildIndexAsync(Project project)
        {
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation == null)
                return null;
            var index = XamlMetadataIndex.Build(compilation);
            LoggingService.LogInfo($"XAML metadata index built for {project.Name} "
                + $"({(index.IsXamlPlatformAvailable ? index.ElementNames.Count + " element types" : "no XAML platform referenced")})");
            return index;
        }
    }

    // A .xaml file is not itself a Roslyn document; its code-behind
    // ("Page.xaml.cs") is. Fall back to the project whose folder contains
    // the file most specifically.
    static Project FindProject(Solution solution, FilePath xamlFile)
    {
        var codeBehindIds = solution.GetDocumentIdsWithFilePath(xamlFile.FullPath + ".cs");
        if (codeBehindIds.Length > 0)
            return solution.GetProject(codeBehindIds[0].ProjectId);

        Project best = null;
        var bestLength = -1;
        foreach (var project in solution.Projects)
        {
            if (string.IsNullOrEmpty(project.FilePath))
                continue;
            var directory = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(directory))
                continue;
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directory
                : directory + Path.DirectorySeparatorChar;
            if (xamlFile.FullPath.ToString().StartsWith(prefix, StringComparison.Ordinal)
                && prefix.Length > bestLength)
            {
                best = project;
                bestLength = prefix.Length;
            }
        }
        return best;
    }
}
