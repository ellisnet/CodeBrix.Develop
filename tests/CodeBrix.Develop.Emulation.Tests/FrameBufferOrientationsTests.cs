using System;
using CodeBrix.Develop.Emulation.FrameBuffer;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

public class FrameBufferOrientationsTests
{
    [Theory]
    [InlineData(FrameBufferOrientation.Portrait, "Portrait")]
    [InlineData(FrameBufferOrientation.Landscape, "Landscape")]
    public void Orientations_are_labeled_for_the_drop_down(FrameBufferOrientation orientation, string expected)
    {
        //Act
        var label = orientation.GetLabel();

        //Assert
        label.Should().Be(expected);
    }

    [Fact]
    public void Portrait_is_first_so_it_is_the_drop_down_default()
    {
        //Assert
        FrameBufferOrientations.All[0].Should().Be(FrameBufferOrientation.Portrait);
        FrameBufferOrientations.Labels[0].Should().Be("Portrait");
        FrameBufferOrientations.Labels.Count.Should().Be(FrameBufferOrientations.All.Count);
    }

    [Fact]
    public void Every_orientation_round_trips_through_its_list_position()
    {
        foreach (var orientation in FrameBufferOrientations.All)
        {
            //Act
            var index = FrameBufferOrientations.IndexOf(orientation);

            //Assert
            FrameBufferOrientations.FromIndex(index).Should().Be(orientation);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void An_out_of_range_position_falls_back_to_portrait(int index)
    {
        //Act
        var orientation = FrameBufferOrientations.FromIndex(index);

        //Assert
        orientation.Should().Be(FrameBufferOrientation.Portrait);
    }

    [Fact]
    public void An_unknown_orientation_is_rejected()
    {
        //Act
        var act = () => ((FrameBufferOrientation) 99).GetLabel();

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
