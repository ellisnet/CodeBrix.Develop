//
// FrameBufferDeviceOrientation.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// How the emulated device is being held — not how the application is laid out.
/// The two are the same only while the application honors the orientation it has
/// been turned to; an application that declines simply stays as it was.
/// <para>
/// The members are in the order a COUNTER-CLOCKWISE turn of the device walks
/// them, and their numeric values are that position in the cycle, so the
/// difference between two of them is the number of quarter turns between them.
/// Those values are also the protocol's wire values (see
/// FrameBufferEmulatorProtocol.Orientation*) — deliberately not the WinUI
/// DisplayOrientations flag values, which this repo cannot reference.
/// </para>
/// </summary>
public enum FrameBufferDeviceOrientation
{
    /// <summary>No rotation.</summary>
    Landscape = 0,

    /// <summary>A quarter turn counter-clockwise from <see cref="Landscape"/>.</summary>
    Portrait = 1,

    /// <summary>Half a turn from <see cref="Landscape"/>.</summary>
    LandscapeFlipped = 2,

    /// <summary>Three quarter turns counter-clockwise from <see cref="Landscape"/>.</summary>
    PortraitFlipped = 3,
}
