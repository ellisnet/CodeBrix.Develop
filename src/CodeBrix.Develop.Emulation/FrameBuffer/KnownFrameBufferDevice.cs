//
// KnownFrameBufferDevice.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The catalog of real devices CodeBrix.Develop recognizes when an
/// SSH-connected machine identifies itself by DMI vendor and model: what
/// screen the device has, and which orientation it is native to. Grows a
/// device at a time as hardware is adopted; an unrecognized device is
/// simply not in the list.
/// </summary>
public sealed class KnownFrameBufferDevice
{
    KnownFrameBufferDevice(string vendor, string model,
        FrameBufferResolution screen, FrameBufferOrientation nativeOrientation)
    {
        Vendor = vendor;
        Model = model;
        Screen = screen;
        NativeOrientation = nativeOrientation;
    }

    /// <summary>The DMI sys_vendor value, e.g. "WinBook".</summary>
    public string Vendor { get; }

    /// <summary>The DMI product_name value, e.g. "TW700".</summary>
    public string Model { get; }

    /// <summary>The device's screen.</summary>
    public FrameBufferResolution Screen { get; }

    /// <summary>
    /// The orientation the device runs in unless someone reconfigures it —
    /// the WinBook TW700, for example, defaults to portrait.
    /// </summary>
    public FrameBufferOrientation NativeOrientation { get; }

    /// <summary>Every device the IDE knows.</summary>
    public static IReadOnlyList<KnownFrameBufferDevice> All { get; } = new[]
    {
        new KnownFrameBufferDevice("WinBook", "TW700",
            FrameBufferResolution.SevenInch800x1280, FrameBufferOrientation.Portrait),
        new KnownFrameBufferDevice("WinBook", "TW802",
            FrameBufferResolution.EightInch800x1280, FrameBufferOrientation.Portrait),
    };

    // Devices on the way to test — add each above once its REAL DMI sys_vendor
    // and product_name are captured on-device. The marketing name and the
    // parenthetical "aka" below are not necessarily the DMI strings (confirm
    // live, the same way we are doing for the TW802). Orientation defaults are
    // "probably portrait" until verified on the hardware.
    //
    //   Dell Venue 8 Pro   (aka T01D)  — 8-inch, 800 x 1280,  portrait(?)
    //                                    -> EightInch800x1280 (exists)
    //   Asus VivoTab Note 8 (aka M80T) — 8-inch, 800 x 1280,  portrait(?)
    //                                    -> EightInch800x1280 (exists)
    //   NuVision TM800W610L            — 8-inch, 1200 x 1920, portrait(?)
    //                                    -> needs a NEW EightInch1200x1920: an
    //                                       8-inch 1200x1920 is a distinct screen
    //                                       from the existing 10-inch one, since
    //                                       the size class is the identity.

    /// <summary>
    /// Finds the catalog entry for a reported vendor and model —
    /// case-insensitively and ignoring surrounding whitespace, since the
    /// values arrive from files with trailing newlines. Null when the device
    /// is not known.
    /// </summary>
    public static KnownFrameBufferDevice? TryFind(string? vendor, string? model)
    {
        if (string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(model))
            return null;
        foreach (var device in All)
        {
            if (string.Equals(device.Vendor, vendor.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(device.Model, model.Trim(), StringComparison.OrdinalIgnoreCase))
                return device;
        }
        return null;
    }
}
