//
// FrameBufferLanguageInfo.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// One entry of the system-language list offered for frame-buffer emulation:
/// the code persisted in options.sqlite and the name shown for it. The codes
/// are the software-keyboard layout ids of the Linux Frame Buffer head
/// (BCP-47-style tags — mostly bare two-letter codes, a few carrying a region
/// or script subtag), plus <see cref="SystemDefaultCode"/> for "whatever the
/// host is set to". Display names read "{English name} ({native name})", or
/// "{English name} ({variant} - {native name} - {native variant})" for the
/// regional and script variants.
/// </summary>
public sealed class FrameBufferLanguageInfo
{
    /// <summary>
    /// The code stored for "follow the host's own language" — the default
    /// until the user picks a specific language, and what an unrecognized
    /// stored code falls back to.
    /// </summary>
    public const string SystemDefaultCode = "system-default";

    /// <summary>The code persisted in options.sqlite.</summary>
    public string Code { get; }

    /// <summary>The name shown for this language in the drop-down.</summary>
    public string DisplayName { get; }

    FrameBufferLanguageInfo(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    /// <summary>
    /// Whether this entry is <see cref="SystemDefaultCode"/> rather than a
    /// particular language.
    /// </summary>
    public bool IsSystemDefault => Code == SystemDefaultCode;

    /// <summary>
    /// Every language, in the order they are offered to the user: the system
    /// default first, then the keyboard layouts grouped west to east —
    /// Western, Southern, Northern and Central Europe, the Cyrillic-script
    /// languages, and the Caucasus.
    /// </summary>
    public static IReadOnlyList<FrameBufferLanguageInfo> All { get; } = new[]
    {
        new FrameBufferLanguageInfo(SystemDefaultCode, "Current system default"),

        //Western Europe
        new FrameBufferLanguageInfo("en", "English (US)"),
        new FrameBufferLanguageInfo("en-GB", "English (UK)"),
        new FrameBufferLanguageInfo("de", "German (Deutsch)"),
        new FrameBufferLanguageInfo("de-CH", "German (Swiss - Deutsch - Schweiz)"),
        new FrameBufferLanguageInfo("fr", "French (Français)"),
        new FrameBufferLanguageInfo("fr-BE", "French (Belgian - Français - Belgique)"),
        new FrameBufferLanguageInfo("fr-CH", "French (Swiss - Français - Suisse)"),
        new FrameBufferLanguageInfo("nl", "Dutch (Nederlands)"),

        //Southern Europe
        new FrameBufferLanguageInfo("es", "Spanish (Español)"),
        new FrameBufferLanguageInfo("pt", "Portuguese (Português)"),
        new FrameBufferLanguageInfo("it", "Italian (Italiano)"),
        new FrameBufferLanguageInfo("mt", "Maltese (Malti)"),
        new FrameBufferLanguageInfo("sq", "Albanian (Shqip)"),
        new FrameBufferLanguageInfo("tr", "Turkish (Türkçe)"),
        new FrameBufferLanguageInfo("el", "Greek (Ελληνικά)"),

        //Northern Europe
        new FrameBufferLanguageInfo("da", "Danish (Dansk)"),
        new FrameBufferLanguageInfo("no", "Norwegian (Norsk)"),
        new FrameBufferLanguageInfo("sv", "Swedish (Svenska)"),
        new FrameBufferLanguageInfo("fi", "Finnish (Suomi)"),
        new FrameBufferLanguageInfo("is", "Icelandic (Íslenska)"),
        new FrameBufferLanguageInfo("lt", "Lithuanian (Lietuvių)"),
        new FrameBufferLanguageInfo("lv", "Latvian (Latviešu)"),
        new FrameBufferLanguageInfo("et", "Estonian (Eesti)"),

        //Central Europe
        new FrameBufferLanguageInfo("pl", "Polish (Polski)"),
        new FrameBufferLanguageInfo("cs", "Czech (Čeština)"),
        new FrameBufferLanguageInfo("sk", "Slovak (Slovenčina)"),
        new FrameBufferLanguageInfo("hu", "Hungarian (Magyar)"),
        new FrameBufferLanguageInfo("ro", "Romanian (Română)"),
        new FrameBufferLanguageInfo("hr", "Croatian (Hrvatski)"),
        new FrameBufferLanguageInfo("sr-Latn", "Serbian (Latin - Srpski - latinica)"),

        //Cyrillic script
        new FrameBufferLanguageInfo("ru", "Russian (Русский)"),
        new FrameBufferLanguageInfo("uk", "Ukrainian (Українська)"),
        new FrameBufferLanguageInfo("be", "Belarusian (Беларуская)"),
        new FrameBufferLanguageInfo("bg", "Bulgarian (Български)"),
        new FrameBufferLanguageInfo("sr", "Serbian (Cyrillic - Српски - ћирилица)"),
        new FrameBufferLanguageInfo("mk", "Macedonian (Македонски)"),

        //Caucasus
        new FrameBufferLanguageInfo("ka", "Georgian (ქართული)"),
        new FrameBufferLanguageInfo("hy", "Armenian (Հայերեն)"),
    };

    /// <summary>The entry standing for the host's own language.</summary>
    public static FrameBufferLanguageInfo SystemDefault { get; } = All[0];

    /// <summary>The display names of <see cref="All"/>, in the same order.</summary>
    public static IReadOnlyList<string> Labels { get; } =
        All.Select(info => info.DisplayName).ToArray();

    /// <summary>
    /// The entry for the given code, or <see cref="SystemDefault"/> when the
    /// code is not one that is offered. A stored code is only ever text — it
    /// can come from a hand-edited store or a build that offered a language
    /// this one does not — so an unknown code falls back rather than throwing.
    /// </summary>
    public static FrameBufferLanguageInfo Get(string? code)
    {
        foreach (var info in All)
        {
            if (info.Code == code)
                return info;
        }
        return SystemDefault;
    }

    /// <summary>
    /// The position of the given code within <see cref="All"/>, or 0 — the
    /// system default — for a code that is not offered.
    /// </summary>
    public static int IndexOf(string? code)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index].Code == code)
                return index;
        }
        return 0;
    }

    /// <summary>
    /// The entry at the given position in <see cref="All"/>, or
    /// <see cref="SystemDefault"/> when the position is out of range (a
    /// drop-down with no selection reports one).
    /// </summary>
    public static FrameBufferLanguageInfo FromIndex(int index) =>
        index >= 0 && index < All.Count ? All[index] : SystemDefault;
}
