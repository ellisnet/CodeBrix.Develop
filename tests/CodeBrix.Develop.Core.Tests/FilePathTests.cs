using CodeBrix.Develop.Core;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class FilePathTests
{
    [Fact]
    public void Combine_joins_path_segments()
        => new FilePath("/home/user").Combine("src", "Program.cs").ToString().Should().Be("/home/user/src/Program.cs");

    [Fact]
    public void FileName_returns_name_with_extension()
        => new FilePath("/home/user/src/Program.cs").FileName.Should().Be("Program.cs");

    [Fact]
    public void FileNameWithoutExtension_strips_extension()
        => new FilePath("/home/user/src/Program.cs").FileNameWithoutExtension.Should().Be("Program");

    [Fact]
    public void Extension_includes_leading_dot()
        => new FilePath("/home/user/App.slnx").Extension.Should().Be(".slnx");

    [Fact]
    public void HasExtension_matches_full_extension()
    {
        //Arrange
        var path = new FilePath("/home/user/App.slnx");

        //Assert
        path.HasExtension(".slnx").Should().BeTrue();
        path.HasExtension(".sln").Should().BeFalse();
    }

    [Fact]
    public void ParentDirectory_returns_containing_directory()
        => new FilePath("/home/user/src/Program.cs").ParentDirectory.ToString().Should().Be("/home/user/src");

    [Fact]
    public void IsChildPathOf_detects_containment()
    {
        //Arrange
        var basePath = new FilePath("/home/user/project");

        //Assert
        new FilePath("/home/user/project/src/A.cs").IsChildPathOf(basePath).Should().BeTrue();
        new FilePath("/home/user/projectile/A.cs").IsChildPathOf(basePath).Should().BeFalse();
        new FilePath("/home/user/project").IsChildPathOf(basePath).Should().BeFalse();
    }

    [Fact]
    public void ToRelative_expresses_path_against_base()
        => new FilePath("/home/user/project/src/A.cs").ToRelative("/home/user/project").ToString().Should().Be("src/A.cs");

    [Fact]
    public void ToAbsolute_resolves_relative_path_against_base()
        => new FilePath("src/A.cs").ToAbsolute("/home/user/project").ToString().Should().Be("/home/user/project/src/A.cs");

    [Fact]
    public void File_uri_is_converted_to_local_path()
        => new FilePath("file:///home/user/A.cs").ToString().Should().Be("/home/user/A.cs");

    [Fact]
    public void Equality_uses_path_comparison()
    {
        //Arrange
        var first = new FilePath("/home/user/A.cs");
        var second = new FilePath("/home/user/A.cs");

        //Assert
        (first == second).Should().BeTrue();
        first.Equals(second).Should().BeTrue();
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void Null_and_empty_report_their_state()
    {
        //Assert
        FilePath.Null.IsNull.Should().BeTrue();
        FilePath.Null.IsNullOrEmpty.Should().BeTrue();
        FilePath.Empty.IsEmpty.Should().BeTrue();
        FilePath.Empty.IsNull.Should().BeFalse();
    }

    [Fact]
    public void GetCommonRootPath_finds_deepest_shared_directory()
    {
        //Arrange
        var paths = new FilePath[]
        {
            "/home/user/project/src/A.cs",
            "/home/user/project/tests/B.cs",
            "/home/user/project/README.md",
        };

        //Act
        var root = FilePath.GetCommonRootPath(paths);

        //Assert
        root.ToString().Should().Be("/home/user/project");
    }
}
