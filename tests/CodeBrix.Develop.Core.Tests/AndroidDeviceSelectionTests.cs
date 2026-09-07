using System;
using CodeBrix.Develop.Core.Android;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class AndroidDeviceSelectionTests
{
    static AndroidDevice Phone(string state = "device")
        => new AndroidDevice("RFCRB0MPHYH", state, "SM_G781U1", "r8quex");

    static AndroidDevice Emulator(string state = "device")
        => new AndroidDevice("emulator-5554", state, "sdk_gphone64_x86_64", "sdk_gphone64_x86_64");

    [Fact]
    public void With_nothing_attached_nothing_is_selected()
    {
        AndroidDeviceSelection.PreferredIndex(Array.Empty<AndroidDevice>(), "").Should().Be(-1);
        AndroidDeviceSelection.PreferredIndex(null, "RFCRB0MPHYH").Should().Be(-1);
    }

    [Fact]
    public void The_first_device_seen_is_selected_when_the_user_never_chose_one()
        => AndroidDeviceSelection.PreferredIndex(new[] { Phone(), Emulator() }, "").Should().Be(0);

    [Fact]
    public void The_remembered_device_wins_over_the_first_one_seen()
        // The whole point of remembering: the emulator is second in adb's
        // list, and still the one that gets used.
        => AndroidDeviceSelection.PreferredIndex(new[] { Phone(), Emulator() }, "emulator-5554")
            .Should().Be(1);

    [Fact]
    public void The_remembered_device_is_skipped_while_it_is_not_attached()
        // Unplugged for the afternoon: fall back, but do not forget.
        => AndroidDeviceSelection.PreferredIndex(new[] { Emulator() }, "RFCRB0MPHYH").Should().Be(0);

    [Fact]
    public void A_ready_device_is_preferred_over_one_that_is_merely_attached()
        // An unauthorized phone cannot be deployed to, so the emulator wins
        // even though the phone is listed first.
        => AndroidDeviceSelection.PreferredIndex(new[] { Phone("unauthorized"), Emulator() }, "")
            .Should().Be(1);

    [Fact]
    public void The_remembered_device_wins_even_when_it_is_not_ready()
        // Deliberate beats convenient: selecting it is what surfaces the
        // "(unauthorized)" state to the user, who can then act on it.
        => AndroidDeviceSelection.PreferredIndex(new[] { Emulator(), Phone("unauthorized") }, "RFCRB0MPHYH")
            .Should().Be(1);

    [Fact]
    public void The_first_device_is_selected_when_none_of_them_are_ready()
        // Nothing is launchable, but something must be shown.
        => AndroidDeviceSelection.PreferredIndex(new[] { Phone("offline"), Emulator("unauthorized") }, "")
            .Should().Be(0);

    [Fact]
    public void Serial_matching_is_exact()
        // Neither a prefix nor a different case is the remembered device.
        => AndroidDeviceSelection.PreferredIndex(new[] { Emulator(), Phone() }, "rfcrb0mphyh")
            .Should().Be(0);
}
