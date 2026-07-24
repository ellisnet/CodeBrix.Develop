using System;
using CodeBrix.Develop.Emulation.FrameBuffer.Transport;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

public class FrameBufferEmulatorProtocolTests
{
    [Theory]
    [InlineData(720, 1280)]
    [InlineData(1280, 720)]
    [InlineData(1200, 1920)]
    [InlineData(1080, 1920)]
    public void The_layout_page_aligns_two_non_overlapping_slots(int width, int height)
    {
        //Act
        var (slot0, slot1, fileSize) = FrameBufferEmulatorProtocol.ComputeLayout(width, height);

        //Assert
        var frameBytes = width * 4 * height;
        (slot0 % FrameBufferEmulatorProtocol.PageSize).Should().Be(0);
        (slot1 % FrameBufferEmulatorProtocol.PageSize).Should().Be(0);
        slot0.Should().BeGreaterThanOrEqualTo(FrameBufferEmulatorProtocol.HeaderSize);
        slot1.Should().BeGreaterThanOrEqualTo(slot0 + frameBytes);
        fileSize.Should().BeGreaterThanOrEqualTo(slot1 + frameBytes);
    }

    [Fact]
    public void Messages_round_trip_through_their_wire_encoding()
    {
        //Arrange
        var buffer = new byte[FrameBufferEmulatorProtocol.MessageSize];

        //Act
        FrameBufferEmulatorProtocol.WriteMessage(buffer,
            FrameBufferEmulatorProtocol.TouchMoveMessage, 419, 861, 0);
        var (type, a, b, c) = FrameBufferEmulatorProtocol.ReadMessage(buffer);

        //Assert
        type.Should().Be(FrameBufferEmulatorProtocol.TouchMoveMessage);
        a.Should().Be(419u);
        b.Should().Be(861u);
        c.Should().Be(0u);
    }

    [Fact]
    public void The_magic_reads_as_CBFE_in_little_endian_bytes()
    {
        //Act
        var bytes = BitConverter.GetBytes(FrameBufferEmulatorProtocol.Magic);

        //Assert
        BitConverter.IsLittleEndian.Should().BeTrue();
        ((char) bytes[0]).Should().Be('C');
        ((char) bytes[1]).Should().Be('B');
        ((char) bytes[2]).Should().Be('F');
        ((char) bytes[3]).Should().Be('E');
    }

    [Fact]
    public void A_degenerate_resolution_is_rejected()
    {
        //Act
        var act = () => FrameBufferEmulatorProtocol.ComputeLayout(0, 1280);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
