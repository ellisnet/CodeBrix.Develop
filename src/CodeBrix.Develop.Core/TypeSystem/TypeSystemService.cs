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
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
                // Shared projects (.shproj) are not MSBuild-loadable; their
                // files reach Roslyn through each head project that imports
                // the sibling .projitems.
                if (project.IsSharedProject)
                    continue;
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
        WorkspaceUnloaded?.Invoke();
    }

    /// <summary>Raised when the current workspace is unloaded (caches must drop).</summary>
    public static event Action WorkspaceUnloaded;

    /// <summary>The current Roslyn solution snapshot, or null.</summary>
    internal static Microsoft.CodeAnalysis.Solution CurrentRoslynSolution => workspace?.CurrentSolution;

    /// <summary>
    /// Whether the given 1-based line of a C# file holds code the debugger
    /// can bind a breakpoint to: a statement, a block brace, a member
    /// initializer, or the header of a method-like member with a body (its
    /// entry point). Blank lines, comment-only lines, and declaration-only
    /// lines (usings, namespace and type headers, bodiless members) are not
    /// breakable. Non-C# files are permissively considered breakable — the
    /// debugger has the final say at run time. Purely syntactic: works even
    /// while the workspace is still loading.
    /// </summary>
    public static bool IsBreakableLine(FilePath file, string bufferText, int line)
    {
        if (!file.HasExtension(".cs"))
            return true;
        var text = SourceText.From(bufferText);
        if (line < 1 || line > text.Lines.Count)
            return false;
        var lineSpan = text.Lines[line - 1].Span;
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        // DescendantTokens matches by FULL span, so a token whose leading
        // trivia is on this line is returned too — re-check the token span.
        foreach (var token in root.DescendantTokens(lineSpan))
        {
            if (token.Span.Length == 0 || !token.Span.IntersectsWith(lineSpan))
                continue;
            if (IsBreakableToken(token))
                return true;
        }
        return false;
    }

    static bool IsBreakableToken(SyntaxToken token)
    {
        for (var node = token.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                // Statements cover block braces and local functions too; the
                // clauses cover field/property initializers, expression
                // bodies, and this()/base() constructor initializers.
                case StatementSyntax:
                case ArrowExpressionClauseSyntax:
                case EqualsValueClauseSyntax:
                case ConstructorInitializerSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return true;
                // A method-like header is the member's entry breakpoint —
                // but only when there is a body to enter.
                case AccessorDeclarationSyntax accessor when accessor.Body != null || accessor.ExpressionBody != null:
                case BaseMethodDeclarationSyntax method when method.Body != null || method.ExpressionBody != null:
                    return true;
            }
        }
        return false;
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
                FilterText = item.FilterText,
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
    /// Whether typing <paramref name="typedChar"/> (whose caret now sits at
    /// UTF-16 offset <paramref name="caretOffset"/>, just after the char)
    /// should automatically open code completion.
    /// </summary>
    public static bool ShouldTriggerCompletion(FilePath file, string bufferText, int caretOffset, char typedChar)
    {
        var document = GetDocumentWithText(file, bufferText);
        if (document == null)
            return false;
        var completionService = CompletionService.GetService(document);
        if (completionService == null)
            return false;
        var trigger = CompletionTrigger.CreateInsertionTrigger(typedChar);
        return completionService.ShouldTriggerCompletion(SourceText.From(bufferText), caretOffset, trigger);
    }

    /// <summary>
    /// Computes the classified (syntax + semantic) spans of the whole
    /// document, used for semantic highlighting in the editor.
    /// </summary>
    public static async Task<IReadOnlyList<ClassifiedSpanInfo>> GetClassifiedSpansAsync(FilePath file, string bufferText, CancellationToken cancellationToken = default)
    {
        var document = GetDocumentWithText(file, bufferText);
        if (document == null)
            return Array.Empty<ClassifiedSpanInfo>();

        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, bufferText.Length), cancellationToken).ConfigureAwait(false);
        var results = new List<ClassifiedSpanInfo>();
        foreach (var span in spans)
        {
            results.Add(new ClassifiedSpanInfo
            {
                Start = span.TextSpan.Start,
                Length = span.TextSpan.Length,
                Classification = span.ClassificationType,
            });
        }
        return results;
    }

    /// <summary>
    /// Computes signature help (parameter hints) at the given UTF-16
    /// offset, or null when the caret is not inside an argument list.
    /// </summary>
    public static async Task<SignatureHelpResult> GetSignatureHelpAsync(FilePath file, string bufferText, int offset, CancellationToken cancellationToken = default)
    {
        var document = GetDocumentWithText(file, bufferText);
        if (document == null)
            return null;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel == null)
            return null;
        return SignatureHelpService.Compute(semanticModel, offset, cancellationToken);
    }

    /// <summary>
    /// Computes compiler diagnostics for the given file, using
    /// <paramref name="bufferText"/> as the current document text.
    /// Hidden diagnostics are omitted.
    /// </summary>
    public static async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(FilePath file, string bufferText, CancellationToken cancellationToken = default)
    {
        var document = GetDocumentWithText(file, bufferText);
        if (document == null)
            return Array.Empty<DiagnosticInfo>();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel == null)
            return Array.Empty<DiagnosticInfo>();

        var results = new List<DiagnosticInfo>();
        foreach (var diagnostic in semanticModel.GetDiagnostics(cancellationToken: cancellationToken))
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden)
                continue;
            results.Add(new DiagnosticInfo
            {
                Id = diagnostic.Id,
                Message = diagnostic.GetMessage(),
                Severity = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => DiagnosticInfoSeverity.Error,
                    DiagnosticSeverity.Warning => DiagnosticInfoSeverity.Warning,
                    _ => DiagnosticInfoSeverity.Info,
                },
                Start = diagnostic.Location.SourceSpan.Start,
                Length = diagnostic.Location.SourceSpan.Length,
            });
        }
        return results;
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

    /// <summary>The text the typed prefix is matched against (defaults to <see cref="DisplayText"/>).</summary>
    public string FilterText { get; set; }

    /// <summary>
    /// How many characters to move the caret back after inserting
    /// <see cref="InsertionText"/> (e.g. 1 for <c>Property=""</c>).
    /// </summary>
    public int CaretBack { get; set; }

    /// <summary>The text inserted when the item is committed.</summary>
    public string InsertionText { get; set; }

    /// <summary>Roslyn tags describing the item kind (Class, Method, Keyword, ...).</summary>
    public IReadOnlyList<string> Tags { get; set; }

    /// <summary>UTF-16 offset of the text span the completion replaces.</summary>
    public int ReplacementStart { get; set; }

    /// <summary>UTF-16 length of the text span the completion replaces.</summary>
    public int ReplacementLength { get; set; }
}
