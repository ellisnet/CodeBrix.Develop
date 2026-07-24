//
// FrameBufferOrientation.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// How an emulated frame-buffer device is held. The member NAMES are the
/// values persisted in options.sqlite (the option store serializes enums as
/// strings), so they must not be renamed once shipped.
/// </summary>
public enum FrameBufferOrientation
{
    /// <summary>The short side is the width — 720 x 1280, for example.</summary>
    Portrait,

    /// <summary>The long side is the width — 1280 x 720, for example.</summary>
    Landscape,
}

/// <summary>
/// The presentation helpers for <see cref="FrameBufferOrientation"/>: the
/// order the orientations are offered in, their labels, and the index
/// mapping a drop-down needs.
/// </summary>
public static class FrameBufferOrientations
{
    /// <summary>The orientations, in the order they are offered to the user.</summary>
    public static IReadOnlyList<FrameBufferOrientation> All { get; } = new[]
    {
        FrameBufferOrientation.Portrait,
        FrameBufferOrientation.Landscape,
    };

    /// <summary>The labels of <see cref="All"/>, in the same order.</summary>
    public static IReadOnlyList<string> Labels { get; } = All.Select(GetLabel).ToArray();

    /// <summary>The label shown for the given orientation.</summary>
    public static string GetLabel(this FrameBufferOrientation orientation) => orientation switch
    {
        FrameBufferOrientation.Portrait => "Portrait",
        FrameBufferOrientation.Landscape => "Landscape",
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Unknown orientation"),
    };

    /// <summary>The position of the given orientation within <see cref="All"/>.</summary>
    public static int IndexOf(FrameBufferOrientation orientation)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index] == orientation)
                return index;
        }
        throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Unknown orientation");
    }

    /// <summary>
    /// The orientation at the given position in <see cref="All"/>, or
    /// <see cref="FrameBufferOrientation.Portrait"/> when the position is out
    /// of range (a drop-down with no selection reports one).
    /// </summary>
    public static FrameBufferOrientation FromIndex(int index) =>
        index >= 0 && index < All.Count ? All[index] : FrameBufferOrientation.Portrait;
}
