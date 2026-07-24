//
// FrameBufferTouchKind.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The three things a single finger can do on the emulated touchscreen. The
/// mapping from the mouse is: left button down = the finger touches the screen
/// (<see cref="Press"/>), movement while the button is held = <see cref="Move"/>,
/// button up = <see cref="Release"/>. Movement with the button up is nothing at
/// all — a finger that is not touching the screen does not exist.
/// </summary>
public enum FrameBufferTouchKind
{
    /// <summary>The finger touched the screen.</summary>
    Press,

    /// <summary>The finger moved while touching the screen.</summary>
    Move,

    /// <summary>The finger lifted off the screen.</summary>
    Release,
}
