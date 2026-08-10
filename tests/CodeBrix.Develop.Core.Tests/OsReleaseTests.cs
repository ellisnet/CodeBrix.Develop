using CodeBrix.Develop.Core.Remote;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class OsReleaseTests
{
    const string DebianTrixie =
        "PRETTY_NAME=\"Debian GNU/Linux 13 (trixie)\"\n" +
        "NAME=\"Debian GNU/Linux\"\n" +
        "VERSION_ID=\"13\"\n" +
        "VERSION=\"13 (trixie)\"\n" +
        "VERSION_CODENAME=trixie\n" +
        "ID=debian\n" +
        "HOME_URL=\"https://www.debian.org/\"\n";

    const string Ubuntu2404 =
        "PRETTY_NAME=\"Ubuntu 24.04.2 LTS\"\n" +
        "NAME=\"Ubuntu\"\n" +
        "VERSION_ID=\"24.04\"\n" +
        "VERSION=\"24.04.2 LTS (Noble Numbat)\"\n" +
        "VERSION_CODENAME=noble\n" +
        "ID=ubuntu\n" +
        "ID_LIKE=debian\n";

    [Fact]
    public void Parse_reads_debian_trixie()
    {
        //Act
        var os = OsRelease.Parse(DebianTrixie);

        //Assert
        os.Id.Should().Be("debian");
        os.IdLike.Should().Be("unknown"); // Debian carries no ID_LIKE
        os.VersionId.Should().Be("13");
        os.VersionCodename.Should().Be("trixie");
    }

    [Fact]
    public void Parse_reads_the_ubuntu_family()
    {
        //Act
        var os = OsRelease.Parse(Ubuntu2404);

        //Assert
        os.Id.Should().Be("ubuntu");
        os.IdLike.Should().Be("debian");
        os.VersionId.Should().Be("24.04");
        os.VersionCodename.Should().Be("noble");
    }

    [Fact]
    public void Parse_strips_single_and_double_quotes_but_keeps_bare_values()
    {
        //Act
        var os = OsRelease.Parse("ID='raspbian'\nVERSION_ID=12\nVERSION_CODENAME=\"bookworm\"\n");

        //Assert
        os.Id.Should().Be("raspbian");
        os.VersionId.Should().Be("12");
        os.VersionCodename.Should().Be("bookworm");
    }

    [Fact]
    public void Parse_ignores_blank_lines_and_comments()
    {
        //Act
        var os = OsRelease.Parse("\n# a comment\nID=debian\n\n");

        //Assert
        os.Id.Should().Be("debian");
        os.VersionId.Should().Be("unknown");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no equals signs here")]
    public void Parse_yields_all_unknown_for_unusable_input(string contents)
    {
        //Act
        var os = OsRelease.Parse(contents);

        //Assert
        os.Id.Should().Be("unknown");
        os.IdLike.Should().Be("unknown");
        os.VersionId.Should().Be("unknown");
        os.VersionCodename.Should().Be("unknown");
    }
}
