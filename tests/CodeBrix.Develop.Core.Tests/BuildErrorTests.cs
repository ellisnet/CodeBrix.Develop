using CodeBrix.Develop.Core.Projects;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class BuildErrorTests
{
    [Fact]
    public void FromMSBuildErrorFormat_parses_compiler_error_with_location()
    {
        //Act
        var error = BuildError.FromMSBuildErrorFormat(
            "/home/user/project/Program.cs(17,20): error CS0168: The variable 'x' is declared but never used [/home/user/project/App.csproj]");

        //Assert
        error.Should().NotBeNull();
        error.FileName.Should().Be("/home/user/project/Program.cs");
        error.Line.Should().Be(17);
        error.Column.Should().Be(20);
        error.IsWarning.Should().BeFalse();
        error.ErrorNumber.Should().Be("CS0168");
    }

    [Fact]
    public void FromMSBuildErrorFormat_parses_warning_category()
    {
        //Act
        var error = BuildError.FromMSBuildErrorFormat(
            "Program.cs(5,10): warning CS0219: The variable 'y' is assigned but its value is never used");

        //Assert
        error.Should().NotBeNull();
        error.IsWarning.Should().BeTrue();
        error.ErrorNumber.Should().Be("CS0219");
    }

    [Fact]
    public void FromMSBuildErrorFormat_parses_tool_origin_without_location()
    {
        //Act
        var error = BuildError.FromMSBuildErrorFormat("MSBUILD : error MSB1009: Project file does not exist.");

        //Assert
        error.Should().NotBeNull();
        error.FileName.Should().Be("MSBUILD");
        error.Line.Should().Be(0);
        error.ErrorNumber.Should().Be("MSB1009");
    }

    [Fact]
    public void FromMSBuildErrorFormat_returns_null_for_ordinary_output()
        => BuildError.FromMSBuildErrorFormat("  Determining projects to restore...").Should().BeNull();

    [Fact]
    public void FromMSBuildErrorFormat_returns_null_for_empty_line()
        => BuildError.FromMSBuildErrorFormat("").Should().BeNull();

    [Fact]
    public void ToString_formats_error_with_location()
    {
        //Arrange
        var error = new BuildError("Program.cs", 17, 20, "CS0168", "The variable 'x' is declared but never used");

        //Act & Assert
        error.ToString().Should().Be("Program.cs(17,20) : error CS0168: The variable 'x' is declared but never used");
    }
}

public class BuildResultTests
{
    [Fact]
    public void Append_counts_errors_and_warnings_separately()
    {
        //Arrange
        var result = new BuildResult();

        //Act
        result.Append(new BuildError { IsWarning = false });
        result.Append(new BuildError { IsWarning = true });
        result.Append(new BuildError { IsWarning = false });

        //Assert
        result.ErrorCount.Should().Be(2);
        result.WarningCount.Should().Be(1);
        result.Errors.Count.Should().Be(3);
    }

    [Fact]
    public void ToString_pluralizes_counts()
    {
        //Arrange
        var result = new BuildResult();
        result.Append(new BuildError { IsWarning = true });

        //Act & Assert
        result.ToString().Should().Be("Build: 0 errors, 1 warning");
    }
}
