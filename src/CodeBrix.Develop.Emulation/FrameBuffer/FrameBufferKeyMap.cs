//
// FrameBufferKeyMap.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System.Collections.Generic;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// Turns a GTK key event into what the emulator's wire protocol carries: the
/// WinUI virtual key, the hardware keycode, and the typed character.
/// </summary>
/// <remarks>
/// <para>
/// The virtual key is derived from the PHYSICAL key, not from the character
/// the layout produces for it — exactly how the real Linux frame-buffer head
/// reads its evdev keyboard, so Shift+1 arrives as Number1 with a "!" rather
/// than as some other key. GTK reports keycodes in the X11 style (the evdev
/// scancode plus 8), which is also what the protocol's hardware-keycode field
/// carries, so the offset is removed only for this lookup.
/// </para>
/// <para>
/// The table is a mirror of the shipped head's own evdev-to-VirtualKey
/// mapping (Platform.UI.Runtime.Skia.Linux.FrameBuffer's
/// FrameBufferKeyboardInputSource), transcribed rather than referenced: the
/// IDE cannot depend on CodeBrix.Platform. Keys that head maps to
/// VirtualKey.None are deliberately absent here too — an app running on the
/// emulator then sees precisely what it would see on the device, punctuation
/// included (those keys still deliver their character).
/// </para>
/// </remarks>
internal static class FrameBufferKeyMap
{
    /// <summary>What GTK adds to an evdev scancode to make an X11-style keycode.</summary>
    public const uint EvdevKeycodeOffset = 8;

    // evdev scancode -> WinUI VirtualKey value.
    static readonly Dictionary<uint, uint> virtualKeysByScancode = new()
    {
        { 1, 27 },  // KEY_ESC -> VirtualKey.Escape
        { 2, 49 },  // KEY_1 -> VirtualKey.Number1
        { 3, 50 },  // KEY_2 -> VirtualKey.Number2
        { 4, 51 },  // KEY_3 -> VirtualKey.Number3
        { 5, 52 },  // KEY_4 -> VirtualKey.Number4
        { 6, 53 },  // KEY_5 -> VirtualKey.Number5
        { 7, 54 },  // KEY_6 -> VirtualKey.Number6
        { 8, 55 },  // KEY_7 -> VirtualKey.Number7
        { 9, 56 },  // KEY_8 -> VirtualKey.Number8
        { 10, 57 },  // KEY_9 -> VirtualKey.Number9
        { 11, 48 },  // KEY_0 -> VirtualKey.Number0
        { 12, 109 },  // KEY_MINUS -> VirtualKey.Subtract
        { 14, 8 },  // KEY_BACKSPACE -> VirtualKey.Back
        { 15, 9 },  // KEY_TAB -> VirtualKey.Tab
        { 16, 81 },  // KEY_Q -> VirtualKey.Q
        { 17, 87 },  // KEY_W -> VirtualKey.W
        { 18, 69 },  // KEY_E -> VirtualKey.E
        { 19, 82 },  // KEY_R -> VirtualKey.R
        { 20, 84 },  // KEY_T -> VirtualKey.T
        { 21, 89 },  // KEY_Y -> VirtualKey.Y
        { 22, 85 },  // KEY_U -> VirtualKey.U
        { 23, 73 },  // KEY_I -> VirtualKey.I
        { 24, 79 },  // KEY_O -> VirtualKey.O
        { 25, 80 },  // KEY_P -> VirtualKey.P
        { 28, 13 },  // KEY_ENTER -> VirtualKey.Enter
        { 29, 162 },  // KEY_LEFTCTRL -> VirtualKey.LeftControl
        { 30, 65 },  // KEY_A -> VirtualKey.A
        { 31, 83 },  // KEY_S -> VirtualKey.S
        { 32, 68 },  // KEY_D -> VirtualKey.D
        { 33, 70 },  // KEY_F -> VirtualKey.F
        { 34, 71 },  // KEY_G -> VirtualKey.G
        { 35, 72 },  // KEY_H -> VirtualKey.H
        { 36, 74 },  // KEY_J -> VirtualKey.J
        { 37, 75 },  // KEY_K -> VirtualKey.K
        { 38, 76 },  // KEY_L -> VirtualKey.L
        { 42, 160 },  // KEY_LEFTSHIFT -> VirtualKey.LeftShift
        { 44, 90 },  // KEY_Z -> VirtualKey.Z
        { 45, 88 },  // KEY_X -> VirtualKey.X
        { 46, 67 },  // KEY_C -> VirtualKey.C
        { 47, 86 },  // KEY_V -> VirtualKey.V
        { 48, 66 },  // KEY_B -> VirtualKey.B
        { 49, 78 },  // KEY_N -> VirtualKey.N
        { 50, 77 },  // KEY_M -> VirtualKey.M
        { 54, 161 },  // KEY_RIGHTSHIFT -> VirtualKey.RightShift
        { 55, 106 },  // KEY_KPASTERISK -> VirtualKey.Multiply
        { 56, 164 },  // KEY_LEFTALT -> VirtualKey.LeftMenu
        { 57, 32 },  // KEY_SPACE -> VirtualKey.Space
        { 58, 20 },  // KEY_CAPSLOCK -> VirtualKey.CapitalLock
        { 59, 112 },  // KEY_F1 -> VirtualKey.F1
        { 60, 113 },  // KEY_F2 -> VirtualKey.F2
        { 61, 114 },  // KEY_F3 -> VirtualKey.F3
        { 62, 115 },  // KEY_F4 -> VirtualKey.F4
        { 63, 116 },  // KEY_F5 -> VirtualKey.F5
        { 64, 117 },  // KEY_F6 -> VirtualKey.F6
        { 65, 118 },  // KEY_F7 -> VirtualKey.F7
        { 66, 119 },  // KEY_F8 -> VirtualKey.F8
        { 67, 120 },  // KEY_F9 -> VirtualKey.F9
        { 68, 121 },  // KEY_F10 -> VirtualKey.F10
        { 69, 144 },  // KEY_NUMLOCK -> VirtualKey.NumberKeyLock
        { 70, 145 },  // KEY_SCROLLLOCK -> VirtualKey.Scroll
        { 71, 103 },  // KEY_KP7 -> VirtualKey.NumberPad7
        { 72, 104 },  // KEY_KP8 -> VirtualKey.NumberPad8
        { 73, 105 },  // KEY_KP9 -> VirtualKey.NumberPad9
        { 74, 109 },  // KEY_KPMINUS -> VirtualKey.Subtract
        { 75, 100 },  // KEY_KP4 -> VirtualKey.NumberPad4
        { 76, 101 },  // KEY_KP5 -> VirtualKey.NumberPad5
        { 77, 102 },  // KEY_KP6 -> VirtualKey.NumberPad6
        { 79, 97 },  // KEY_KP1 -> VirtualKey.NumberPad1
        { 80, 98 },  // KEY_KP2 -> VirtualKey.NumberPad2
        { 81, 99 },  // KEY_KP3 -> VirtualKey.NumberPad3
        { 82, 96 },  // KEY_KP0 -> VirtualKey.NumberPad0
        { 83, 108 },  // KEY_KPDOT -> VirtualKey.Separator
        { 87, 122 },  // KEY_F11 -> VirtualKey.F11
        { 88, 123 },  // KEY_F12 -> VirtualKey.F12
        { 97, 163 },  // KEY_RIGHTCTRL -> VirtualKey.RightControl
        { 100, 165 },  // KEY_RIGHTALT -> VirtualKey.RightMenu
        { 102, 36 },  // KEY_HOME -> VirtualKey.Home
        { 103, 38 },  // KEY_UP -> VirtualKey.Up
        { 104, 33 },  // KEY_PAGEUP -> VirtualKey.PageUp
        { 105, 37 },  // KEY_LEFT -> VirtualKey.Left
        { 106, 39 },  // KEY_RIGHT -> VirtualKey.Right
        { 107, 35 },  // KEY_END -> VirtualKey.End
        { 108, 40 },  // KEY_DOWN -> VirtualKey.Down
        { 109, 34 },  // KEY_PAGEDOWN -> VirtualKey.PageDown
        { 110, 45 },  // KEY_INSERT -> VirtualKey.Insert
        { 111, 46 },  // KEY_DELETE -> VirtualKey.Delete
        { 158, 8 },  // KEY_BACK -> VirtualKey.Back
        { 159, 167 },  // KEY_FORWARD -> VirtualKey.GoForward
        { 183, 124 },  // KEY_F13 -> VirtualKey.F13
        { 184, 125 },  // KEY_F14 -> VirtualKey.F14
        { 185, 126 },  // KEY_F15 -> VirtualKey.F15
        { 186, 127 },  // KEY_F16 -> VirtualKey.F16
        { 187, 128 },  // KEY_F17 -> VirtualKey.F17
        { 188, 129 },  // KEY_F18 -> VirtualKey.F18
        { 189, 130 },  // KEY_F19 -> VirtualKey.F19
        { 190, 131 },  // KEY_F20 -> VirtualKey.F20
        { 191, 132 },  // KEY_F21 -> VirtualKey.F21
        { 192, 133 },  // KEY_F22 -> VirtualKey.F22
        { 193, 134 },  // KEY_F23 -> VirtualKey.F23
        { 194, 135 },  // KEY_F24 -> VirtualKey.F24
    };

    /// <summary>
    /// The character the key typed, as the device's xkb keyboard would report
    /// it. GTK resolves the layout and Shift for us; what it does not do is
    /// fold Control in, so a held Control turns a letter into its control
    /// code the way xkb_state_key_get_utf8 does — otherwise Ctrl+A would
    /// arrive as the letter "a" and be typed into a text box instead of
    /// selecting its contents.
    /// </summary>
    public static uint CharacterFromKeyval(uint unicodeCodepoint, bool controlHeld)
    {
        if (!controlHeld || unicodeCodepoint == 0)
            return unicodeCodepoint;
        // "@" through "_" (which covers A-Z) map onto 0x00-0x1F; the lower-case
        // letters fold onto the same codes. Everything else Control cannot
        // name, so it types nothing.
        var upper = unicodeCodepoint is >= 'a' and <= 'z' ? unicodeCodepoint - 32 : unicodeCodepoint;
        if (upper is >= '@' and <= '_')
            return upper & 0x1F;
        if (unicodeCodepoint == '?')
            return 0x7F; // Ctrl+? is DEL, as on a terminal
        return 0;
    }

    /// <summary>
    /// The WinUI virtual key for an X11-style hardware keycode as GTK reports
    /// it, or 0 (VirtualKey.None) for a key the head does not name — which is
    /// how the device behaves too.
    /// </summary>
    public static uint VirtualKeyFromKeycode(uint hardwareKeyCode)
    {
        if (hardwareKeyCode < EvdevKeycodeOffset)
            return 0;
        return virtualKeysByScancode.TryGetValue(hardwareKeyCode - EvdevKeycodeOffset, out var virtualKey)
            ? virtualKey
            : 0;
    }
}
