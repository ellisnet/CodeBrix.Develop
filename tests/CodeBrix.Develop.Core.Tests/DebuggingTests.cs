using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.Debugging;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class BreakpointStoreTests
{
    readonly BreakpointStore store = new BreakpointStore();
    readonly FilePath file = new FilePath("/tmp/example/Program.cs");

    [Fact]
    public void Toggle_sets_and_clears_a_breakpoint()
    {
        //Act + Assert
        store.Toggle(file, 12).Should().BeTrue();
        store.IsSet(file, 12).Should().BeTrue();
        store.Toggle(file, 12).Should().BeFalse();
        store.IsSet(file, 12).Should().BeFalse();
        store.GetFiles().Count.Should().Be(0);
    }

    [Fact]
    public void Lines_are_reported_ascending()
    {
        //Arrange
        store.Toggle(file, 30);
        store.Toggle(file, 5);
        store.Toggle(file, 12);

        //Assert
        store.GetLines(file).Should().Equal(new[] { 5, 12, 30 });
    }

    [Fact]
    public void Toggle_and_clear_raise_change_events()
    {
        //Arrange
        var changes = new List<string>();
        store.Changed += changedFile => changes.Add(changedFile);

        //Act
        store.Toggle(file, 3);
        store.Clear();

        //Assert
        changes.Count.Should().Be(2);
        store.GetLines(file).Count.Should().Be(0);
    }
}

public class HoverExpressionTests
{
    [Fact]
    public void Extracts_the_identifier_under_the_pointer()
    {
        //                 0123456789
        var line = "var total = count + 1;";
        HoverExpression.At(line, 13).Should().Be("count"); // on 'o'
        HoverExpression.At(line, 4).Should().Be("total");
        HoverExpression.At(line, 10).Should().BeNull();    // on '='
    }

    [Fact]
    public void Extends_left_through_member_chains()
    {
        var line = "return viewModel.Settings.Name;";
        HoverExpression.At(line, line.IndexOf("Name", StringComparison.Ordinal)).Should().Be("viewModel.Settings.Name");
        HoverExpression.At(line, line.IndexOf("Settings", StringComparison.Ordinal)).Should().Be("viewModel.Settings");
        HoverExpression.At(line, line.IndexOf("viewModel", StringComparison.Ordinal)).Should().Be("viewModel");
    }

    [Fact]
    public void Rejects_numeric_literals_and_out_of_range_positions()
    {
        HoverExpression.At("var x = 12345;", 10).Should().BeNull();
        HoverExpression.At("short", 99).Should().BeNull();
        HoverExpression.At("", 0).Should().BeNull();
        HoverExpression.At(null, 0).Should().BeNull();
    }
}

public class DapClientTests : IDisposable
{
    // The IDE side of the wire.
    readonly AnonymousPipeServerStream toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
    readonly AnonymousPipeServerStream fromAdapter = new AnonymousPipeServerStream(PipeDirection.In);
    readonly AnonymousPipeClientStream adapterIn;
    readonly AnonymousPipeClientStream adapterOut;
    readonly DapClient client;

    public DapClientTests()
    {
        adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        adapterOut = new AnonymousPipeClientStream(PipeDirection.Out, fromAdapter.ClientSafePipeHandle);
        client = new DapClient(fromAdapter, toAdapter);
        client.Start();
    }

    public void Dispose()
    {
        // Close the adapter's ends FIRST so the client's blocked read loop
        // sees end-of-stream and exits (mirrors production, where killing
        // the debugger process unblocks the reader); disposing a pipe with
        // a blocked synchronous reader deadlocks otherwise.
        adapterOut.Dispose();
        adapterIn.Dispose();
        client.Dispose();
    }

    void AdapterSend(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        adapterOut.Write(header);
        adapterOut.Write(payload);
        adapterOut.Flush();
    }

    JsonDocument AdapterReadRequest()
    {
        // Read headers byte-wise, then the body.
        var length = -1;
        var line = new StringBuilder();
        while (true)
        {
            var value = adapterIn.ReadByte();
            value.Should().NotBe(-1);
            if (value == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                    break;
                if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(text.Substring("Content-Length:".Length).Trim());
            }
            else
            {
                line.Append((char) value);
            }
        }
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
            read += adapterIn.Read(buffer, read, length - read);
        return JsonDocument.Parse(buffer);
    }

    [Fact]
    public async Task Request_and_response_are_correlated_and_body_returned()
    {
        //Act — issue the request, then play the adapter answering it.
        var requestTask = client.SendRequestAsync("threads", cancellationToken: TestContext.Current.CancellationToken);
        using var request = AdapterReadRequest();
        var seq = request.RootElement.GetProperty("seq").GetInt32();
        request.RootElement.GetProperty("command").GetString().Should().Be("threads");
        AdapterSend(new { type = "response", request_seq = seq, success = true, command = "threads", body = new { value = 42 } });

        //Assert
        var body = await requestTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        body.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Failed_responses_throw_with_the_adapter_message()
    {
        //Act
        var requestTask = client.SendRequestAsync("evaluate", cancellationToken: TestContext.Current.CancellationToken);
        using var request = AdapterReadRequest();
        var seq = request.RootElement.GetProperty("seq").GetInt32();
        AdapterSend(new { type = "response", request_seq = seq, success = false, command = "evaluate", message = "no can do" });

        //Assert
        var act = async () => await requestTask.WaitAsync(TimeSpan.FromSeconds(10));
        (await Record.ExceptionAsync(act)).Should().BeOfType<DapException>()
            .Which.Message.Should().Be("no can do");
    }

    [Fact]
    public async Task Events_are_dispatched_with_their_bodies()
    {
        //Arrange
        var received = new TaskCompletionSource<(string Name, int Thread)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (name, body) =>
            received.TrySetResult((name, body.GetProperty("threadId").GetInt32()));

        //Act
        AdapterSend(new { type = "event", @event = "stopped", body = new { reason = "breakpoint", threadId = 7 } });

        //Assert
        var (name, thread) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        name.Should().Be("stopped");
        thread.Should().Be(7);
    }
}

/// <summary>
/// The ATTACH handshake, which is what an Android session runs: the program
/// is already running (Android started it), so there is nothing to launch and
/// the adapter is only told which process to take over. The order is the part
/// that matters — the attach response does not arrive until configuration is
/// done, so the request has to be in flight while the breakpoints are sent.
/// </summary>
public class DebugSessionAttachTests : IDisposable
{
    // The IDE side of the wire; the "adapter" is this test.
    readonly AnonymousPipeServerStream toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
    readonly AnonymousPipeServerStream fromAdapter = new AnonymousPipeServerStream(PipeDirection.In);
    readonly AnonymousPipeClientStream adapterIn;
    readonly AnonymousPipeClientStream adapterOut;
    readonly PipeTransport transport;
    readonly List<string> commands = new List<string>();
    DebugSession session;

    public DebugSessionAttachTests()
    {
        adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        adapterOut = new AnonymousPipeClientStream(PipeDirection.Out, fromAdapter.ClientSafePipeHandle);
        transport = new PipeTransport(fromAdapter, toAdapter, CloseAdapterEnd);
    }

    // Ending the adapter is what unblocks the session's reader — the same
    // thing killing the debugger process does in production.
    void CloseAdapterEnd()
    {
        adapterOut.Dispose();
        adapterIn.Dispose();
    }

    public void Dispose()
    {
        CloseAdapterEnd();
        session?.Dispose();
        toAdapter.Dispose();
        fromAdapter.Dispose();
    }

    [Fact]
    public async Task AttachAsync_initializes_attaches_sets_breakpoints_then_finishes_configuration()
    {
        //Arrange
        var breakpoints = new Dictionary<string, IReadOnlyList<int>>
        {
            ["/src/SimpleDebugApp/MainActivity.cs"] = new[] { 47, 63 },
        };

        //Act — the session, and the adapter answering it
        var attaching = DebugSession.AttachAsync(transport, 4321, breakpoints, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        var initialize = ReadRequest();
        Respond(initialize);

        // The attach request is in flight from here on: its response is the
        // last thing to arrive, after configurationDone.
        var attach = ReadRequest();
        attach.Arguments.GetProperty("processId").GetInt32().Should().Be(4321);
        SendEvent("initialized");

        var setBreakpoints = ReadRequest();
        setBreakpoints.Arguments.GetProperty("source").GetProperty("path").GetString()
            .Should().Be("/src/SimpleDebugApp/MainActivity.cs");
        setBreakpoints.Arguments.GetProperty("breakpoints").GetArrayLength().Should().Be(2);
        Respond(setBreakpoints);

        var configurationDone = ReadRequest();
        Respond(configurationDone);
        Respond(attach);

        session = await attaching.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        //Assert
        commands.Should().Equal(new[] { "initialize", "attach", "setBreakpoints", "configurationDone" });
        session.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task AttachAsync_reports_an_adapter_that_refuses_the_attach_and_lets_go_of_it()
    {
        //Act — the real attach happens inside configurationDone, so that is
        //where an app the debugger cannot take over says so
        var attaching = DebugSession.AttachAsync(transport, 4321,
            new Dictionary<string, IReadOnlyList<int>>(), TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Respond(ReadRequest());          // initialize
        var attach = ReadRequest();      // in flight, never answered
        SendEvent("initialized");
        Fail(ReadRequest(), "process not debuggable");

        //Assert — the failure surfaces, and the transport was let go of
        var act = async () => await attaching;
        (await Record.ExceptionAsync(act)).Should().BeOfType<DapException>()
            .Which.Message.Should().Be("process not debuggable");
        attach.Command.Should().Be("attach");
        transport.Terminated.Should().BeTrue();
    }

    [Fact]
    public async Task SetExceptionBreakpointsAsync_sends_the_filter_names_and_clears_with_an_empty_list()
    {
        //Arrange — an attached session
        var attaching = DebugSession.AttachAsync(transport, 4321,
            new Dictionary<string, IReadOnlyList<int>>(), TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Respond(ReadRequest());          // initialize
        var attach = ReadRequest();      // attach, answered last
        SendEvent("initialized");
        Respond(ReadRequest());          // configurationDone
        Respond(attach);
        session = await attaching.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        //Act
        var settingAll = session.SetExceptionBreakpointsAsync(new[] { DebugSession.AllExceptionsFilter },
            TestContext.Current.CancellationToken);
        var setAll = ReadRequest();
        Respond(setAll);
        await settingAll.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var clearing = session.SetExceptionBreakpointsAsync(null, TestContext.Current.CancellationToken);
        var cleared = ReadRequest();
        Respond(cleared);
        await clearing.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        //Assert
        setAll.Command.Should().Be("setExceptionBreakpoints");
        setAll.Arguments.GetProperty("filters").GetArrayLength().Should().Be(1);
        setAll.Arguments.GetProperty("filters")[0].GetString().Should().Be("all");
        cleared.Command.Should().Be("setExceptionBreakpoints");
        cleared.Arguments.GetProperty("filters").GetArrayLength().Should().Be(0);
    }

    void Respond(AdapterRequest request) => AdapterSend(new
    {
        type = "response",
        request_seq = request.Seq,
        success = true,
        command = request.Command,
        body = new { },
    });

    void Fail(AdapterRequest request, string message) => AdapterSend(new
    {
        type = "response",
        request_seq = request.Seq,
        success = false,
        command = request.Command,
        message,
    });

    void SendEvent(string name) => AdapterSend(new { type = "event", @event = name, body = new { } });

    void AdapterSend(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        adapterOut.Write(header);
        adapterOut.Write(payload);
        adapterOut.Flush();
    }

    AdapterRequest ReadRequest()
    {
        var length = -1;
        var line = new StringBuilder();
        while (true)
        {
            var value = adapterIn.ReadByte();
            value.Should().NotBe(-1);
            if (value == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                    break;
                if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(text.Substring("Content-Length:".Length).Trim());
            }
            else
            {
                line.Append((char) value);
            }
        }
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
            read += adapterIn.Read(buffer, read, length - read);

        var document = JsonDocument.Parse(buffer);
        var request = new AdapterRequest
        {
            Seq = document.RootElement.GetProperty("seq").GetInt32(),
            Command = document.RootElement.GetProperty("command").GetString(),
            Arguments = document.RootElement.TryGetProperty("arguments", out var arguments)
                ? arguments.Clone()
                : default,
        };
        document.Dispose();
        commands.Add(request.Command);
        return request;
    }

    sealed class AdapterRequest
    {
        internal int Seq { get; set; }

        internal string Command { get; set; }

        internal JsonElement Arguments { get; set; }
    }

    // A transport over the test's pipes; terminating it ends the "adapter",
    // exactly as killing the debugger process does.
    sealed class PipeTransport : IDebuggerTransport
    {
        readonly Action onTerminate;

        internal PipeTransport(Stream input, Stream output, Action onTerminate)
        {
            Input = input;
            Output = output;
            this.onTerminate = onTerminate;
        }

        public Stream Input { get; }

        public Stream Output { get; }

        internal bool Terminated { get; private set; }

        public void Terminate()
        {
            Terminated = true;
            onTerminate();
        }

        public void Dispose() => Terminate();
    }
}
