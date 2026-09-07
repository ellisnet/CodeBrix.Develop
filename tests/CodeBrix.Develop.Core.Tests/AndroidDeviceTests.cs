using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.Android;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class AndroidDeviceTests
{
    // Real output, a phone and a running emulator attached at once.
    const string TwoDevices = """
        List of devices attached
        RFCRB0MPHYH            device usb:1-12 product:r8quex model:SM_G781U1 device:r8q transport_id:1
        emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:2

        """;

    [Fact]
    public void ParseDevices_reads_both_a_phone_and_an_emulator()
    {
        var devices = AndroidDebugBridge.ParseDevices(TwoDevices);

        devices.Should().HaveCount(2);
        devices[0].Serial.Should().Be("RFCRB0MPHYH");
        devices[0].Model.Should().Be("SM_G781U1");
        devices[0].Product.Should().Be("r8quex");
        devices[0].IsReady.Should().BeTrue();
        devices[1].Serial.Should().Be("emulator-5554");
        devices[1].Model.Should().Be("sdk_gphone64_x86_64");
    }

    [Fact]
    public void DisplayName_is_the_model_then_the_serial_for_a_phone()
    {
        var devices = AndroidDebugBridge.ParseDevices(TwoDevices);

        // Underscores become spaces; the casing the phone reported is kept.
        devices[0].DisplayName.Should().Be("SM G781U1 - RFCRB0MPHYH");
    }

    [Fact]
    public void DisplayName_is_the_avd_name_then_the_serial_for_an_emulator()
    {
        // Every emulator reports the same generic model, so the AVD id is the
        // only thing that tells one from another.
        var emulator = new AndroidDevice("emulator-5554", "device",
            "sdk_gphone64_x86_64", "sdk_gphone64_x86_64", "15-inch_HD_Device");

        emulator.IsEmulator.Should().BeTrue();
        emulator.DisplayName.Should().Be("15-inch HD Device - emulator-5554");
    }

    [Fact]
    public void An_emulator_without_a_known_avd_name_falls_back_to_its_model()
    {
        var emulator = new AndroidDevice("emulator-5554", "device",
            "sdk_gphone64_x86_64", "sdk_gphone64_x86_64");

        emulator.IsEmulator.Should().BeFalse();
        emulator.DisplayName.Should().Be("sdk gphone64 x86 64 - emulator-5554");
    }

    [Fact]
    public void DisplayName_falls_back_to_the_serial_when_adb_reports_no_model()
        => new AndroidDevice("RFCRB0MPHYH", "device", model: null, product: null)
            .DisplayName.Should().Be("RFCRB0MPHYH");

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("offline")]
    [InlineData("no")]
    public void A_device_that_is_not_ready_says_so_in_its_name(string state)
    {
        var device = new AndroidDevice("RFCRB0MPHYH", state, "SM_G781U1", "r8quex");

        device.IsReady.Should().BeFalse();
        device.DisplayName.Should().Be($"SM G781U1 - RFCRB0MPHYH ({state})");
    }

    [Fact]
    public void Nothing_is_title_cased()
        // Deliberate: the casing an emulator or a manufacturer chose is the
        // casing shown, so "15-inch" does not become "15-Inch".
        => new AndroidDevice("emulator-5554", "device", "m", "p", "15-inch_HD_Device")
            .DisplayName.Should().StartWith("15-inch ");

    [Fact]
    public void ParseDevices_keeps_an_unauthorized_device_but_marks_it_unusable()
    {
        // A phone that has not accepted this computer's debugging key yet.
        var devices = AndroidDebugBridge.ParseDevices("""
            List of devices attached
            RFCRB0MPHYH            unauthorized usb:1-12 transport_id:1
            """);

        devices.Should().HaveCount(1);
        devices[0].IsReady.Should().BeFalse();
        devices[0].State.Should().Be("unauthorized");
    }

    [Theory]
    [InlineData("List of devices attached\n")]
    [InlineData("List of devices attached\n\n")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseDevices_is_empty_when_nothing_is_attached(string output)
        => AndroidDebugBridge.ParseDevices(output).Should().BeEmpty();

    [Fact]
    public void ParseDevices_ignores_the_lines_adb_writes_about_its_own_daemon()
    {
        var devices = AndroidDebugBridge.ParseDevices("""
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:2
            """);

        devices.Should().ContainSingle();
        devices[0].Serial.Should().Be("emulator-5554");
    }

    [Fact]
    public void ParseDevices_handles_windows_line_endings()
        => AndroidDebugBridge
            .ParseDevices("List of devices attached\r\nRFCRB0MPHYH\tdevice model:SM_G781U1\r\n")
            .Should().ContainSingle().Which.Serial.Should().Be("RFCRB0MPHYH");

    [Fact]
    public void SameDevices_sees_no_change_in_an_identical_poll()
    {
        var first = AndroidDebugBridge.ParseDevices(TwoDevices);
        var second = AndroidDebugBridge.ParseDevices(TwoDevices);

        AndroidDeviceMonitor.SameDevices(first, second).Should().BeTrue();
    }

    [Fact]
    public void SameDevices_notices_a_device_arriving_or_leaving()
    {
        var both = AndroidDebugBridge.ParseDevices(TwoDevices);
        var one = AndroidDebugBridge.ParseDevices("""
            List of devices attached
            RFCRB0MPHYH            device usb:1-12 product:r8quex model:SM_G781U1 device:r8q transport_id:1
            """);

        AndroidDeviceMonitor.SameDevices(both, one).Should().BeFalse();
        AndroidDeviceMonitor.SameDevices(one, Array.Empty<AndroidDevice>()).Should().BeFalse();
    }

    [Fact]
    public void SameDevices_notices_a_phone_becoming_authorized()
    {
        // The change the user is waiting for after tapping "Allow" on the phone.
        var before = new[] { new AndroidDevice("RFCRB0MPHYH", "unauthorized", "SM_G781U1", "r8quex") };
        var after = new[] { new AndroidDevice("RFCRB0MPHYH", "device", "SM_G781U1", "r8quex") };

        AndroidDeviceMonitor.SameDevices(before, after).Should().BeFalse();
    }

    [Fact]
    public async Task The_monitor_reports_the_first_poll_and_then_only_real_changes()
    {
        var polls = 0;
        var devices = (IReadOnlyList<AndroidDevice>) Array.Empty<AndroidDevice>();
        var announced = new List<int>();
        using var monitor = new AndroidDeviceMonitor(TimeSpan.FromMilliseconds(15), _ =>
        {
            Interlocked.Increment(ref polls);
            return Task.FromResult(devices);
        });

        var changes = 0;
        monitor.DevicesChanged += reported =>
        {
            Interlocked.Increment(ref changes);
            lock (announced)
                announced.Add(reported.Count);
        };

        monitor.Start();
        monitor.IsRunning.Should().BeTrue();

        // Nothing attached: the very first poll is itself no change from the
        // empty starting point, so nothing is announced.
        await WaitUntil(() => polls >= 3);
        changes.Should().Be(0);

        // A device arrives, and keeps being reported by every later poll.
        devices = AndroidDebugBridge.ParseDevices(TwoDevices);
        await WaitUntil(() => changes >= 1);
        var pollsAfterArrival = polls;
        await WaitUntil(() => polls >= pollsAfterArrival + 3);

        changes.Should().Be(1);
        lock (announced)
            announced.Should().ContainSingle().Which.Should().Be(2);

        monitor.Devices.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_monitor_keeps_polling_after_a_failure()
    {
        var polls = 0;
        using var monitor = new AndroidDeviceMonitor(TimeSpan.FromMilliseconds(10), _ =>
        {
            // A transient adb failure must not end the loop — the device may
            // still be on its way.
            if (Interlocked.Increment(ref polls) <= 2)
                throw new InvalidOperationException("adb fell over");
            return Task.FromResult((IReadOnlyList<AndroidDevice>) Array.Empty<AndroidDevice>());
        });

        monitor.Start();
        await WaitUntil(() => polls >= 5);

        monitor.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Stopping_the_monitor_ends_the_polling_and_forgets_the_devices()
    {
        var polls = 0;
        using var monitor = new AndroidDeviceMonitor(TimeSpan.FromMilliseconds(10), _ =>
        {
            Interlocked.Increment(ref polls);
            return Task.FromResult(AndroidDebugBridge.ParseDevices(TwoDevices));
        });

        monitor.Start();
        await WaitUntil(() => monitor.Devices.Count == 2);
        monitor.Stop();

        monitor.IsRunning.Should().BeFalse();
        monitor.Devices.Should().BeEmpty();

        var pollsAtStop = polls;
        await Task.Delay(60, TestContext.Current.CancellationToken);
        polls.Should().BeLessThanOrEqualTo(pollsAtStop + 1); // at most one already in flight
    }

    [Fact]
    public void Starting_twice_is_harmless()
    {
        using var monitor = new AndroidDeviceMonitor(TimeSpan.FromMilliseconds(50),
            _ => Task.FromResult((IReadOnlyList<AndroidDevice>) Array.Empty<AndroidDevice>()));

        monitor.Start();
        monitor.Start();

        monitor.IsRunning.Should().BeTrue();
        monitor.Stop();
        monitor.Stop(); // and stopping twice, likewise
        monitor.IsRunning.Should().BeFalse();
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not come true within five seconds.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
