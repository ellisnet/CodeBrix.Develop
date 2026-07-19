//
// TemplateUpdateState.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Globalization;
using CodeBrix.Develop.Core.Options;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// The persisted state (in options.sqlite) tracking which TemplateApp.zip the
/// New Application experience uses and when the background updater last
/// reached GitHub successfully:
/// <list type="bullet">
///   <item><see cref="ActiveCommitId"/> — the CodeBrix.Platform commit whose
///   templates/TemplateApp.zip is currently active.</item>
///   <item><see cref="UseUpdatedCopy"/> — whether the downloaded copy under
///   templates/updated/ supersedes the bundled baseline.</item>
///   <item><see cref="LastSuccessfulCheckUtc"/> — the last time the GitHub
///   check succeeded (drives the 10-minute throttle).</item>
/// </list>
/// All accessors are safe to read before the options store is initialized
/// (they then report defaults and ignore writes).
/// </summary>
public static class TemplateUpdateState
{
    /// <summary>
    /// The CodeBrix.Platform commit that last touched templates/TemplateApp.zip
    /// which the build's embedded/bundled baseline corresponds to. Also the
    /// value <see cref="ActiveCommitId"/> is reset to on fallback.
    /// </summary>
    public const string BaselineCommitId = "38f5a49e3171d577e7612e317fe8e744b6534426";

    const string CommitIdKey = "TemplateArchive.ActiveCommitId";
    const string UseUpdatedKey = "TemplateArchive.UseUpdatedCopy";
    const string LastCheckKey = "TemplateArchive.LastSuccessfulCheckUtc";

    /// <summary>The commit whose TemplateApp.zip is currently active.</summary>
    public static string ActiveCommitId
    {
        get => PropertyService.IsInitialized
            ? PropertyService.Get(CommitIdKey, BaselineCommitId)
            : BaselineCommitId;
        set
        {
            if (PropertyService.IsInitialized)
                PropertyService.Set(CommitIdKey, value);
        }
    }

    /// <summary>Whether the downloaded templates/updated/ copy is the active archive.</summary>
    public static bool UseUpdatedCopy
    {
        get => PropertyService.IsInitialized && PropertyService.Get(UseUpdatedKey, false);
        set
        {
            if (PropertyService.IsInitialized)
                PropertyService.Set(UseUpdatedKey, value);
        }
    }

    /// <summary>The last time the GitHub check succeeded, or null if never.</summary>
    public static DateTime? LastSuccessfulCheckUtc
    {
        get
        {
            if (!PropertyService.IsInitialized)
                return null;
            var stored = PropertyService.Get<string>(LastCheckKey, null);
            if (string.IsNullOrEmpty(stored))
                return null;
            return DateTime.TryParse(stored, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) ? parsed : (DateTime?) null;
        }
        set
        {
            if (!PropertyService.IsInitialized)
                return;
            PropertyService.Set(LastCheckKey,
                value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Reverts to the bundled baseline archive: stops using the updated copy
    /// and resets the active commit id to <see cref="BaselineCommitId"/>. Used
    /// when the updated copy turns out to be unusable.
    /// </summary>
    public static void RevertToBaseline()
    {
        UseUpdatedCopy = false;
        ActiveCommitId = BaselineCommitId;
    }
}
