//
// TemplateUpdater.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// The background check that keeps the New Application template current with
/// CodeBrix.Platform. On launch it asks GitHub for the commit that last
/// touched templates/TemplateApp.zip on the main branch; when that differs
/// from the active commit it downloads the new archive into templates/updated/
/// and records it as active. Everything is best-effort and unauthenticated:
/// a failed or throttled check simply leaves the current archive in place.
/// </summary>
public static class TemplateUpdater
{
    const string Owner = "ellisnet";
    const string Repo = "CodeBrix.Platform";
    const string ArchiveRepoPath = "templates/TemplateApp.zip";

    // At most one successful GitHub check per this interval (throttle keyed on
    // the last SUCCESSFUL check timestamp in options.sqlite).
    static readonly TimeSpan CheckThrottle = TimeSpan.FromMinutes(10);

    static readonly HttpClient http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeBrix.Develop");
        return client;
    }

    /// <summary>
    /// Starts the launch-time update check in the background and returns
    /// immediately. Safe to call once the options store is initialized.
    /// </summary>
    public static void StartBackgroundCheck()
        => _ = Task.Run(() => CheckAndUpdateAsync());

    /// <summary>
    /// Runs one update check. Honors the 10-minute throttle, and never throws.
    /// </summary>
    public static async Task CheckAndUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var lastCheck = TemplateUpdateState.LastSuccessfulCheckUtc;
            if (lastCheck.HasValue && DateTime.UtcNow - lastCheck.Value < CheckThrottle)
                return;

            var templatesDirectory = TemplateArchive.FindTemplatesDirectory();
            if (templatesDirectory == null)
            {
                LoggingService.LogInfo("Template updater: templates folder not found; skipping update check.");
                return;
            }

            var latestCommit = await GetLatestArchiveCommitAsync(cancellationToken).ConfigureAwait(false);
            if (latestCommit == null)
                return; // check refused/unreachable/rate-limited: keep current, no timestamp update

            // The check itself succeeded.
            TemplateUpdateState.LastSuccessfulCheckUtc = DateTime.UtcNow;

            if (string.Equals(latestCommit, TemplateUpdateState.ActiveCommitId, StringComparison.OrdinalIgnoreCase))
                return; // already on the latest archive

            if (await TryDownloadAndActivateAsync(templatesDirectory, latestCommit, cancellationToken).ConfigureAwait(false))
                LoggingService.LogInfo($"Template updater: updated TemplateApp.zip to commit {latestCommit}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Template updater: update check failed: {ex.Message}");
        }
    }

    // The commit that last touched the archive on main, or null when GitHub
    // is unreachable, refuses the request, or reports nothing.
    static async Task<string> GetLatestArchiveCommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/commits?sha=main&path={ArchiveRepoPath}&per_page=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LoggingService.LogInfo($"Template updater: GitHub check returned {(int) response.StatusCode}; keeping current archive.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;
            var sha = root[0].GetProperty("sha").GetString();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.LogInfo($"Template updater: GitHub check unavailable: {ex.Message}");
            return null;
        }
    }

    // Downloads the archive at the given commit, validates it, and — only when
    // valid — replaces templates/updated/TemplateApp.zip and records it as
    // active. Returns whether the updated copy was activated.
    static async Task<bool> TryDownloadAndActivateAsync(
        string templatesDirectory, string commit, CancellationToken cancellationToken)
    {
        var url = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{commit}/{ArchiveRepoPath}";
        byte[] data;
        using (var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                LoggingService.LogWarning($"Template updater: download failed ({(int) response.StatusCode}); keeping current archive.");
                return false;
            }
            data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var updatedDirectory = Path.Combine(templatesDirectory, TemplateArchive.UpdatedFolderName);
        Directory.CreateDirectory(updatedDirectory);
        var finalPath = Path.Combine(updatedDirectory, TemplateArchive.ArchiveFileName);
        var stagingPath = finalPath + ".tmp";

        await File.WriteAllBytesAsync(stagingPath, data, cancellationToken).ConfigureAwait(false);
        if (!TemplateArchive.TryReadValidArchive(stagingPath, out _))
        {
            LoggingService.LogWarning("Template updater: downloaded archive is invalid; discarding.");
            TryDelete(stagingPath);
            return false;
        }

        File.Move(stagingPath, finalPath, overwrite: true);
        TemplateUpdateState.ActiveCommitId = commit;
        TemplateUpdateState.UseUpdatedCopy = true;
        return true;
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
