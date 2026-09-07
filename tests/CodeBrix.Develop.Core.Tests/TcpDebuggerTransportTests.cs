//
// TcpDebuggerTransportTests.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.Android;
using CodeBrix.Develop.Core.Debugging;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

/// <summary>
/// The transport an Android session speaks DAP over: a loopback socket, at
/// the near end of an adb port forward. The far end is started in the
/// background on a phone, so "not listening yet" is the normal state of
/// affairs for the first second or so — waiting it out is the behaviour, not
/// a workaround.
/// </summary>
public class TcpDebuggerTransportTests
{
    [Fact]
    public async Task ConnectAsync_connects_to_a_listening_adapter()
    {
        //Arrange
        using var listener = new AdapterListener();

        //Act
        using var transport = await TcpDebuggerTransport.ConnectAsync(listener.Port, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptAsync();

        //Assert
        transport.Port.Should().Be(listener.Port);
        transport.Input.Should().NotBeNull();
        // One socket serves both directions, which is what a socket IS.
        transport.Output.Should().BeSameAs(transport.Input);
    }

    [Fact]
    public async Task The_socket_carries_both_directions_of_the_protocol()
    {
        //Arrange
        using var listener = new AdapterListener();
        using var transport = await TcpDebuggerTransport.ConnectAsync(listener.Port, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptAsync();
        var adapter = accepted.GetStream();

        //Act — the IDE writes, the adapter answers
        transport.Output.WriteByte(7);
        transport.Output.Flush();
        var received = adapter.ReadByte();
        adapter.WriteByte(9);
        adapter.Flush();

        //Assert
        received.Should().Be(7);
        transport.Input.ReadByte().Should().Be(9);
    }

    [Fact]
    public async Task ConnectAsync_waits_for_an_adapter_that_is_not_listening_yet()
    {
        //Arrange — the port is free, and nothing is on it when the connect starts
        var port = AndroidDebugger.FindFreeHostPort();
        var connecting = TcpDebuggerTransport.ConnectAsync(port, TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);

        //Act — the adapter turns up late, exactly as one starting on a phone does
        using var listener = new AdapterListener(port);
        using var transport = await connecting;
        using var accepted = await listener.AcceptAsync();

        //Assert
        transport.Port.Should().Be(port);
    }

    [Fact]
    public async Task ConnectAsync_gives_up_when_nothing_ever_listens()
    {
        //Arrange
        var port = AndroidDebugger.FindFreeHostPort();

        //Act
        var act = async () => await TcpDebuggerTransport.ConnectAsync(port, TimeSpan.FromMilliseconds(750),
            TestContext.Current.CancellationToken);

        //Assert — and it names the port, which is what makes the log useful
        (await Record.ExceptionAsync(act)).Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain(port.ToString());
    }

    [Fact]
    public async Task Terminate_and_dispose_are_both_safe_to_repeat()
    {
        //Arrange
        using var listener = new AdapterListener();
        var transport = await TcpDebuggerTransport.ConnectAsync(listener.Port, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptAsync();

        //Act + Assert — the session's teardown calls Terminate and then
        //Dispose, and application shutdown may call either again
        transport.Terminate();
        transport.Terminate();
        transport.Dispose();
        transport.Dispose();
    }

    // A stand-in for the debugger listening on the device's forwarded port.
    sealed class AdapterListener : IDisposable
    {
        readonly TcpListener listener;

        internal AdapterListener() : this(0)
        {
        }

        internal AdapterListener(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint) listener.LocalEndpoint).Port;
        }

        internal int Port { get; }

        internal Task<TcpClient> AcceptAsync() => listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

        public void Dispose() => listener.Stop();
    }
}
