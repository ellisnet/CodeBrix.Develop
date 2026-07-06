//
// TypeSystemService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.TypeSystem.TypeSystemService, rebuilt on
//      Roslyn's MSBuildWorkspace for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace CodeBrix.Develop.Core.TypeSystem;

/// <summary>
/// Hosts the Roslyn workspace for the currently loaded solution and exposes
/// C# language services (code completion, diagnostics) to the IDE.
/// </summary>
public static class TypeSystemService
{
    static MSBuildWorkspace workspace;
    static readonly SemaphoreSlim loadLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Locates the installed .NET SDK's MSBuild. Must run before any
    /// Microsoft.Build assembly is loaded, i.e. at application startup.
    /// </summary>
    public static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            var instance = MSBuildLocator.RegisterDefaults();
            LoggingService.LogInfo($"Roslyn type system using MSBuild from {instance.Name} {instance.Version}");
        }
    }

    /// <summary>Whether a Roslyn workspace with at least one project is available.</summary>
    public static bool IsWorkspaceLoaded => workspace != null && workspace.CurrentSolution.ProjectIds.Count > 0;

    /// <summary>
    /// Loads all projects of the given solution into a fresh Roslyn
    /// workspace. Runs design-time builds, so it can take a while; call it
    /// from a background continuation and report progress via
    /// <paramref name="progress"/>.
    /// </summary>
    public static async Task LoadSolutionAsync(Projects.Solution solution, IProgress<string> progress = null, CancellationToken cancellationToken = default)
    {
        await loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            workspace?.Dispose();
            workspace = MSBuildWorkspace.Create();
            workspace.SkipUnrecognizedProjects = true;
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                    LoggingService.LogWarning($"Roslyn workspace: {e.Diagnostic.Message}");
            });

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (workspace.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, project.FileName, StringComparison.Ordinal)))
                    continue;
                progress?.Report($"Loading {project.Name} into the type system...");
                await workspace.OpenProjectAsync(project.FileName, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            progress?.Report($"Type system ready ({workspace.CurrentSolution.ProjectIds.Count} projects).");
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>Closes the current workspace, if any.</summary>
    public static void UnloadSolution()
    {
        workspace?.Dispose();
        workspace = null;
    }

    static Document GetDocumentWithText(FilePath file, string bufferText)
    {
        if (workspace == null)
            return null;

        var documentId = workspace.CurrentSolution
            .GetDocumentIdsWithFilePath(file.FullPath)
            .FirstOrDefault();
        if (documentId == null)
            return null;

        return workspace.CurrentSolution
            .WithDocumentText(documentId, SourceText.From(bufferText))
            .GetDocument(documentId);
    }

    /// <summary>
    /// Computes code-completion items for the given file at the given UTF-16
    /// offset, using <paramref name="bufferText"/> as the current (possibly
    /// unsaved) document text. Returns an empty list when the file is not
    /// part of the loaded solution.
    /// </summary>
    public static async Task<IReadOnlyList<CodeCompletionItem>> GetCompletionsAsync(FilePath file, string bufferText, int offset, CancellationToken cancellationToken = default)
    {
        var document = GetDocumentWithText(file, bufferText);
        if (document == null)
            return Array.Empty<CodeCompletionItem>();

        var completionService = CompletionService.GetService(document);
        if (completionService == null)
            return Array.Empty<CodeCompletionItem>();

        var completions = await completionService.GetCompletionsAsync(document, offset, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completions.ItemsList.Count == 0)
            return Array.Empty<CodeCompletionItem>();

        var results = new List<CodeCompletionItem>(completions.ItemsList.Count);
        foreach (var item in completions.ItemsList)
        {
            results.Add(new CodeCompletionItem
            {
                DisplayText = item.DisplayText,
                SortText = item.SortText,
                Tags = item.Tags,
                ReplacementStart = item.Span.Start,
                ReplacementLength = item.Span.Length,
                InsertionText = item.Properties.TryGetValue("InsertionText", out var insertion)
                    ? insertion
                    : item.DisplayText,
            });
        }
        results.Sort((a, b) => string.CompareOrdinal(a.SortText, b.SortText));
        return results;
    }

    /// <summary>
    /// Computes compiler diagnostics for the given file, using
    /// <paramref name="bufferText"/> as the current document text.
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(FilePath file, string bufferText, CancellationToken cancellationToken = default)
    {
        var document = GetDocumentWithText(file, bufferText);
        if (document == null)
            return Array.Empty<Diagnostic>();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel == null)
            return Array.Empty<Diagnostic>();
        return semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
    }
}

/// <summary>
/// A single code-completion suggestion, decoupled from Roslyn types so UI
/// code does not need to reference Microsoft.CodeAnalysis.
/// </summary>
public class CodeCompletionItem
{
    /// <summary>The text shown in the completion list.</summary>
    public string DisplayText { get; set; }

    /// <summary>The text used for ordering the list.</summary>
    public string SortText { get; set; }

    /// <summary>The text inserted when the item is committed.</summary>
    public string InsertionText { get; set; }

    /// <summary>Roslyn tags describing the item kind (Class, Method, Keyword, ...).</summary>
    public IReadOnlyList<string> Tags { get; set; }

    /// <summary>UTF-16 offset of the text span the completion replaces.</summary>
    public int ReplacementStart { get; set; }

    /// <summary>UTF-16 length of the text span the completion replaces.</summary>
    public int ReplacementLength { get; set; }
}
