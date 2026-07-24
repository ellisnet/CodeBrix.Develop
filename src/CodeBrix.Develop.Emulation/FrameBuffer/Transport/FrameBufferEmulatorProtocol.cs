//
// FrameBufferEmulatorProtocol.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Buffers.Binary;

namespace CodeBrix.Develop.Emulation.FrameBuffer.Transport;

/// <summary>
/// The frame-buffer emulator transport protocol, version 1 — the contract
/// between CodeBrix.Develop (the server: it creates the shared-memory file and
/// the listening socket BEFORE launching the app) and an emulated frame-buffer
/// head (the client: it maps the file, connects, and powers off — libc _exit —
/// the moment the socket reaches end-of-file).
/// <para>
/// KEEP IN SYNC, TEXTUALLY, with the head-side implementation in the
/// CodeBrix.Platform repo (src/Platform.UI.Runtime.Skia.Linux.FrameBuffer.Emulated,
/// FrameBufferEmulatorProtocol.cs). The IDE cannot reference CodeBrix.Platform,
/// so the protocol is implemented twice from this one definition.
/// </para>
/// <para>
/// LAUNCH CONTRACT — environment variables set by the IDE on the app process:
/// CODEBRIX_FBEMU_SHM_PATH (absolute path of the shared-memory file, created
/// and sized by the IDE), CODEBRIX_FBEMU_SOCKET_PATH (absolute path of the
/// Unix stream socket), CODEBRIX_FBEMU_WIDTH and CODEBRIX_FBEMU_HEIGHT (device
/// pixels, decimal strings). The head asserts the env resolution matches the
/// shared-memory header and hard-fails on mismatch (resolution lockstep).
/// </para>
/// <para>
/// SHARED MEMORY — a 64-byte little-endian header (field offsets below), then
/// two page-aligned pixel slots of height*stride bytes each, top-down rows,
/// BGRA8888 premultiplied (matches SKColorType.Bgra8888/Premul and Cairo
/// ARGB32 on little-endian). The head renders frame N into slot N % 2, then
/// atomically stores LatestSequence = N, then sends FrameReady(N) on the
/// socket. Sequence 0 means "no frame yet". The IDE reads on its own tick:
/// s1 = LatestSequence, blit slot s1 % 2, s2 = LatestSequence; if
/// s2 &gt;= s1 + 2 the slot may have been rewritten mid-blit, so re-blit.
/// </para>
/// <para>
/// SOCKET — SOCK_STREAM, fixed 16-byte little-endian messages: uint32 type,
/// then three uint32 payload fields a/b/c. Head to IDE: Hello (a = protocol
/// version, b = head pid, c = capability bits; sent once immediately after
/// connect; the IDE closes the socket on version mismatch, which powers the
/// head off) and FrameReady (a = sequence low 32, b = sequence high 32,
/// c = slot index; advisory — the header is authoritative). IDE to head:
/// TouchPress / TouchMove / TouchRelease (a = x, b = y in DEVICE pixels,
/// c = pointer id — the IDE's single mouse-finger always sends 0; the head
/// honors whatever id arrives, so multi-touch senders need no head change)
/// and KeyDown / KeyUp (a = the WinUI VirtualKey, b = the X11-style hardware
/// keycode = evdev scancode + 8, c = the Unicode codepoint for text input or
/// 0 for none; the head tracks modifier state itself from the modifier keys'
/// down/up, and V1 of the IDE sends no key messages at all — the capability
/// ships dormant so enabling keyboard forwarding later is an IDE-only
/// change). There is deliberately NO shutdown message: shutdown IS the socket
/// closing.
/// </para>
/// </summary>
internal static class FrameBufferEmulatorProtocol
{
    /// <summary>'C','B','F','E' read as a little-endian uint32.</summary>
    public const uint Magic = 0x45464243;

    /// <summary>The protocol version this implementation speaks.</summary>
    public const uint Version = 1;

    /// <summary>BGRA8888 premultiplied — the only pixel format in v1.</summary>
    public const uint PixelFormatBgra8888Premul = 1;

    /// <summary>Two slots: the head writes N % 2 while the IDE reads the other.</summary>
    public const uint SlotCount = 2;

    /// <summary>The fixed size of the shared-memory header, in bytes.</summary>
    public const int HeaderSize = 64;

    /// <summary>Pixel slots start on page boundaries.</summary>
    public const int PageSize = 4096;

    /// <summary>The fixed size of every socket message, in bytes.</summary>
    public const int MessageSize = 16;

    // Header field offsets, in bytes from the start of the file.
    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int WidthOffset = 8;
    public const int HeightOffset = 12;
    public const int StrideOffset = 16;
    public const int FormatOffset = 20;
    public const int SlotCountOffset = 24;
    public const int Slot0Offset = 28;
    public const int Slot1Offset = 32;
    public const int LatestSequenceOffset = 40;

    // Socket message types.
    public const uint HelloMessage = 1;
    public const uint FrameReadyMessage = 2;
    public const uint TouchPressMessage = 16;
    public const uint TouchMoveMessage = 17;
    public const uint TouchReleaseMessage = 18;
    public const uint KeyDownMessage = 32;
    public const uint KeyUpMessage = 33;

    // Hello capability bits: what the connected head KNOWS HOW to consume, so
    // a newer IDE can tailor what it offers without a head republish.
    public const uint CapabilityKeyboard = 1u << 0;
    public const uint CapabilityTouchPointIds = 1u << 1;

    // The launch-contract environment variable names.
    public const string ShmPathVariable = "CODEBRIX_FBEMU_SHM_PATH";
    public const string SocketPathVariable = "CODEBRIX_FBEMU_SOCKET_PATH";
    public const string WidthVariable = "CODEBRIX_FBEMU_WIDTH";
    public const string HeightVariable = "CODEBRIX_FBEMU_HEIGHT";

    /// <summary>
    /// Computes the layout of the shared-memory file for a device resolution:
    /// the two page-aligned slot offsets and the total file size (also rounded
    /// up to a page).
    /// </summary>
    public static (int Slot0, int Slot1, long FileSize) ComputeLayout(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        var slotBytes = (long) width * 4 * height;
        var slot0 = RoundUpToPage(HeaderSize);
        var slot1 = slot0 + RoundUpToPage(slotBytes);
        var fileSize = slot1 + RoundUpToPage(slotBytes);
        return ((int) slot0, (int) slot1, fileSize);
    }

    /// <summary>Encodes one 16-byte message into <paramref name="destination"/>.</summary>
    public static void WriteMessage(Span<byte> destination, uint type, uint a, uint b, uint c)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], a);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], c);
    }

    /// <summary>Decodes one 16-byte message from <paramref name="source"/>.</summary>
    public static (uint Type, uint A, uint B, uint C) ReadMessage(ReadOnlySpan<byte> source) => (
        BinaryPrimitives.ReadUInt32LittleEndian(source),
        BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[12..]));

    static long RoundUpToPage(long bytes) => (bytes + PageSize - 1) / PageSize * PageSize;
}
