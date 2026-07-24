using System;
using System.Linq;
using CodeBrix.Develop.Emulation.FrameBuffer;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

public class FrameBufferResolutionInfoTests
{
    [Theory]
    [InlineData(FrameBufferResolution.FiveInch720x1280, "720 x 1280 pixels (5-inch)")]
    [InlineData(FrameBufferResolution.SevenInch720x1280, "720 x 1280 pixels (7-inch)")]
    [InlineData(FrameBufferResolution.TenInch1200x1920, "1200 x 1920 pixels (10-inch)")]
    [InlineData(FrameBufferResolution.Hd1080x1920, "1080 x 1920 pixels (HD)")]
    public void Portrait_labels_lead_with_the_short_side(FrameBufferResolution resolution, string expected)
    {
        //Act
        var label = FrameBufferResolutionInfo.Get(resolution).GetLabel(FrameBufferOrientation.Portrait);

        //Assert
        label.Should().Be(expected);
    }

    [Theory]
    [InlineData(FrameBufferResolution.FiveInch720x1280, "1280 x 720 pixels (5-inch)")]
    [InlineData(FrameBufferResolution.SevenInch720x1280, "1280 x 720 pixels (7-inch)")]
    [InlineData(FrameBufferResolution.TenInch1200x1920, "1920 x 1200 pixels (10-inch)")]
    [InlineData(FrameBufferResolution.Hd1080x1920, "1920 x 1080 pixels (HD)")]
    public void Landscape_labels_lead_with_the_long_side(FrameBufferResolution resolution, string expected)
    {
        //Act
        var label = FrameBufferResolutionInfo.Get(resolution).GetLabel(FrameBufferOrientation.Landscape);

        //Assert
        label.Should().Be(expected);
    }

    [Fact]
    public void Switching_orientation_keeps_every_screen_at_the_same_position()
    {
        //Arrange
        var portrait = FrameBufferResolutionInfo.GetLabels(FrameBufferOrientation.Portrait);
        var landscape = FrameBufferResolutionInfo.GetLabels(FrameBufferOrientation.Landscape);

        //Assert — the Options page relies on this: relabel in place, keep the
        //selected index, and the user stays on the screen they had chosen.
        portrait.Count.Should().Be(landscape.Count);
        portrait.Count.Should().Be(FrameBufferResolutionInfo.All.Count);
        for (var index = 0; index < portrait.Count; index++)
        {
            var screen = FrameBufferResolutionInfo.All[index];
            portrait[index].Should().Be(screen.GetLabel(FrameBufferOrientation.Portrait));
            landscape[index].Should().Be(screen.GetLabel(FrameBufferOrientation.Landscape));
        }
    }

    [Fact]
    public void Five_inch_and_seven_inch_share_dimensions_but_stay_distinct_screens()
    {
        //Arrange
        var fiveInch = FrameBufferResolutionInfo.Get(FrameBufferResolution.FiveInch720x1280);
        var sevenInch = FrameBufferResolutionInfo.Get(FrameBufferResolution.SevenInch720x1280);

        //Assert — intentional: the size class is the identity, not the pixels.
        fiveInch.ShortSide.Should().Be(sevenInch.ShortSide);
        fiveInch.LongSide.Should().Be(sevenInch.LongSide);
        fiveInch.SizeClass.Should().Be("5-inch");
        sevenInch.SizeClass.Should().Be("7-inch");
        fiveInch.Resolution.Should().NotBe(sevenInch.Resolution);
    }

    [Theory]
    [InlineData(FrameBufferResolution.FiveInch720x1280, 360, 640)]
    [InlineData(FrameBufferResolution.SevenInch720x1280, 360, 640)]
    [InlineData(FrameBufferResolution.TenInch1200x1920, 400, 640)]
    [InlineData(FrameBufferResolution.Hd1080x1920, 360, 640)]
    public void Portrait_default_window_size_is_640_tall_and_proportional(
        FrameBufferResolution resolution, int expectedWidth, int expectedHeight)
    {
        //Act
        var size = FrameBufferResolutionInfo.Get(resolution)
            .GetDefaultWindowSize(FrameBufferOrientation.Portrait);

        //Assert
        size.Width.Should().Be(expectedWidth);
        size.Height.Should().Be(expectedHeight);
    }

    [Theory]
    [InlineData(FrameBufferResolution.FiveInch720x1280, 640, 360)]
    [InlineData(FrameBufferResolution.TenInch1200x1920, 640, 400)]
    public void Landscape_default_window_size_is_640_wide_and_proportional(
        FrameBufferResolution resolution, int expectedWidth, int expectedHeight)
    {
        //Act
        var size = FrameBufferResolutionInfo.Get(resolution)
            .GetDefaultWindowSize(FrameBufferOrientation.Landscape);

        //Assert
        size.Width.Should().Be(expectedWidth);
        size.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void Size_for_width_keeps_the_width_and_takes_the_height_from_the_screen()
    {
        //Act — a remembered 500-wide window re-fitted to a portrait 720 x 1280.
        var size = FrameBufferResolutionInfo.Get(FrameBufferResolution.SevenInch720x1280)
            .GetSizeForWidth(500, FrameBufferOrientation.Portrait);

        //Assert
        size.Width.Should().Be(500);
        size.Height.Should().Be(889); // 500 * 1280 / 720, rounded
    }

    [Fact]
    public void Snapping_prefers_the_candidate_that_moves_the_window_least()
    {
        //Arrange
        var screen = FrameBufferResolutionInfo.Get(FrameBufferResolution.FiveInch720x1280);

        //Act — 400 x 640 is much nearer the height-preserving 360 x 640 than
        //the width-preserving 400 x 711.
        var size = screen.SnapToAspectRatio(400, 640, FrameBufferOrientation.Portrait);

        //Assert
        size.Width.Should().Be(360);
        size.Height.Should().Be(640);
    }

    [Fact]
    public void Snapping_keeps_the_width_when_the_height_moved_further()
    {
        //Arrange
        var screen = FrameBufferResolutionInfo.Get(FrameBufferResolution.FiveInch720x1280);

        //Act
        var size = screen.SnapToAspectRatio(360, 700, FrameBufferOrientation.Portrait);

        //Assert — the height-preserving candidate wins here (34px of movement
        //against 60px), so the window keeps its new height.
        size.Width.Should().Be(394);
        size.Height.Should().Be(700);
    }

    [Fact]
    public void Snapping_an_already_proportional_size_changes_nothing()
    {
        //Arrange
        var screen = FrameBufferResolutionInfo.Get(FrameBufferResolution.TenInch1200x1920);

        //Act
        var size = screen.SnapToAspectRatio(400, 640, FrameBufferOrientation.Portrait);

        //Assert
        size.Width.Should().Be(400);
        size.Height.Should().Be(640);
    }

    [Theory]
    [InlineData(FrameBufferOrientation.Portrait, 120, 213)]
    [InlineData(FrameBufferOrientation.Landscape, 213, 120)]
    public void Snapping_never_shrinks_the_short_side_below_the_minimum(
        FrameBufferOrientation orientation, int expectedWidth, int expectedHeight)
    {
        //Arrange
        var screen = FrameBufferResolutionInfo.Get(FrameBufferResolution.FiveInch720x1280);

        //Act
        var size = screen.SnapToAspectRatio(20, 20, orientation);

        //Assert
        size.Width.Should().Be(expectedWidth);
        size.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void Every_resolution_round_trips_through_its_list_position()
    {
        foreach (var screen in FrameBufferResolutionInfo.All)
        {
            //Act
            var index = FrameBufferResolutionInfo.IndexOf(screen.Resolution);

            //Assert
            FrameBufferResolutionInfo.FromIndex(index).Resolution.Should().Be(screen.Resolution);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void An_out_of_range_position_falls_back_to_the_default_screen(int index)
    {
        //Act
        var screen = FrameBufferResolutionInfo.FromIndex(index);

        //Assert
        screen.Resolution.Should().Be(FrameBufferResolution.SevenInch720x1280);
    }

    [Fact]
    public void An_unknown_resolution_is_rejected()
    {
        //Act
        var act = () => FrameBufferResolutionInfo.Get((FrameBufferResolution) 99);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void All_lists_every_declared_resolution_once()
    {
        //Arrange
        var declared = Enum.GetValues<FrameBufferResolution>();

        //Act
        var listed = FrameBufferResolutionInfo.All.Select(info => info.Resolution).ToArray();

        //Assert
        listed.Should().BeEquivalentTo(declared);
        listed.Distinct().Count().Should().Be(listed.Length);
    }
}
