//
// EditorAnnotations.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (semantic highlighting + live diagnostic squiggles for the editor,
//      layered as GtkTextTags over GtkSourceView's lexical highlighting;
//      all colors derive from the active VS Code theme)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.TypeSystem;
using CodeBrix.Develop.Core.Xaml;
using CodeBrix.Develop.Ide.Themes;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;
using GtkSource = CodeBrix.Develop.UI.GtkSource;
using Pango = CodeBrix.Develop.UI.Pango;

namespace CodeBrix.Develop.Ide.Gui.Documents;

/// <summary>
/// Owns the semantic-highlighting and diagnostic-squiggle text tags of one
/// editor document. Refreshes are debounced, computed off the UI thread,
/// and dropped when the buffer changed meanwhile. Tag colors are resolved
/// from the active theme's own token colors, so they always match it.
/// </summary>
public class EditorAnnotations
{
    const int RefreshDelayMs = 500;
    const int WorkspaceRetryDelayMs = 2000;

    readonly GtkSource.Buffer buffer;
    readonly FilePath fileName;
    readonly bool isCSharp;
    readonly bool isXaml;
    readonly Func<string> getText;
    readonly SynchronizationContext? uiContext;

    readonly Dictionary<string, Gtk.TextTag> semanticTags = new(StringComparer.Ordinal);
    Gtk.TextTag? errorTag;
    Gtk.TextTag? warningTag;
    Gtk.TextTag? infoTag;

    // Diagnostics currently applied, with buffer (code point) offsets, for hover lookup.
    readonly List<(int Start, int End, DiagnosticInfo Diagnostic)> appliedDiagnostics = new();

    CancellationTokenSource? refreshCancellation;
    int version;

    /// <summary>Creates the annotation layer for one document.</summary>
    public EditorAnnotations(GtkSource.Buffer buffer, FilePath fileName, bool isCSharp, bool isXaml, Func<string> getText)
    {
        this.buffer = buffer;
        this.fileName = fileName;
        this.isCSharp = isCSharp;
        this.isXaml = isXaml;
        this.getText = getText;
        uiContext = SynchronizationContext.Current;
        CreateTags();
    }

    /// <summary>Whether this document gets any annotations at all.</summary>
    public bool IsActive => isCSharp || isXaml;

    /// <summary>
    /// Schedules a debounced refresh of semantic highlighting and
    /// diagnostics; call on every buffer change (and once after loading).
    /// </summary>
    public void ScheduleRefresh()
    {
        if (!IsActive)
            return;
        version++;
        refreshCancellation?.Cancel();
        refreshCancellation = new CancellationTokenSource();
        _ = RefreshAsync(version, refreshCancellation.Token);
    }

    /// <summary>Re-derives all tag colors from the (changed) theme and re-applies.</summary>
    public void OnThemeChanged()
    {
        if (!IsActive)
            return;
        RemoveTags();
        CreateTags();
        ScheduleRefresh();
    }

    /// <summary>
    /// The diagnostic message at the given buffer (code point) offset, or
    /// null — used for the hover bubble.
    /// </summary>
    public string? GetDiagnosticMessageAt(int charOffset)
    {
        foreach (var (start, end, diagnostic) in appliedDiagnostics)
        {
            if (charOffset >= start && charOffset < Math.Max(end, start + 1))
                return diagnostic.Id is { Length: > 0 }
                    ? $"{diagnostic.Id}: {diagnostic.Message}"
                    : diagnostic.Message;
        }
        return null;
    }

    async Task RefreshAsync(int requestVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RefreshDelayMs, cancellationToken).ConfigureAwait(false);

            // The type system loads in the background after the solution
            // opens; retry quietly until it is ready.
            while (!TypeSystemService.IsWorkspaceLoaded)
                await Task.Delay(WorkspaceRetryDelayMs, cancellationToken).ConfigureAwait(false);

            var text = "";
            var fetched = new TaskCompletionSource<string>();
            Post(_ => fetched.SetResult(getText()));
            text = await fetched.Task.ConfigureAwait(false);

            IReadOnlyList<ClassifiedSpanInfo> classified = Array.Empty<ClassifiedSpanInfo>();
            IReadOnlyList<DiagnosticInfo> diagnostics;
            if (isCSharp)
            {
                classified = await TypeSystemService.GetClassifiedSpansAsync(fileName, text, cancellationToken).ConfigureAwait(false);
                diagnostics = await TypeSystemService.GetDiagnosticsAsync(fileName, text, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                diagnostics = await XamlLanguageService.GetDiagnosticsAsync(fileName, text, cancellationToken).ConfigureAwait(false);
            }

            Post(_ =>
            {
                if (requestVersion != version)
                    return; // the buffer changed again; a newer refresh is queued
                Apply(text, classified, diagnostics);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Annotation refresh for {fileName.FileName} failed", ex);
        }
    }

    void Post(SendOrPostCallback callback)
    {
        if (uiContext != null)
            uiContext.Post(callback, null);
        else
            callback(null);
    }

    void Apply(string text, IReadOnlyList<ClassifiedSpanInfo> classified, IReadOnlyList<DiagnosticInfo> diagnostics)
    {
        // One pass over the text converts every needed UTF-16 offset to a
        // buffer (code point) offset; surrogate pairs make them diverge.
        var offsets = new SortedSet<int>();
        foreach (var span in classified)
        {
            if (semanticTags.ContainsKey(span.Classification))
            {
                offsets.Add(span.Start);
                offsets.Add(span.Start + span.Length);
            }
        }
        foreach (var diagnostic in diagnostics)
        {
            offsets.Add(diagnostic.Start);
            offsets.Add(diagnostic.Start + diagnostic.Length);
        }
        var map = MapUtf16ToCharOffsets(text, offsets);

        buffer.GetBounds(out var bufferStart, out var bufferEnd);
        foreach (var tag in semanticTags.Values)
            buffer.RemoveTag(tag, bufferStart, bufferEnd);
        if (errorTag != null)
            buffer.RemoveTag(errorTag, bufferStart, bufferEnd);
        if (warningTag != null)
            buffer.RemoveTag(warningTag, bufferStart, bufferEnd);
        if (infoTag != null)
            buffer.RemoveTag(infoTag, bufferStart, bufferEnd);
        appliedDiagnostics.Clear();

        // GtkSourceView's own syntax tags are created lazily as regions get
        // highlighted, which can outrank ours; keep our layers on top
        // (semantic first, squiggles above).
        var tagTable = buffer.GetTagTable();
        foreach (var tag in semanticTags.Values)
            tag.SetPriority(tagTable.GetSize() - 1);
        foreach (var tag in new[] { infoTag, warningTag, errorTag })
            tag?.SetPriority(tagTable.GetSize() - 1);

        foreach (var span in classified)
        {
            if (!semanticTags.TryGetValue(span.Classification, out var tag))
                continue;
            if (!map.TryGetValue(span.Start, out var start) || !map.TryGetValue(span.Start + span.Length, out var end))
                continue;
            ApplyTag(tag, start, end);
        }

        foreach (var diagnostic in diagnostics)
        {
            var tag = diagnostic.Severity switch
            {
                DiagnosticInfoSeverity.Error => errorTag,
                DiagnosticInfoSeverity.Warning => warningTag,
                _ => infoTag,
            };
            if (tag == null)
                continue;
            if (!map.TryGetValue(diagnostic.Start, out var start) || !map.TryGetValue(diagnostic.Start + diagnostic.Length, out var end))
                continue;
            // Zero-length spans (e.g. "; expected") still need a visible squiggle.
            if (end == start)
                end = start + 1;
            ApplyTag(tag, start, end);
            appliedDiagnostics.Add((start, end, diagnostic));
        }
    }

    void ApplyTag(Gtk.TextTag tag, int startChar, int endChar)
    {
        buffer.GetIterAtOffset(out var start, startChar);
        buffer.GetIterAtOffset(out var end, endChar);
        buffer.ApplyTag(tag, start, end);
    }

    static Dictionary<int, int> MapUtf16ToCharOffsets(string text, SortedSet<int> utf16Offsets)
    {
        var map = new Dictionary<int, int>(utf16Offsets.Count);
        using var enumerator = utf16Offsets.GetEnumerator();
        if (!enumerator.MoveNext())
            return map;
        var charOffset = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            while (enumerator.Current == i)
            {
                map[i] = charOffset;
                if (!enumerator.MoveNext())
                    return map;
            }
            if (i < text.Length && !char.IsLowSurrogate(text[i]))
                charOffset++;
        }
        return map;
    }

    void CreateTags()
    {
        var theme = ThemeService.CurrentDefinition;
        if (theme == null)
            return;
        var tagTable = buffer.GetTagTable();

        foreach (var classification in SemanticStyleMap.Classifications)
        {
            var style = SemanticStyleMap.Resolve(theme, classification);
            if (style == null || (style.Foreground == null && !style.Bold && !style.Italic && !style.Underline))
                continue;
            var tag = Gtk.TextTag.New(null);
            if (style.Foreground != null)
                tag.Foreground = style.Foreground;
            if (style.Bold)
                tag.Weight = 700;
            if (style.Italic)
                tag.Style = Pango.Style.Italic;
            if (style.Underline)
                tag.Underline = Pango.Underline.Single;
            tagTable.Add(tag);
            semanticTags[classification] = tag;
        }

        var dark = theme.Info.IsDark;
        errorTag = CreateSquiggleTag(tagTable, theme.GetColor(dark ? "#F14C4C" : "#E51400", "editorError.foreground"));
        warningTag = CreateSquiggleTag(tagTable, theme.GetColor(dark ? "#CCA700" : "#BF8803", "editorWarning.foreground"));
        infoTag = CreateSquiggleTag(tagTable, theme.GetColor(dark ? "#3794FF" : "#1A85FF", "editorInfo.foreground"));
    }

    static Gtk.TextTag CreateSquiggleTag(Gtk.TextTagTable tagTable, string color)
    {
        var tag = Gtk.TextTag.New(null);
        tag.Underline = Pango.Underline.Error;
        var rgba = new Gdk.RGBA();
        if (rgba.Parse(color))
            tag.UnderlineRgba = rgba;
        tagTable.Add(tag);
        return tag;
    }

    void RemoveTags()
    {
        var tagTable = buffer.GetTagTable();
        foreach (var tag in semanticTags.Values)
            tagTable.Remove(tag);
        semanticTags.Clear();
        foreach (var tag in new[] { errorTag, warningTag, infoTag })
        {
            if (tag != null)
                tagTable.Remove(tag);
        }
        errorTag = warningTag = infoTag = null;
        appliedDiagnostics.Clear();
    }
}
