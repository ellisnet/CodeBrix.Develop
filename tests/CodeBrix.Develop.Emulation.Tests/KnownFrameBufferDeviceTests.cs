using CodeBrix.Develop.Emulation.FrameBuffer;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

public class KnownFrameBufferDeviceTests
{
    [Fact]
    public void The_winbook_tw700_is_a_portrait_native_800x1280()
    {
        //Act
        var device = KnownFrameBufferDevice.TryFind("WinBook", "TW700");

        //Assert
        device.Should().NotBeNull();
        device.Screen.Should().Be(FrameBufferResolution.SevenInch800x1280);
        device.NativeOrientation.Should().Be(FrameBufferOrientation.Portrait);
    }

    [Fact]
    public void The_winbook_tw802_is_a_portrait_native_eight_inch_800x1280()
    {
        //Act
        var device = KnownFrameBufferDevice.TryFind("WinBook", "TW802");

        //Assert
        device.Should().NotBeNull();
        device.Screen.Should().Be(FrameBufferResolution.EightInch800x1280);
        device.NativeOrientation.Should().Be(FrameBufferOrientation.Portrait);
    }

    [Fact]
    public void Lookup_ignores_case_and_the_trailing_newlines_of_dmi_files()
        => KnownFrameBufferDevice.TryFind("winbook\n", " tw700 ").Should().NotBeNull();

    [Theory]
    [InlineData("Acme", "TW700")]
    [InlineData("WinBook", "TW800")]
    [InlineData("unknown", "unknown")]
    [InlineData(null, "TW700")]
    [InlineData("WinBook", "")]
    public void An_unknown_device_is_not_found(string vendor, string model)
        => KnownFrameBufferDevice.TryFind(vendor, model).Should().BeNull();

    [Fact]
    public void Every_cataloged_screen_is_a_real_resolution()
    {
        foreach (var device in KnownFrameBufferDevice.All)
        {
            //Act — Get throws on a resolution the info list does not carry
            var info = FrameBufferResolutionInfo.Get(device.Screen);

            //Assert
            info.Resolution.Should().Be(device.Screen);
        }
    }
}
