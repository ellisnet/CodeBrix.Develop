//
// TerminalKeyEncoder.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the GTK counterpart of the author's Lily.Shell TerminalView
//      KeyboardEncoder — GTK supplies the layout-composed character and the
//      modifier state directly, so no hand-maintained US-QWERTY table and no
//      caller-side modifier tracking are needed here)
// SPDX-License-Identifier: MIT
//

using System.Text;
using CodeBrix.Terminal.Engine;
using Gdk = CodeBrix.Develop.UI.Gdk;

namespace CodeBrix.Develop.Ide.Gui.Terminal;

/// <summary>
/// Translates a GTK key event into the VT byte sequence a terminal
/// application expects: special keys become their escape sequences (honoring
/// the terminal's application-cursor mode), Ctrl chords become C0 control
/// codes, Alt prefixes ESC, and printable keys become the layout's composed
/// character.
/// </summary>
public static class TerminalKeyEncoder
{
    /// <summary>
    /// Encodes a key press. Returns null when the key produces no terminal
    /// input (a bare modifier, an unmapped function key).
    /// </summary>
    public static string? Encode(uint keyval, Gdk.ModifierType state, bool applicationCursor)
    {
        var control = state.HasFlag(Gdk.ModifierType.ControlMask);
        var alt = state.HasFlag(Gdk.ModifierType.AltMask);

        var encoded = EncodeCore(keyval, control, applicationCursor);
        if (encoded == null) { return null; }

        //Alt prefixes ESC, the classic meta convention
        return alt ? "\x1b" + encoded : encoded;
    }

    static string? EncodeCore(uint keyval, bool control, bool applicationCursor)
    {
        var special = EncodeSpecial(keyval, applicationCursor);
        if (special != null) { return special; }

        var unicode = Gdk.Functions.KeyvalToUnicode(keyval);
        if (unicode == 0) { return null; }

        if (control)
        {
            //Ctrl+A..Ctrl+Z are C0 control codes 1..26; Ctrl+@..Ctrl+_ the rest
            if (unicode is >= 'a' and <= 'z')
            {
                return ((char) (unicode - 'a' + 1)).ToString();
            }
            if (unicode is >= '@' and <= '_')
            {
                return ((char) (unicode & 0x1f)).ToString();
            }
            if (unicode == ' ')
            {
                return "\x00";
            }
        }

        if (unicode is >= ' ' and not 0x7f)
        {
            return char.ConvertFromUtf32((int) unicode);
        }

        return null;
    }

    static string? EncodeSpecial(uint keyval, bool applicationCursor) => (int) keyval switch
    {
        Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter => "\r",
        Gdk.Constants.KEY_BackSpace => "\x7f",
        Gdk.Constants.KEY_Tab or Gdk.Constants.KEY_KP_Tab => "\t",
        Gdk.Constants.KEY_ISO_Left_Tab => Ascii(EscapeSequences.CmdBackTab),
        Gdk.Constants.KEY_Escape => "\x1b",

        Gdk.Constants.KEY_Up or Gdk.Constants.KEY_KP_Up =>
            Ascii(applicationCursor ? EscapeSequences.MoveUpApp : EscapeSequences.MoveUpNormal),
        Gdk.Constants.KEY_Down or Gdk.Constants.KEY_KP_Down =>
            Ascii(applicationCursor ? EscapeSequences.MoveDownApp : EscapeSequences.MoveDownNormal),
        Gdk.Constants.KEY_Right or Gdk.Constants.KEY_KP_Right =>
            Ascii(applicationCursor ? EscapeSequences.MoveRightApp : EscapeSequences.MoveRightNormal),
        Gdk.Constants.KEY_Left or Gdk.Constants.KEY_KP_Left =>
            Ascii(applicationCursor ? EscapeSequences.MoveLeftApp : EscapeSequences.MoveLeftNormal),
        Gdk.Constants.KEY_Home or Gdk.Constants.KEY_KP_Home =>
            Ascii(applicationCursor ? EscapeSequences.MoveHomeApp : EscapeSequences.MoveHomeNormal),
        Gdk.Constants.KEY_End or Gdk.Constants.KEY_KP_End =>
            Ascii(applicationCursor ? EscapeSequences.MoveEndApp : EscapeSequences.MoveEndNormal),

        Gdk.Constants.KEY_Insert or Gdk.Constants.KEY_KP_Insert => "\x1b[2~",
        Gdk.Constants.KEY_Delete or Gdk.Constants.KEY_KP_Delete => "\x1b[3~",
        Gdk.Constants.KEY_Page_Up or Gdk.Constants.KEY_KP_Page_Up => "\x1b[5~",
        Gdk.Constants.KEY_Page_Down or Gdk.Constants.KEY_KP_Page_Down => "\x1b[6~",

        Gdk.Constants.KEY_F1 => Ascii(EscapeSequences.CmdF[0]),
        Gdk.Constants.KEY_F2 => Ascii(EscapeSequences.CmdF[1]),
        Gdk.Constants.KEY_F3 => Ascii(EscapeSequences.CmdF[2]),
        Gdk.Constants.KEY_F4 => Ascii(EscapeSequences.CmdF[3]),
        Gdk.Constants.KEY_F5 => Ascii(EscapeSequences.CmdF[4]),
        Gdk.Constants.KEY_F6 => Ascii(EscapeSequences.CmdF[5]),
        Gdk.Constants.KEY_F7 => Ascii(EscapeSequences.CmdF[6]),
        Gdk.Constants.KEY_F8 => Ascii(EscapeSequences.CmdF[7]),
        Gdk.Constants.KEY_F9 => Ascii(EscapeSequences.CmdF[8]),
        Gdk.Constants.KEY_F10 => Ascii(EscapeSequences.CmdF[9]),
        Gdk.Constants.KEY_F11 => Ascii(EscapeSequences.CmdF[10]),
        Gdk.Constants.KEY_F12 => Ascii(EscapeSequences.CmdF[11]),

        //The keypad digits and operators have no keyval-to-unicode mapping
        Gdk.Constants.KEY_KP_0 => "0",
        Gdk.Constants.KEY_KP_1 => "1",
        Gdk.Constants.KEY_KP_2 => "2",
        Gdk.Constants.KEY_KP_3 => "3",
        Gdk.Constants.KEY_KP_4 => "4",
        Gdk.Constants.KEY_KP_5 => "5",
        Gdk.Constants.KEY_KP_6 => "6",
        Gdk.Constants.KEY_KP_7 => "7",
        Gdk.Constants.KEY_KP_8 => "8",
        Gdk.Constants.KEY_KP_9 => "9",
        Gdk.Constants.KEY_KP_Add => "+",
        Gdk.Constants.KEY_KP_Subtract => "-",
        Gdk.Constants.KEY_KP_Multiply => "*",
        Gdk.Constants.KEY_KP_Divide => "/",
        Gdk.Constants.KEY_KP_Decimal => ".",
        Gdk.Constants.KEY_KP_Space => " ",
        Gdk.Constants.KEY_KP_Equal => "=",

        _ => null,
    };

    static string Ascii(byte[] sequence) => Encoding.ASCII.GetString(sequence);
}
