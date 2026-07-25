using CodeBrix.Develop.Emulation.FrameBuffer;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

public class FrameBufferKeyMapTests
{
    // GTK reports the evdev scancode plus 8; the values below are the
    // scancodes from Linux's input-event-codes.h.
    const uint Offset = FrameBufferKeyMap.EvdevKeycodeOffset;

    [Theory]
    [InlineData(1u, 27u)]     // KEY_ESC   -> VirtualKey.Escape
    [InlineData(28u, 13u)]    // KEY_ENTER -> VirtualKey.Enter
    [InlineData(30u, 65u)]    // KEY_A     -> VirtualKey.A
    [InlineData(2u, 49u)]     // KEY_1     -> VirtualKey.Number1
    [InlineData(57u, 32u)]    // KEY_SPACE -> VirtualKey.Space
    [InlineData(14u, 8u)]     // KEY_BACKSPACE -> VirtualKey.Back
    [InlineData(103u, 38u)]   // KEY_UP    -> VirtualKey.Up
    [InlineData(42u, 160u)]   // KEY_LEFTSHIFT  -> VirtualKey.LeftShift
    [InlineData(29u, 162u)]   // KEY_LEFTCTRL   -> VirtualKey.LeftControl
    [InlineData(63u, 116u)]   // KEY_F5    -> VirtualKey.F5
    public void The_virtual_key_comes_from_the_physical_key(uint scancode, uint expectedVirtualKey)
        => FrameBufferKeyMap.VirtualKeyFromKeycode(scancode + Offset).Should().Be(expectedVirtualKey);

    [Theory]
    [InlineData(13u)]  // KEY_EQUAL
    [InlineData(26u)]  // KEY_LEFTBRACE
    [InlineData(39u)]  // KEY_SEMICOLON
    [InlineData(51u)]  // KEY_COMMA
    [InlineData(53u)]  // KEY_SLASH
    [InlineData(125u)] // KEY_LEFTMETA
    public void A_key_the_head_does_not_name_maps_to_None(uint scancode)
        => FrameBufferKeyMap.VirtualKeyFromKeycode(scancode + Offset).Should().Be(0u);

    [Fact]
    public void An_unknown_scancode_maps_to_None()
        => FrameBufferKeyMap.VirtualKeyFromKeycode(60000).Should().Be(0u);

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    public void A_keycode_below_the_evdev_offset_maps_to_None(uint keycode)
        => FrameBufferKeyMap.VirtualKeyFromKeycode(keycode).Should().Be(0u);

    [Theory]
    [InlineData('a', 'a')]
    [InlineData('!', '!')]
    [InlineData(0u, 0u)]
    public void Without_Control_the_character_is_what_the_layout_typed(uint codepoint, uint expected)
        => FrameBufferKeyMap.CharacterFromKeyval(codepoint, controlHeld: false).Should().Be(expected);

    [Theory]
    [InlineData('a', 0x01u)]   // Ctrl+A -> SOH, so a text box selects instead of typing "a"
    [InlineData('A', 0x01u)]
    [InlineData('q', 0x11u)]   // Ctrl+Q -> DC1
    [InlineData('@', 0x00u)]
    [InlineData('_', 0x1Fu)]
    [InlineData('?', 0x7Fu)]   // Ctrl+? -> DEL
    public void With_Control_a_letter_becomes_its_control_code(uint codepoint, uint expected)
        => FrameBufferKeyMap.CharacterFromKeyval(codepoint, controlHeld: true).Should().Be(expected);

    [Theory]
    [InlineData('1')]
    [InlineData('.')]
    public void With_Control_a_key_that_has_no_control_code_types_nothing(uint codepoint)
        => FrameBufferKeyMap.CharacterFromKeyval(codepoint, controlHeld: true).Should().Be(0u);
}
