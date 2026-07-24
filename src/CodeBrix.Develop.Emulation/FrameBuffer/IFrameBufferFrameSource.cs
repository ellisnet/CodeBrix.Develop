//
// IFrameBufferFrameSource.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// Supplies device-resolution frames to the emulator window's screen. The
/// window polls this on its draw tick, on the GTK main thread; implementations
/// must tolerate being called there and being disposed out from under a call
/// only after the window has detached the source.
/// </summary>
public interface IFrameBufferFrameSource
{
    /// <summary>
    /// Copies the newest complete frame into <paramref name="destination"/>
    /// (device-resolution BGRA8888 premultiplied, tightly packed rows) when a
    /// frame newer than <paramref name="lastSequence"/> is available, updating
    /// <paramref name="lastSequence"/> to the copied frame's sequence number.
    /// Returns false — leaving the destination untouched — when nothing newer
    /// has arrived, including when no frame has ever arrived.
    /// </summary>
    /// <param name="destination">The buffer to copy the frame into.</param>
    /// <param name="destinationBytes">The destination's size; must be exactly
    /// the device frame size (width * height * 4).</param>
    /// <param name="lastSequence">The sequence number of the frame the caller
    /// already has, 0 for none; updated on a successful copy.</param>
    bool TryCopyLatestFrame(IntPtr destination, int destinationBytes, ref long lastSequence);
}
