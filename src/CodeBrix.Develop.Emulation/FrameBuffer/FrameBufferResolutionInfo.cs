//
// FrameBufferResolutionInfo.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The per-screen facts that drive frame-buffer emulation: the size class it
/// is labeled with, its pixel dimensions, and the window geometry those
/// dimensions imply. Dimensions are stored orientation-independently as a
/// short and a long side, so switching orientation is a presentation change
/// rather than a different screen.
/// </summary>
public sealed class FrameBufferResolutionInfo
{
    /// <summary>
    /// The smallest the emulator window's shorter side may become; below
    /// this the bezel and the screen stop being distinguishable.
    /// </summary>
    public const int MinimumWindowShortSide = 120;

    /// <summary>
    /// The longer side of the emulator window the first time it opens. The
    /// shorter side follows from the screen's proportions, so the default is
    /// 360 x 640 for a 720 x 1280 screen held in portrait.
    /// </summary>
    public const int DefaultWindowLongSide = 640;

    /// <summary>The screen this describes.</summary>
    public FrameBufferResolution Resolution { get; }

    /// <summary>The shorter side in pixels — the width in portrait.</summary>
    public int ShortSide { get; }

    /// <summary>The longer side in pixels — the height in portrait.</summary>
    public int LongSide { get; }

    /// <summary>The size class shown in parentheses (e.g. "5-inch", "HD").</summary>
    public string SizeClass { get; }

    FrameBufferResolutionInfo(FrameBufferResolution resolution, int shortSide, int longSide, string sizeClass)
    {
        Resolution = resolution;
        ShortSide = shortSide;
        LongSide = longSide;
        SizeClass = sizeClass;
    }

    /// <summary>All screens, in the order they are offered to the user.</summary>
    public static IReadOnlyList<FrameBufferResolutionInfo> All { get; } = new[]
    {
        new FrameBufferResolutionInfo(FrameBufferResolution.FiveInch720x1280, 720, 1280, "5-inch"),
        new FrameBufferResolutionInfo(FrameBufferResolution.SevenInch720x1280, 720, 1280, "7-inch"),
        new FrameBufferResolutionInfo(FrameBufferResolution.TenInch1200x1920, 1200, 1920, "10-inch"),
        new FrameBufferResolutionInfo(FrameBufferResolution.Hd1080x1920, 1080, 1920, "HD"),
    };

    /// <summary>Returns the info record for the given screen.</summary>
    public static FrameBufferResolutionInfo Get(FrameBufferResolution resolution)
    {
        foreach (var info in All)
        {
            if (info.Resolution == resolution)
                return info;
        }
        throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown frame buffer resolution");
    }

    /// <summary>The position of the given screen within <see cref="All"/>.</summary>
    public static int IndexOf(FrameBufferResolution resolution)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index].Resolution == resolution)
                return index;
        }
        throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown frame buffer resolution");
    }

    /// <summary>
    /// The screen at the given position in <see cref="All"/>, or the default
    /// screen when the position is out of range (a drop-down with no
    /// selection reports one).
    /// </summary>
    public static FrameBufferResolutionInfo FromIndex(int index) =>
        index >= 0 && index < All.Count ? All[index] : Get(FrameBufferResolution.SevenInch720x1280);

    /// <summary>
    /// The labels of every screen in <see cref="All"/> order, as shown for
    /// the given orientation. Switching orientation relabels the same list
    /// position to the same screen, which is what lets the Options page keep
    /// the user's choice when they flip the orientation.
    /// </summary>
    public static IReadOnlyList<string> GetLabels(FrameBufferOrientation orientation) =>
        All.Select(info => info.GetLabel(orientation)).ToArray();

    /// <summary>The screen's width in pixels in the given orientation.</summary>
    public int GetWidth(FrameBufferOrientation orientation) =>
        orientation == FrameBufferOrientation.Portrait ? ShortSide : LongSide;

    /// <summary>The screen's height in pixels in the given orientation.</summary>
    public int GetHeight(FrameBufferOrientation orientation) =>
        orientation == FrameBufferOrientation.Portrait ? LongSide : ShortSide;

    /// <summary>The screen's width divided by its height in the given orientation.</summary>
    public double GetAspectRatio(FrameBufferOrientation orientation) =>
        (double) GetWidth(orientation) / GetHeight(orientation);

    /// <summary>
    /// The label shown for this screen in the given orientation, e.g.
    /// "720 x 1280 pixels (5-inch)" in portrait and
    /// "1280 x 720 pixels (5-inch)" in landscape.
    /// </summary>
    public string GetLabel(FrameBufferOrientation orientation) => string.Format(
        CultureInfo.InvariantCulture, "{0} x {1} pixels ({2})",
        GetWidth(orientation), GetHeight(orientation), SizeClass);

    /// <summary>
    /// The emulator window size to open at when no size has been remembered:
    /// <see cref="DefaultWindowLongSide"/> on the long side, the short side
    /// scaled to the screen's proportions.
    /// </summary>
    public (int Width, int Height) GetDefaultWindowSize(FrameBufferOrientation orientation) =>
        orientation == FrameBufferOrientation.Portrait
            ? Clamp(Scale(DefaultWindowLongSide, ShortSide, LongSide), DefaultWindowLongSide, orientation)
            : Clamp(DefaultWindowLongSide, Scale(DefaultWindowLongSide, ShortSide, LongSide), orientation);

    /// <summary>
    /// The window size keeping the given width and taking the height from
    /// this screen's proportions — how a remembered size is re-fitted when
    /// the window reopens against a different screen or orientation.
    /// </summary>
    public (int Width, int Height) GetSizeForWidth(int width, FrameBufferOrientation orientation) =>
        Clamp(width, Scale(width, GetHeight(orientation), GetWidth(orientation)), orientation);

    /// <summary>
    /// The nearest size to the one requested that matches this screen's
    /// proportions exactly — applied once an interactive resize settles.
    /// Both candidates (keep the width, keep the height) are considered and
    /// the one that moves the window least wins; a tie keeps the width.
    /// </summary>
    public (int Width, int Height) SnapToAspectRatio(int width, int height, FrameBufferOrientation orientation)
    {
        var screenWidth = GetWidth(orientation);
        var screenHeight = GetHeight(orientation);

        var keepWidth = (Width: width, Height: Scale(width, screenHeight, screenWidth));
        var keepHeight = (Width: Scale(height, screenWidth, screenHeight), Height: height);

        var keepWidthChange = Math.Abs(keepWidth.Width - width) + Math.Abs(keepWidth.Height - height);
        var keepHeightChange = Math.Abs(keepHeight.Width - width) + Math.Abs(keepHeight.Height - height);

        var winner = keepHeightChange < keepWidthChange ? keepHeight : keepWidth;
        return Clamp(winner.Width, winner.Height, orientation);
    }

    // value * numerator / denominator, rounded, never below 1.
    static int Scale(int value, int numerator, int denominator) =>
        Math.Max(1, (int) Math.Round((double) value * numerator / denominator, MidpointRounding.AwayFromZero));

    // Grows a proportional size until its shorter side reaches the minimum,
    // keeping the proportions exact.
    (int Width, int Height) Clamp(int width, int height, FrameBufferOrientation orientation)
    {
        var screenWidth = GetWidth(orientation);
        var screenHeight = GetHeight(orientation);
        var minimumWidth = screenWidth <= screenHeight
            ? MinimumWindowShortSide
            : Scale(MinimumWindowShortSide, screenWidth, screenHeight);
        var minimumHeight = screenWidth <= screenHeight
            ? Scale(MinimumWindowShortSide, screenHeight, screenWidth)
            : MinimumWindowShortSide;

        return width < minimumWidth || height < minimumHeight
            ? (minimumWidth, minimumHeight)
            : (width, height);
    }
}
