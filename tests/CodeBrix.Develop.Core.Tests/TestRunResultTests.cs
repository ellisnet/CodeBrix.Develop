using CodeBrix.Develop.Core.Testing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class TestRunResultTests
{
    [Fact]
    public void TryGetFailureLocation_parses_dotnet_stack_format()
    {
        //Arrange
        var result = new TestRunResult
        {
            StackTrace = "   at SpikeTests.SecondFixture.throwing_test() in /home/user/proj/BasicTests.cs:line 29\n"
                + "   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
        };

        //Act
        var found = result.TryGetFailureLocation(out var file, out var line);

        //Assert
        found.Should().BeTrue();
        ((string) file).Should().Be("/home/user/proj/BasicTests.cs");
        line.Should().Be(29);
    }

    [Fact]
    public void TryGetFailureLocation_parses_mtp_stack_format_without_line_keyword()
    {
        //Arrange
        var result = new TestRunResult
        {
            StackTrace = "    at SpikeTests.BasicTests.theory_test(Int32 value) in /home/user/proj/BasicTests.cs:20",
        };

        //Act
        var found = result.TryGetFailureLocation(out var file, out var line);

        //Assert
        found.Should().BeTrue();
        ((string) file).Should().Be("/home/user/proj/BasicTests.cs");
        line.Should().Be(20);
    }

    [Fact]
    public void TryGetFailureLocation_is_false_without_a_source_location()
    {
        //Arrange
        var result = new TestRunResult
        {
            StackTrace = "   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
        };

        //Act + Assert
        result.TryGetFailureLocation(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetFailureLocation_is_false_for_an_empty_stack()
        => new TestRunResult().TryGetFailureLocation(out _, out _).Should().BeFalse();
}
