using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Emulation.FrameBuffer;
using CodeBrix.Develop.Emulation.FrameBuffer.Transport;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

/// <summary>
/// Loopback tests of the IDE's transport server: these play the emulated
/// head's role — connect to the socket, speak the handshake, publish frames
/// through the shared memory — and assert the session's observable behaviour,
/// with no GTK involved.
/// </summary>
public class FrameBufferEmulatorSessionTests
{
    const int Width = 72;
    const int Height = 128;

    static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_header_is_written_for_the_app_to_validate()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);

        //Act
        var shmPath = session.EnvironmentVariables[FrameBufferEmulatorProtocol.ShmPathVariable];
        var header = new byte[FrameBufferEmulatorProtocol.HeaderSize];
        using (var stream = new FileStream(shmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.ReadExactly(header);
        }

        //Assert
        BitConverter.ToUInt32(header, FrameBufferEmulatorProtocol.MagicOffset)
            .Should().Be(FrameBufferEmulatorProtocol.Magic);
        BitConverter.ToUInt32(header, FrameBufferEmulatorProtocol.VersionOffset)
            .Should().Be(FrameBufferEmulatorProtocol.Version);
        BitConverter.ToUInt32(header, FrameBufferEmulatorProtocol.WidthOffset).Should().Be((uint) Width);
        BitConverter.ToUInt32(header, FrameBufferEmulatorProtocol.HeightOffset).Should().Be((uint) Height);
        BitConverter.ToUInt32(header, FrameBufferEmulatorProtocol.StrideOffset).Should().Be((uint) (Width * 4));
        BitConverter.ToInt64(header, FrameBufferEmulatorProtocol.LatestSequenceOffset).Should().Be(0L);
    }

    [Fact]
    public void The_environment_variables_carry_the_launch_contract()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);

        //Act
        var variables = session.EnvironmentVariables;

        //Assert
        File.Exists(variables[FrameBufferEmulatorProtocol.ShmPathVariable]).Should().BeTrue();
        File.Exists(variables[FrameBufferEmulatorProtocol.SocketPathVariable]).Should().BeTrue();
        variables[FrameBufferEmulatorProtocol.WidthVariable].Should().Be("72");
        variables[FrameBufferEmulatorProtocol.HeightVariable].Should().Be("128");
    }

    [Fact]
    public async Task A_frame_published_by_the_app_reaches_the_frame_source()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        using var app = await ConnectAndGreetAsync(session);

        //Act — play the head: write frame 1 into slot 1, then publish it.
        var (_, slot1, _) = FrameBufferEmulatorProtocol.ComputeLayout(Width, Height);
        var frameBytes = session.FrameSizeInBytes;
        using (var mapped = OpenSharedMemory(session))
        using (var accessor = mapped.CreateViewAccessor())
        {
            for (var i = 0; i < frameBytes; i++)
                accessor.Write(slot1 + i, (byte) (i % 251));
            accessor.Write(FrameBufferEmulatorProtocol.LatestSequenceOffset, 1L);
        }

        var destination = new byte[frameBytes];
        var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            long lastSequence = 0;
            var copied = ((IFrameBufferFrameSource) session).TryCopyLatestFrame(
                handle.AddrOfPinnedObject(), frameBytes, ref lastSequence);

            //Assert
            copied.Should().BeTrue();
            lastSequence.Should().Be(1L);
            destination[0].Should().Be(0);
            destination[500].Should().Be((byte) (500 % 251));

            // The same sequence is not copied twice.
            ((IFrameBufferFrameSource) session).TryCopyLatestFrame(
                handle.AddrOfPinnedObject(), frameBytes, ref lastSequence).Should().BeFalse();
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public async Task Frame_ready_messages_surface_the_sequence()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        var frameReady = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameReady += sequence => frameReady.TrySetResult(sequence);
        using var app = await ConnectAndGreetAsync(session);

        //Act
        SendMessage(app, FrameBufferEmulatorProtocol.FrameReadyMessage, 5, 0, 1);

        //Assert
        (await frameReady.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken))
            .Should().Be(5L);
    }

    [Fact]
    public async Task The_heads_hello_capabilities_are_surfaced()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connected += () => connected.TrySetResult();
        using var app = await ConnectAsync(session);

        //Act — a head declaring the dormant keyboard + touch-id capabilities.
        SendMessage(app, FrameBufferEmulatorProtocol.HelloMessage,
            FrameBufferEmulatorProtocol.Version, 12345,
            FrameBufferEmulatorProtocol.CapabilityKeyboard
                | FrameBufferEmulatorProtocol.CapabilityTouchPointIds);
        await connected.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken);

        //Assert
        (session.HeadCapabilities & FrameBufferEmulatorProtocol.CapabilityKeyboard).Should()
            .Be(FrameBufferEmulatorProtocol.CapabilityKeyboard);
        (session.HeadCapabilities & FrameBufferEmulatorProtocol.CapabilityTouchPointIds).Should()
            .Be(FrameBufferEmulatorProtocol.CapabilityTouchPointIds);
    }

    [Fact]
    public async Task Keys_reach_the_app_with_the_key_down_wire_shape()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        using var app = await ConnectAndGreetAsync(session);

        //Act — dormant in the IDE today, but the wire shape is shipped.
        session.SendKey(pressed: true, virtualKey: 65, hardwareKeyCode: 38, unicodeCodepoint: 'a');

        //Assert
        var message = await ReceiveMessageAsync(app);
        message.Type.Should().Be(FrameBufferEmulatorProtocol.KeyDownMessage);
        message.A.Should().Be(65u);
        message.B.Should().Be(38u);
        message.C.Should().Be((uint) 'a');
    }

    [Fact]
    public async Task Touches_reach_the_app_over_the_socket_in_device_pixels()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        using var app = await ConnectAndGreetAsync(session);

        //Act
        session.SendTouch(FrameBufferTouchKind.Press, 41, 86);

        //Assert
        var message = await ReceiveMessageAsync(app);
        message.Type.Should().Be(FrameBufferEmulatorProtocol.TouchPressMessage);
        message.A.Should().Be(41u);
        message.B.Should().Be(86u);
    }

    [Fact]
    public async Task The_app_dying_on_its_own_raises_disconnected()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += () => disconnected.TrySetResult();
        var app = await ConnectAndGreetAsync(session);

        //Act — the device powered itself off.
        app.Dispose();

        //Assert
        await disconnected.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_is_loss_of_power_and_never_raises_disconnected()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        var disconnected = false;
        session.Disconnected += () => disconnected = true;
        using var app = await ConnectAndGreetAsync(session);

        //Act
        session.Shutdown();

        //Assert — the app sees end-of-file (its cue to hard-exit)...
        var buffer = new byte[FrameBufferEmulatorProtocol.MessageSize];
        var received = await app.ReceiveAsync(buffer, SocketFlags.None,
            TestContext.Current.CancellationToken);
        received.Should().Be(0);

        // ...and the IDE-initiated stop is not reported as an app death.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        disconnected.Should().BeFalse();
    }

    [Fact]
    public async Task A_version_mismatch_powers_the_head_off()
    {
        //Arrange
        using var session = new FrameBufferEmulatorSession(Width, Height);
        var connected = false;
        session.Connected += () => connected = true;
        using var app = await ConnectAsync(session);

        //Act
        SendMessage(app, FrameBufferEmulatorProtocol.HelloMessage, 99, 0, 0);

        //Assert — the server closes; the pretend head reads end-of-file.
        var buffer = new byte[FrameBufferEmulatorProtocol.MessageSize];
        var received = await app.ReceiveAsync(buffer, SocketFlags.None,
            TestContext.Current.CancellationToken);
        received.Should().Be(0);
        connected.Should().BeFalse();
    }

    static async Task<Socket> ConnectAsync(FrameBufferEmulatorSession session)
    {
        var socketPath = session.EnvironmentVariables[FrameBufferEmulatorProtocol.SocketPathVariable];
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath),
            TestContext.Current.CancellationToken);
        return socket;
    }

    static async Task<Socket> ConnectAndGreetAsync(FrameBufferEmulatorSession session)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connected += () => connected.TrySetResult();
        var socket = await ConnectAsync(session);
        SendMessage(socket, FrameBufferEmulatorProtocol.HelloMessage,
            FrameBufferEmulatorProtocol.Version, 12345, 0);
        await connected.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken);
        return socket;
    }

    static void SendMessage(Socket socket, uint type, uint a, uint b, uint c)
    {
        Span<byte> message = stackalloc byte[FrameBufferEmulatorProtocol.MessageSize];
        FrameBufferEmulatorProtocol.WriteMessage(message, type, a, b, c);
        socket.Send(message);
    }

    static async Task<(uint Type, uint A, uint B, uint C)> ReceiveMessageAsync(Socket socket)
    {
        var buffer = new byte[FrameBufferEmulatorProtocol.MessageSize];
        var received = 0;
        while (received < buffer.Length)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None,
                TestContext.Current.CancellationToken);
            count.Should().BeGreaterThan(0);
            received += count;
        }
        return FrameBufferEmulatorProtocol.ReadMessage(buffer);
    }

    static MemoryMappedFile OpenSharedMemory(FrameBufferEmulatorSession session)
    {
        var shmPath = session.EnvironmentVariables[FrameBufferEmulatorProtocol.ShmPathVariable];
        var stream = new FileStream(shmPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return MemoryMappedFile.CreateFromFile(stream, null, 0,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
    }
}
