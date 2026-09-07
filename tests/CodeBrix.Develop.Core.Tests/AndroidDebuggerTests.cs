//
// AndroidDebuggerTests.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeBrix.Develop.Core.Android;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

/// <summary>
/// The rules of debugging on an Android device: which payload an ABI needs,
/// where it goes on the device, the exact shell commands that put it there
/// and start it, and the parsers for what the device answers with. All of it
/// is decided here, so all of it can be checked without a phone attached.
/// </summary>
public class AndroidDebuggerTests
{
    const string Package = "com.codebrix.simpledebugapp";
    const string LibraryDirectory = "/data/app/~~abc==/com.codebrix.simpledebugapp-xyz==/lib/arm64";

    [Theory]
    [InlineData("arm64-v8a", "netcoredbg-android-arm64")]
    [InlineData("x86_64", "netcoredbg-android-x64")]
    public void PayloadFolderName_maps_each_supported_abi_to_its_own_folder(string abi, string folder)
        => AndroidDebugger.PayloadFolderName(abi).Should().Be(folder);

    [Theory]
    [InlineData("armeabi-v7a")]
    [InlineData("x86")]
    [InlineData("")]
    [InlineData(null)]
    public void PayloadFolderName_is_null_for_an_abi_with_no_payload(string abi)
    {
        //Assert — and the two questions agree with each other
        AndroidDebugger.PayloadFolderName(abi).Should().BeNull();
        AndroidDebugger.IsSupportedAbi(abi).Should().BeFalse();
        AndroidDebugger.LocalPayloadDirectory(abi).Should().BeNull();
    }

    [Fact]
    public void LocalPayloadDirectory_is_a_sibling_of_the_desktop_debugger_folder()
    {
        //Act — the desktop bundle is copied wholesale to SSH devices, so an
        //Android binary must never live inside it
        var directory = AndroidDebugger.LocalPayloadDirectory("arm64-v8a");

        //Assert
        directory.Should().Be(Path.Combine(AppContext.BaseDirectory, "netcoredbg-android-arm64"));
        Path.GetFileName(directory).Should().NotBe("netcoredbg");
    }

    [Theory]
    [InlineData("arm64-v8a", "CodeBrix.Develop.Debug.AndroidArm64")]
    [InlineData("x86_64", "CodeBrix.Develop.Debug.AndroidX64")]
    public void PayloadMissingMessage_names_the_package_that_carries_the_debugger(string abi, string package)
    {
        //Act
        var message = AndroidDebugger.PayloadMissingMessage(abi);

        //Assert
        message.Should().Contain(package);
        message.Should().Contain(AndroidDebugger.PayloadFolderName(abi));
    }

    [Fact]
    public void PayloadMissingMessage_says_so_when_the_abi_itself_is_unsupported()
        => AndroidDebugger.PayloadMissingMessage("mips64").Should().Contain("mips64");

    [Fact]
    public void The_device_paths_all_sit_inside_the_apps_own_sandbox()
    {
        //Assert — nothing but /data/local/tmp is outside it, and that is only
        //a staging post because adb cannot write into an app's data directory
        AndroidDebugger.StagingDirectory.Should().Be("/data/local/tmp/codebrix-ncdbg");
        AndroidDebugger.DebuggerDirectory(Package).Should().Be("/data/data/com.codebrix.simpledebugapp/ncdbg");
        AndroidDebugger.DebuggerPath(Package)
            .Should().Be("/data/data/com.codebrix.simpledebugapp/ncdbg/netcoredbg");
        AndroidDebugger.StampPath(Package)
            .Should().Be("/data/data/com.codebrix.simpledebugapp/ncdbg/payload.sha256");
        AndroidDebugger.AssemblyDirectory(Package, "arm64-v8a")
            .Should().Be("/data/data/com.codebrix.simpledebugapp/files/.__override__/arm64-v8a");
        AndroidDebugger.CacheDirectory(Package).Should().Be("/data/data/com.codebrix.simpledebugapp/cache");
        AndroidDebugger.DefaultDevicePort.Should().Be(4711);
        AndroidDebugger.DebuggerFileName.Should().Be("netcoredbg");
        AndroidDebugger.OutputFileName.Should().Be("ncdbg.out");
        AndroidDebugger.StampFileName.Should().Be("payload.sha256");
    }

    [Fact]
    public void The_activity_manager_commands_are_exactly_what_the_device_expects()
    {
        //Assert — set-debug-app is --persistent so it survives the force-stop
        //that follows it, and clear-debug-app is what undoes it
        AndroidDebugger.SetDebugAppCommand(Package)
            .Should().Be("am set-debug-app --persistent com.codebrix.simpledebugapp");
        AndroidDebugger.ClearDebugAppCommand.Should().Be("am clear-debug-app");
        AndroidDebugger.ForceStopCommand(Package).Should().Be("am force-stop com.codebrix.simpledebugapp");
        AndroidDebugger.ResolveActivityCommand(Package)
            .Should().Be("cmd package resolve-activity --brief com.codebrix.simpledebugapp");
        AndroidDebugger.StartActivityCommand("com.codebrix.simpledebugapp/.MainActivity")
            .Should().Be("am start -n com.codebrix.simpledebugapp/.MainActivity");
        AndroidDebugger.PidOfCommand(Package).Should().Be("pidof com.codebrix.simpledebugapp");
    }

    [Fact]
    public void The_sandbox_commands_all_go_through_run_as()
    {
        //Assert — only the app's own uid may read its /proc entry or write
        //into its data directory
        AndroidDebugger.ReadMapsCommand(Package, 12345)
            .Should().Be("run-as com.codebrix.simpledebugapp cat /proc/12345/maps");
        AndroidDebugger.ReadStampCommand(Package)
            .Should().Be("run-as com.codebrix.simpledebugapp cat "
                + "/data/data/com.codebrix.simpledebugapp/ncdbg/payload.sha256");
        AndroidDebugger.KillDebuggerCommand(Package)
            .Should().Be("run-as com.codebrix.simpledebugapp pkill -9 netcoredbg");
    }

    [Fact]
    public void InstallPayloadCommand_copies_the_staged_payload_in_and_makes_the_debugger_executable()
        => AndroidDebugger.InstallPayloadCommand(Package).Should().Be(
            "run-as com.codebrix.simpledebugapp sh -c "
            + "'mkdir -p /data/data/com.codebrix.simpledebugapp/ncdbg "
            + "&& cp /data/local/tmp/codebrix-ncdbg/* /data/data/com.codebrix.simpledebugapp/ncdbg/ "
            + "&& chmod 755 /data/data/com.codebrix.simpledebugapp/ncdbg/netcoredbg'");

    [Fact]
    public void WriteStampCommand_records_the_payload_version_beside_the_debugger()
        => AndroidDebugger.WriteStampCommand(Package, "0f1e2d").Should().Be(
            "run-as com.codebrix.simpledebugapp sh -c "
            + "'printf %s 0f1e2d > /data/data/com.codebrix.simpledebugapp/ncdbg/payload.sha256'");

    [Fact]
    public void ServerEnvironment_is_the_set_the_debuggers_android_layer_reads()
    {
        //Act
        var environment = AndroidDebugger.ServerEnvironment(Package, "arm64-v8a", LibraryDirectory);

        //Assert — order matters: it is the order the command line is built in
        var names = new List<string>();
        foreach (var variable in environment)
            names.Add(variable.Key);
        names.Should().Equal(new[]
        {
            "NETCOREDBG_ANDROID_ASSEMBLY_DIR", "NETCOREDBG_ANDROID_CLR_DIR", "LD_LIBRARY_PATH",
            "TMPDIR", "NETCOREDBG_COMMAND_TIMEOUT_MS",
        });
        environment[0].Value.Should().Be("/data/data/com.codebrix.simpledebugapp/files/.__override__/arm64-v8a");
        environment[1].Value.Should().Be(LibraryDirectory);
        environment[2].Value.Should().Be(LibraryDirectory);
        environment[3].Value.Should().Be("/data/data/com.codebrix.simpledebugapp/cache");
        environment[4].Value.Should().Be("60000");
    }

    [Fact]
    public void StartServerCommand_is_the_exact_line_the_device_runs()
        => AndroidDebugger.StartServerCommand(Package, "arm64-v8a", LibraryDirectory, 4711).Should().Be(
            "run-as com.codebrix.simpledebugapp sh -c 'env "
            + "NETCOREDBG_ANDROID_ASSEMBLY_DIR=/data/data/com.codebrix.simpledebugapp/files/.__override__/arm64-v8a "
            + "NETCOREDBG_ANDROID_CLR_DIR=/data/app/~~abc==/com.codebrix.simpledebugapp-xyz==/lib/arm64 "
            + "LD_LIBRARY_PATH=/data/app/~~abc==/com.codebrix.simpledebugapp-xyz==/lib/arm64 "
            + "TMPDIR=/data/data/com.codebrix.simpledebugapp/cache "
            + "NETCOREDBG_COMMAND_TIMEOUT_MS=60000 "
            + "/data/data/com.codebrix.simpledebugapp/ncdbg/netcoredbg --interpreter=vscode --server=4711 "
            + "> /data/data/com.codebrix.simpledebugapp/ncdbg/ncdbg.out 2>&1 &'");

    [Fact]
    public void StartServerCommand_appends_extra_environment_in_a_fixed_order()
    {
        //Arrange — a dictionary's enumeration order is not part of anyone's
        //contract, and the command line has to be the same every time
        var extra = new Dictionary<string, string>
        {
            ["ZED_LAST"] = "2",
            ["ALPHA_FIRST"] = "1",
        };

        //Act
        var command = AndroidDebugger.StartServerCommand(Package, "x86_64", "/data/app/x/lib/x86_64", 4711, extra);

        //Assert
        command.Should().Contain("NETCOREDBG_COMMAND_TIMEOUT_MS=60000 ALPHA_FIRST=1 ZED_LAST=2 ");
    }

    [Theory]
    [InlineData("com.example.it's")]
    [InlineData("com.example.\"quoted\"")]
    public void A_value_carrying_a_quote_is_refused_rather_than_mangled(string package)
    {
        //Arrange — the whole command is wrapped in single quotes for the
        //device's shell, so there is no escaping level left to spend
        var act = () => AndroidDebugger.StartServerCommand(package, "arm64-v8a", LibraryDirectory, 4711);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_quoted_library_directory_or_environment_value_is_refused_too()
    {
        //Arrange
        var quotedDirectory = () => AndroidDebugger.StartServerCommand(Package, "arm64-v8a", "/data/app/'/lib/arm64", 4711);
        var quotedValue = () => AndroidDebugger.StartServerCommand(Package, "arm64-v8a", LibraryDirectory, 4711,
            new Dictionary<string, string> { ["EXTRA"] = "a'b" });

        //Assert
        quotedDirectory.Should().Throw<ArgumentException>();
        quotedValue.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("arm64-v8a\r\n", "arm64-v8a")]
    [InlineData("  x86_64  ", "x86_64")]
    [InlineData("x86_64\n", "x86_64")]
    public void ParseAbi_strips_the_line_ending_adbs_shell_adds(string output, string abi)
        => AndroidDebugger.ParseAbi(output).Should().Be(abi);

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData(null)]
    public void ParseAbi_is_null_when_the_device_said_nothing(string output)
        => AndroidDebugger.ParseAbi(output).Should().BeNull();

    // /proc/net/tcp as a Pixel 4 XL printed it while the debugger listened on 4711 (0x1267).
    const string SocketTable =
        "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
        "   0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 12345 1 0000000000000000 100 0 0 10 0\n" +
        "   1: 00000000:1267 00000000:0000 0A 00000000:00000000 00:00000000 00000000 10190        0 67890 1 0000000000000000 100 0 0 10 0\n" +
        "   2: 0100007F:1267 0100007F:B3F2 01 00000000:00000000 00:00000000 00000000 10190        0 67891 1 0000000000000000 20 4 30 10 -1\n";

    [Fact]
    public void IsPortListening_finds_a_listener_on_the_port()
        => AndroidDebugger.IsPortListening(SocketTable, 4711).Should().BeTrue();

    [Fact]
    public void IsPortListening_ignores_established_connections_and_other_ports()
    {
        //Assert — 8080 (0x1F90) listens, 46066 (0xB3F2) is only the far end of an established connection
        AndroidDebugger.IsPortListening(SocketTable, 8080).Should().BeTrue();
        AndroidDebugger.IsPortListening(SocketTable, 46066).Should().BeFalse();
        AndroidDebugger.IsPortListening(SocketTable, 4712).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n")]
    public void IsPortListening_is_false_for_an_empty_table(string table)
        => AndroidDebugger.IsPortListening(table, 4711).Should().BeFalse();

    [Fact]
    public void PidOfDebuggerCommand_runs_as_the_app()
        => AndroidDebugger.PidOfDebuggerCommand(Package).Should().Be("run-as com.codebrix.simpledebugapp pidof netcoredbg");

    [Fact]
    public void ReadDebuggerOutputCommand_reads_the_output_file_in_the_sandbox()
        => AndroidDebugger.ReadDebuggerOutputCommand(Package)
            .Should().Be("run-as com.codebrix.simpledebugapp cat /data/data/com.codebrix.simpledebugapp/ncdbg/ncdbg.out");

    [Theory]
    [InlineData("12345\r\n", 12345)]
    [InlineData("  12345 23456 \n", 12345)]
    public void ParsePid_takes_the_first_process_of_the_package(string output, int pid)
        => AndroidDebugger.ParsePid(output).Should().Be(pid);

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("not-a-pid")]
    [InlineData(null)]
    public void ParsePid_is_null_when_the_app_is_not_running(string output)
        => AndroidDebugger.ParsePid(output).Should().BeNull();

    [Fact]
    public void ParseLibraryDirectory_finds_the_apk_library_folder_that_holds_libcoreclr()
    {
        //Arrange — a realistic excerpt: the install path carries randomized
        //segments, so the folder can only be discovered, never constructed
        var maps = string.Join("\n",
            "7f8a1c000000-7f8a1c021000 r--p 00000000 fd:03 1179656  /apex/com.android.runtime/lib64/bionic/libc.so",
            "7f8a1b000000-7f8a1b400000 r-xp 00000000 fd:03 2359310  "
                + "/data/app/~~abc==/com.codebrix.simpledebugapp-xyz==/lib/arm64/libcoreclr.so",
            "7f8a1b400000-7f8a1b500000 r--p 00400000 fd:03 2359310  "
                + "/data/app/~~abc==/com.codebrix.simpledebugapp-xyz==/lib/arm64/libcoreclr.so",
            "7f8a1a000000-7f8a1a100000 r-xp 00000000 fd:03 2359311  "
                + "/data/app/~~abc==/com.codebrix.simpledebugapp-xyz==/lib/arm64/libSystem.Native.so");

        //Act + Assert — the directory, not the file, and repeats collapse
        AndroidDebugger.ParseLibraryDirectory(maps).Should().Be(LibraryDirectory);
    }

    [Fact]
    public void ParseLibraryDirectory_reads_an_x64_emulators_maps_the_same_way()
        => AndroidDebugger.ParseLibraryDirectory(
                "7f00-7f01 r-xp 0 fd:03 1 /data/app/~~q==/com.codebrix.simpledebugapp-w==/lib/x86_64/libcoreclr.so")
            .Should().Be("/data/app/~~q==/com.codebrix.simpledebugapp-w==/lib/x86_64");

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ParseLibraryDirectory_is_null_when_there_is_nothing_to_read(string maps)
        => AndroidDebugger.ParseLibraryDirectory(maps).Should().BeNull();

    [Fact]
    public void ParseLibraryDirectory_is_null_for_a_process_running_on_monovm()
        // No CoreCLR mapped is exactly what a MonoVM app looks like, and it
        // is the honest answer that there is nothing to attach to.
        => AndroidDebugger.ParseLibraryDirectory(
                "7f00-7f01 r-xp 0 fd:03 1 /data/app/~~q==/com.x-w==/lib/arm64/libmonosgen-2.0.so")
            .Should().BeNull();

    [Fact]
    public void ParseResolvedActivity_takes_the_last_line_the_package_manager_prints()
        => AndroidDebugger.ParseResolvedActivity(
                "priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\r\n"
                + "com.codebrix.simpledebugapp/.MainActivity\r\n")
            .Should().Be("com.codebrix.simpledebugapp/.MainActivity");

    [Theory]
    [InlineData("No activity found")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseResolvedActivity_is_null_when_no_component_was_named(string output)
        => AndroidDebugger.ParseResolvedActivity(output).Should().BeNull();

    [Fact]
    public void ComputePayloadStamp_is_the_same_for_the_same_files_and_different_for_any_change()
    {
        //Arrange
        using var payload = new TemporaryPayload();
        payload.Write("netcoredbg", "binary contents");
        payload.Write("ManagedPart.dll", "managed contents");
        var original = AndroidDebugger.ComputePayloadStamp(payload.Path);

        //Act + Assert — stable across calls
        AndroidDebugger.ComputePayloadStamp(payload.Path).Should().Be(original);

        //Act + Assert — a changed file changes it
        payload.Write("netcoredbg", "rebuilt contents");
        var rebuilt = AndroidDebugger.ComputePayloadStamp(payload.Path);
        rebuilt.Should().NotBe(original);

        //Act + Assert — so does an added one
        payload.Write("libdbgshim.so", "managed contents");
        AndroidDebugger.ComputePayloadStamp(payload.Path).Should().NotBe(rebuilt);
    }

    [Fact]
    public void ComputePayloadStamp_separates_names_from_contents()
    {
        //Arrange — two folders whose concatenated contents are identical and
        //whose file names are not. Without the separator they would stamp the
        //same, and a changed payload would never reach the device.
        using var first = new TemporaryPayload();
        using var second = new TemporaryPayload();
        first.Write("ab", "cd");
        second.Write("abc", "d");

        //Assert
        AndroidDebugger.ComputePayloadStamp(first.Path)
            .Should().NotBe(AndroidDebugger.ComputePayloadStamp(second.Path));
    }

    [Fact]
    public void ComputePayloadStamp_is_lowercase_hex_sha256()
    {
        //Arrange
        using var payload = new TemporaryPayload();
        payload.Write("netcoredbg", "contents");

        //Act
        var stamp = AndroidDebugger.ComputePayloadStamp(payload.Path);

        //Assert — 32 bytes of SHA-256, and safe to put in a shell command
        stamp.Length.Should().Be(64);
        stamp.Should().Be(stamp.ToLowerInvariant());
        foreach (var character in stamp)
            "0123456789abcdef".Should().Contain(character.ToString());
    }

    [Fact]
    public void ComputePayloadStamp_refuses_a_folder_that_is_not_there()
    {
        //Arrange
        var act = () => AndroidDebugger.ComputePayloadStamp(
            Path.Combine(Path.GetTempPath(), "codebrix-no-such-payload-" + Guid.NewGuid().ToString("N")));

        //Assert
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void FindFreeHostPort_returns_a_port_that_can_actually_be_bound()
    {
        //Act
        var port = AndroidDebugger.FindFreeHostPort();

        //Assert — and it is really free, which is the whole point
        port.Should().BeGreaterThan(0);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    // A throwaway payload folder, so stamping can be checked against real
    // files without touching the IDE's own output.
    sealed class TemporaryPayload : IDisposable
    {
        internal TemporaryPayload()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "codebrix-android-payload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal void Write(string name, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents, Encoding.UTF8);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
