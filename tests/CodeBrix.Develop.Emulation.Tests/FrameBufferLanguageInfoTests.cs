using System.Linq;
using CodeBrix.Develop.Emulation.FrameBuffer;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Emulation.Tests;

public class FrameBufferLanguageInfoTests
{
    [Fact]
    public void The_system_default_leads_the_list_so_it_is_the_drop_down_default()
    {
        //Assert
        FrameBufferLanguageInfo.All[0].Code.Should().Be("system-default");
        FrameBufferLanguageInfo.All[0].DisplayName.Should().Be("Current system default");
        FrameBufferLanguageInfo.All[0].IsSystemDefault.Should().BeTrue();
        FrameBufferLanguageInfo.SystemDefault.Should().BeSameAs(FrameBufferLanguageInfo.All[0]);
    }

    [Fact]
    public void The_system_default_is_followed_by_the_thirty_eight_keyboard_layouts()
    {
        //Arrange — the software-keyboard layout ids of the Linux Frame Buffer
        //head, in the order the layouts are grouped.
        var expected = new[]
        {
            "en", "en-GB", "de", "de-CH", "fr", "fr-BE", "fr-CH", "nl",
            "es", "pt", "it", "mt", "sq", "tr", "el",
            "da", "no", "sv", "fi", "is", "lt", "lv", "et",
            "pl", "cs", "sk", "hu", "ro", "hr", "sr-Latn",
            "ru", "uk", "be", "bg", "sr", "mk",
            "ka", "hy",
        };

        //Act
        var codes = FrameBufferLanguageInfo.All.Skip(1).Select(info => info.Code).ToArray();

        //Assert
        codes.Should().Equal(expected);
        FrameBufferLanguageInfo.All.Count.Should().Be(39);
    }

    [Theory]
    [InlineData("en", "English (US)")]
    [InlineData("en-GB", "English (UK)")]
    [InlineData("de", "German (Deutsch)")]
    [InlineData("de-CH", "German (Swiss - Deutsch - Schweiz)")]
    [InlineData("fr", "French (Français)")]
    [InlineData("fr-BE", "French (Belgian - Français - Belgique)")]
    [InlineData("fr-CH", "French (Swiss - Français - Suisse)")]
    [InlineData("el", "Greek (Ελληνικά)")]
    [InlineData("sr-Latn", "Serbian (Latin - Srpski - latinica)")]
    [InlineData("sr", "Serbian (Cyrillic - Српски - ћирилица)")]
    [InlineData("ka", "Georgian (ქართული)")]
    [InlineData("hy", "Armenian (Հայերեն)")]
    public void Languages_are_named_english_first_with_the_native_name_in_parentheses(
        string code, string expected)
    {
        //Act
        var info = FrameBufferLanguageInfo.Get(code);

        //Assert
        info.DisplayName.Should().Be(expected);
        info.IsSystemDefault.Should().BeFalse();
    }

    [Fact]
    public void Labels_match_the_list_position_for_position()
    {
        //Assert — the Options page fills the drop-down from Labels and then
        //selects by list position, so the two must line up exactly.
        FrameBufferLanguageInfo.Labels.Count.Should().Be(FrameBufferLanguageInfo.All.Count);
        for (var index = 0; index < FrameBufferLanguageInfo.All.Count; index++)
            FrameBufferLanguageInfo.Labels[index].Should().Be(FrameBufferLanguageInfo.All[index].DisplayName);
    }

    [Fact]
    public void Every_code_round_trips_through_its_list_position()
    {
        foreach (var language in FrameBufferLanguageInfo.All)
        {
            //Act
            var index = FrameBufferLanguageInfo.IndexOf(language.Code);

            //Assert
            FrameBufferLanguageInfo.FromIndex(index).Code.Should().Be(language.Code);
            FrameBufferLanguageInfo.Get(language.Code).Should().BeSameAs(language);
        }
    }

    [Fact]
    public void Codes_and_display_names_are_distinct()
    {
        //Assert
        FrameBufferLanguageInfo.All.Select(info => info.Code).Distinct().Count()
            .Should().Be(FrameBufferLanguageInfo.All.Count);
        FrameBufferLanguageInfo.All.Select(info => info.DisplayName).Distinct().Count()
            .Should().Be(FrameBufferLanguageInfo.All.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("EN")]
    [InlineData(null)]
    public void An_unrecognized_stored_code_falls_back_to_the_system_default(string code)
    {
        //Act
        var info = FrameBufferLanguageInfo.Get(code);

        //Assert — a stored code is only ever text, so it must not throw.
        info.Should().BeSameAs(FrameBufferLanguageInfo.SystemDefault);
        FrameBufferLanguageInfo.IndexOf(code).Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(39)]
    public void An_out_of_range_position_falls_back_to_the_system_default(int index)
    {
        //Act
        var info = FrameBufferLanguageInfo.FromIndex(index);

        //Assert
        info.Should().BeSameAs(FrameBufferLanguageInfo.SystemDefault);
    }
}
