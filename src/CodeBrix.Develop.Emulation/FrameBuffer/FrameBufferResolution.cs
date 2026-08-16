//
// FrameBufferResolution.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The screen an emulated frame-buffer device has. Each member names both
/// the size class and the pixel dimensions, so a future device of the same
/// size class at a different resolution is a NEW member rather than a
/// redefinition of an existing one — two of these deliberately share the
/// same dimensions today (5-inch and 7-inch are both 720 x 1280).
/// <para>
/// The member NAMES are the values persisted in options.sqlite (the option
/// store serializes enums as strings), so they must not be renamed once
/// shipped. The dimensions in the names are the PORTRAIT dimensions; the
/// orientation is a separate setting.
/// </para>
/// </summary>
public enum FrameBufferResolution
{
    /// <summary>A 5-inch screen, 720 x 1280 in portrait.</summary>
    FiveInch720x1280,

    /// <summary>A 7-inch screen, 720 x 1280 in portrait.</summary>
    SevenInch720x1280,

    /// <summary>A 7-inch screen, 800 x 1280 in portrait (e.g. WinBook TW700).</summary>
    SevenInch800x1280,

    /// <summary>An 8-inch screen, 800 x 1280 in portrait (e.g. WinBook TW802).</summary>
    EightInch800x1280,

    /// <summary>
    /// An 8-inch screen, 1200 x 1920 in portrait (e.g. NuVision TM800W610L) —
    /// a distinct screen from the 10-inch one of the same dimensions, since
    /// the size class is the identity. At about 283 ppi it is the density that
    /// wants an application-side interface scale.
    /// </summary>
    EightInch1200x1920,

    /// <summary>A 10-inch screen, 1200 x 1920 in portrait.</summary>
    TenInch1200x1920,

    /// <summary>A high-definition screen, 1080 x 1920 in portrait.</summary>
    Hd1080x1920,
}
