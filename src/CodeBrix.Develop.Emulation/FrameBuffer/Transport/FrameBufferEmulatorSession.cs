//
// FrameBufferEmulatorSession.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Emulation.FrameBuffer.Transport;

/// <summary>
/// The IDE's side of one emulated frame-buffer application: it creates the
/// shared-memory file and the listening Unix socket BEFORE the app is
/// launched, hands the app their paths through <see cref="EnvironmentVariables"/>,
/// receives the app's frames (through the shared memory, surfaced to the
/// emulator window as an <see cref="IFrameBufferFrameSource"/>) and forwards
/// single-touch input back over the socket.
/// <para>
/// One session serves one app launch. <see cref="Shutdown"/> closes the
/// socket, which the head experiences as loss of power — it hard-exits on
/// end-of-file; the caller keeps a SIGKILL backstop for anything still alive
/// afterwards. <see cref="Disconnected"/> is raised only when the app went
/// away on its OWN — the device powered itself off — never in response to
/// <see cref="Shutdown"/>.
/// </para>
/// <para>
/// <see cref="Connected"/>, <see cref="Disconnected"/> and
/// <see cref="FrameReady"/> are raised on a background thread; marshal to the
/// UI thread before touching any widget. Detach the session from the emulator
/// window BEFORE disposing it — the window polls
/// <see cref="TryCopyLatestFrame"/> from its draw tick.
/// </para>
/// </summary>
public sealed class FrameBufferEmulatorSession : IFrameBufferFrameSource, IDisposable
{
    static int sessionCounter;

    readonly int deviceWidth;
    readonly int deviceHeight;
    readonly int frameBytes;
    readonly int slot0Offset;
    readonly int slot1Offset;

    readonly string sessionDirectory;
    readonly string shmPath;
    readonly string socketPath;

    readonly MemoryMappedFile sharedMemory;
    readonly MemoryMappedViewAccessor accessor;
    readonly IntPtr basePointer;

    readonly Socket listener;
    readonly Task readLoop;
    readonly CancellationTokenSource lifetime = new();

    // Serializes touch sends; and serializes frame reads against unmapping.
    readonly object sendLock = new();
    readonly object frameLock = new();

    volatile Socket? client;
    volatile bool connected;
    volatile bool shutdownRequested;
    volatile bool disposed;

    /// <summary>
    /// Raised (on a background thread) when the app has connected and passed
    /// its version handshake — the emulated device is on.
    /// </summary>
    public event Action? Connected;

    /// <summary>
    /// Raised (on a background thread) when the app went away on its own —
    /// its process exited, crashed, or closed the socket. Never raised in
    /// response to <see cref="Shutdown"/>.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Raised (on a background thread) for each FrameReady message, with the
    /// frame's sequence number. Advisory: the emulator window polls the shared
    /// memory on its own tick and does not need this to display frames.
    /// </summary>
    public event Action<long>? FrameReady;

    /// <summary>
    /// Creates the transport for one emulated app launch: the shared-memory
    /// file (header written, slots zeroed) and the listening socket, both in a
    /// fresh per-session directory under XDG_RUNTIME_DIR.
    /// </summary>
    public FrameBufferEmulatorSession(int deviceWidth, int deviceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deviceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deviceHeight, 0);
        this.deviceWidth = deviceWidth;
        this.deviceHeight = deviceHeight;
        frameBytes = deviceWidth * 4 * deviceHeight;

        long fileSize;
        (slot0Offset, slot1Offset, fileSize) =
            FrameBufferEmulatorProtocol.ComputeLayout(deviceWidth, deviceHeight);

        sessionDirectory = CreateSessionDirectory();
        shmPath = Path.Combine(sessionDirectory, "screen");
        socketPath = Path.Combine(sessionDirectory, "input.sock");

        // FileShare.ReadWrite is load-bearing: the app process opens and maps
        // this same file, which the path-based CreateFromFile overloads would
        // forbid by holding the file exclusively.
        var stream = new FileStream(shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.SetLength(fileSize);
        sharedMemory = MemoryMappedFile.CreateFromFile(stream, null, fileSize,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        accessor = sharedMemory.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);
        basePointer = AcquirePointer(accessor);
        WriteHeader();

        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        readLoop = Task.Run(() => AcceptAndReadAsync(lifetime.Token));
    }

    /// <summary>The emulated device's width, in pixels.</summary>
    public int DeviceWidth => deviceWidth;

    /// <summary>The emulated device's height, in pixels.</summary>
    public int DeviceHeight => deviceHeight;

    /// <summary>One device frame's size in bytes (width * height * 4).</summary>
    public int FrameSizeInBytes => frameBytes;

    /// <summary>
    /// The launch contract: the environment variables the app process must be
    /// started with so its emulated head finds this session.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables =>
        new Dictionary<string, string>
        {
            [FrameBufferEmulatorProtocol.ShmPathVariable] = shmPath,
            [FrameBufferEmulatorProtocol.SocketPathVariable] = socketPath,
            [FrameBufferEmulatorProtocol.WidthVariable] =
                deviceWidth.ToString(CultureInfo.InvariantCulture),
            [FrameBufferEmulatorProtocol.HeightVariable] =
                deviceHeight.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>True while the app is connected and past its handshake.</summary>
    public bool IsConnected => connected;

    /// <summary>
    /// Sends one touch event to the app, in DEVICE pixels. Does nothing when
    /// no app is connected; a send failure is not an error here — the read
    /// loop notices the dead socket and raises <see cref="Disconnected"/>.
    /// </summary>
    public void SendTouch(FrameBufferTouchKind kind, int x, int y)
    {
        if (disposed || !connected || client is not { } connection)
            return;

        var type = kind switch
        {
            FrameBufferTouchKind.Press => FrameBufferEmulatorProtocol.TouchPressMessage,
            FrameBufferTouchKind.Move => FrameBufferEmulatorProtocol.TouchMoveMessage,
            _ => FrameBufferEmulatorProtocol.TouchReleaseMessage,
        };
        Span<byte> message = stackalloc byte[FrameBufferEmulatorProtocol.MessageSize];
        FrameBufferEmulatorProtocol.WriteMessage(message, type,
            (uint) Math.Max(0, x), (uint) Math.Max(0, y), 0);
        lock (sendLock)
        {
            try
            {
                connection.Send(message);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <inheritdoc/>
    public bool TryCopyLatestFrame(IntPtr destination, int destinationBytes, ref long lastSequence)
    {
        if (disposed || destination == IntPtr.Zero)
            return false;
        if (destinationBytes != frameBytes)
            throw new ArgumentException(
                $"Destination is {destinationBytes} bytes; a device frame is {frameBytes}.",
                nameof(destinationBytes));

        lock (frameLock)
        {
            if (disposed)
                return false;
            unsafe
            {
                var basePtr = (byte*) basePointer;
                ref var latestSequence = ref *(long*) (basePtr + FrameBufferEmulatorProtocol.LatestSequenceOffset);
                var sequence = Volatile.Read(ref latestSequence);
                if (sequence == 0 || sequence == lastSequence)
                    return false;

                // Two slots: the head writes frame N into slot N % 2, so the
                // slot just published is stable unless the head got two MORE
                // frames ahead while this copy ran. Detect that and re-copy;
                // after a few attempts accept the tear for this one tick.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var slotOffset = sequence % 2 == 0 ? slot0Offset : slot1Offset;
                    Buffer.MemoryCopy(basePtr + slotOffset, (void*) destination,
                        destinationBytes, frameBytes);
                    var check = Volatile.Read(ref latestSequence);
                    if (check < sequence + 2)
                        break;
                    sequence = check;
                }
                lastSequence = sequence;
                return true;
            }
        }
    }

    /// <summary>
    /// Pulls the plug: closes the app's socket — which the head experiences as
    /// loss of power and hard-exits on — and stops listening. Idempotent, and
    /// deliberately does NOT raise <see cref="Disconnected"/>.
    /// </summary>
    public void Shutdown()
    {
        shutdownRequested = true;
        connected = false;
        lifetime.Cancel();
        if (client is { } connection)
        {
            try
            {
                connection.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            connection.Dispose();
        }
        listener.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
            return;
        Shutdown();
        lock (frameLock)
        {
            disposed = true;
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            sharedMemory.Dispose();
        }
        lifetime.Dispose();
        try
        {
            Directory.Delete(sessionDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static string CreateSessionDirectory()
    {
        // A Unix socket path is limited to ~108 bytes, so prefer the short
        // XDG_RUNTIME_DIR (/run/user/<uid>) and fall back to the temp folder.
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var baseDir = !string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir)
            ? runtimeDir
            : Path.GetTempPath();
        var directory = Path.Combine(baseDir, "codebrix-develop",
            $"fbemu-{Environment.ProcessId}-{Interlocked.Increment(ref sessionCounter)}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    static IntPtr AcquirePointer(MemoryMappedViewAccessor accessor)
    {
        unsafe
        {
            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            return (IntPtr) pointer;
        }
    }

    void WriteHeader()
    {
        accessor.Write(FrameBufferEmulatorProtocol.MagicOffset, FrameBufferEmulatorProtocol.Magic);
        accessor.Write(FrameBufferEmulatorProtocol.VersionOffset, FrameBufferEmulatorProtocol.Version);
        accessor.Write(FrameBufferEmulatorProtocol.WidthOffset, (uint) deviceWidth);
        accessor.Write(FrameBufferEmulatorProtocol.HeightOffset, (uint) deviceHeight);
        accessor.Write(FrameBufferEmulatorProtocol.StrideOffset, (uint) (deviceWidth * 4));
        accessor.Write(FrameBufferEmulatorProtocol.FormatOffset,
            FrameBufferEmulatorProtocol.PixelFormatBgra8888Premul);
        accessor.Write(FrameBufferEmulatorProtocol.SlotCountOffset, FrameBufferEmulatorProtocol.SlotCount);
        accessor.Write(FrameBufferEmulatorProtocol.Slot0Offset, (uint) slot0Offset);
        accessor.Write(FrameBufferEmulatorProtocol.Slot1Offset, (uint) slot1Offset);
        accessor.Write(FrameBufferEmulatorProtocol.LatestSequenceOffset, 0L);
        accessor.Flush();
    }

    async Task AcceptAndReadAsync(CancellationToken token)
    {
        Socket connection;
        try
        {
            connection = await listener.AcceptAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SocketException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        client = connection;
        var sawHello = false;
        var buffer = new byte[FrameBufferEmulatorProtocol.MessageSize];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await ReceiveExactlyAsync(connection, buffer, token).ConfigureAwait(false))
                    break; // end-of-file: the app went away

                var (type, a, b, _) = FrameBufferEmulatorProtocol.ReadMessage(buffer);
                if (!sawHello)
                {
                    if (type != FrameBufferEmulatorProtocol.HelloMessage
                        || a != FrameBufferEmulatorProtocol.Version)
                    {
                        break; // refuse: closing the socket powers the head off
                    }
                    sawHello = true;
                    connected = true;
                    Connected?.Invoke();
                    continue;
                }
                if (type == FrameBufferEmulatorProtocol.FrameReadyMessage)
                    FrameReady?.Invoke((long) (((ulong) b << 32) | a));
                // Unknown message types are ignored, for forward compatibility.
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            connected = false;
            connection.Dispose();
            if (sawHello && !shutdownRequested && !disposed)
                Disconnected?.Invoke();
        }
    }

    static async Task<bool> ReceiveExactlyAsync(Socket socket, byte[] buffer, CancellationToken token)
    {
        var received = 0;
        while (received < buffer.Length)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, token)
                .ConfigureAwait(false);
            if (count == 0)
                return false;
            received += count;
        }
        return true;
    }
}
