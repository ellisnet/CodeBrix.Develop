using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodeBrix.Develop.Core.Testing;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class XunitRunnerParsingTests
{
    [Fact]
    public void StripAnsi_removes_color_escape_sequences()
        => XunitRunnerParsing.StripAnsi("\x1B[37m{\"a\":1}\x1B[0m").Should().Be("{\"a\":1}");

    [Fact]
    public void StripAnsi_leaves_plain_lines_alone()
        => XunitRunnerParsing.StripAnsi("plain output").Should().Be("plain output");

    [Fact]
    public void MethodFullNameFromDisplayName_strips_theory_arguments()
        => XunitRunnerParsing.MethodFullNameFromDisplayName("Ns.Cls.theory_test(value: 3)").Should().Be("Ns.Cls.theory_test");

    [Fact]
    public void MethodFullNameFromDisplayName_passes_plain_names_through()
        => XunitRunnerParsing.MethodFullNameFromDisplayName("Ns.Cls.passing_test").Should().Be("Ns.Cls.passing_test");

    [Fact]
    public void TryParseNativeEvent_reads_the_event_type()
    {
        //Act
        var type = XunitRunnerParsing.TryParseNativeEvent(
            "\x1B[37m{\"$type\":\"test-passed\",\"ExecutionTime\":0.5}\x1B[0m", out var document);

        //Assert
        type.Should().Be("test-passed");
        using (document)
            document.RootElement.GetProperty("ExecutionTime").GetDouble().Should().Be(0.5);
    }

    [Fact]
    public void TryParseNativeEvent_rejects_plain_output_lines()
    {
        //Act + Assert
        XunitRunnerParsing.TryParseNativeEvent("Hello from a test", out var document).Should().BeNull();
        document.Should().BeNull();
    }

    [Fact]
    public void TryParseNativeEvent_rejects_json_without_a_type()
    {
        //Act + Assert
        XunitRunnerParsing.TryParseNativeEvent("{\"key\": \"value\"}", out var document).Should().BeNull();
        document.Should().BeNull();
    }

    [Fact]
    public void TryParseMtpResultLine_reads_passed_lines()
    {
        //Act
        var row = XunitRunnerParsing.TryParseMtpResultLine("passed SpikeTests.BasicTests.passing_test (8ms)");

        //Assert
        row.Should().NotBeNull();
        row.Status.Should().Be(TestStatus.Passed);
        row.MethodFullName.Should().Be("SpikeTests.BasicTests.passing_test");
    }

    [Fact]
    public void TryParseMtpResultLine_reads_failed_theory_rows()
    {
        //Act
        var row = XunitRunnerParsing.TryParseMtpResultLine("failed SpikeTests.BasicTests.theory_test(value: 3) (7ms)");

        //Assert
        row.Should().NotBeNull();
        row.Status.Should().Be(TestStatus.Failed);
        row.DisplayName.Should().Be("SpikeTests.BasicTests.theory_test(value: 3)");
        row.MethodFullName.Should().Be("SpikeTests.BasicTests.theory_test");
    }

    [Fact]
    public void TryParseMtpResultLine_reads_skipped_lines()
        => XunitRunnerParsing.TryParseMtpResultLine("skipped SpikeTests.BasicTests.skipped_test (0ms)")
            .Status.Should().Be(TestStatus.Skipped);

    [Fact]
    public void TryParseMtpResultLine_rejects_detail_and_summary_lines()
    {
        //Assert
        XunitRunnerParsing.TryParseMtpResultLine("  value was 3").Should().BeNull();
        XunitRunnerParsing.TryParseMtpResultLine("Test run summary: Failed!").Should().BeNull();
        XunitRunnerParsing.TryParseMtpResultLine("").Should().BeNull();
    }

    [Fact]
    public void TryParseMtpArtifactLine_reads_the_xunit_report_path()
        => XunitRunnerParsing.TryParseMtpArtifactLine("    - /tmp/results/user_host_2026.xunit")
            .Should().Be("/tmp/results/user_host_2026.xunit");

    [Fact]
    public void TryParseMtpArtifactLine_ignores_other_lines()
    {
        //Assert
        XunitRunnerParsing.TryParseMtpArtifactLine("  In process file artifacts produced:").Should().BeNull();
        XunitRunnerParsing.TryParseMtpArtifactLine("    - /tmp/results/report.trx").Should().BeNull();
    }

    [Fact]
    public void ParseXunitXmlReport_reads_pass_fail_and_skip_rows()
    {
        //Arrange
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <assemblies schema-version="3">
              <assembly name="/x/Spike.dll" total="4" passed="2" failed="1" skipped="1">
                <collection name="Test collection for Ns.Cls" total="4">
                  <test name="Ns.Cls.good" type="Ns.Cls" method="good" result="Pass" time="0.008" />
                  <test name="Ns.Cls.theory(value: 1)" type="Ns.Cls" method="theory" result="Pass" time="0.001" />
                  <test name="Ns.Cls.bad" type="Ns.Cls" method="bad" result="Fail" time="0.002">
                    <failure exception-type="Xunit.Sdk.EqualException">
                      <message><![CDATA[Assert.Equal() Failure]]></message>
                      <stack-trace><![CDATA[   at Ns.Cls.bad() in /x/File.cs:line 11]]></stack-trace>
                    </failure>
                  </test>
                  <test name="Ns.Cls.ignored" type="Ns.Cls" method="ignored" result="Skip">
                    <reason><![CDATA[Deliberately skipped]]></reason>
                  </test>
                </collection>
              </assembly>
            </assemblies>
            """;

        //Act
        var rows = XunitRunnerParsing.ParseXunitXmlReport(xml);

        //Assert
        rows.Count.Should().Be(4);
        var good = rows.Single(r => r.DisplayName == "Ns.Cls.good");
        good.Status.Should().Be(TestStatus.Passed);
        good.MethodFullName.Should().Be("Ns.Cls.good");
        good.DurationSeconds.Should().Be(0.008);
        var theory = rows.Single(r => r.DisplayName == "Ns.Cls.theory(value: 1)");
        theory.MethodFullName.Should().Be("Ns.Cls.theory");
        var bad = rows.Single(r => r.DisplayName == "Ns.Cls.bad");
        bad.Status.Should().Be(TestStatus.Failed);
        bad.Message.Should().Be("Assert.Equal() Failure");
        bad.StackTrace.Should().Contain("/x/File.cs:line 11");
        var ignored = rows.Single(r => r.DisplayName == "Ns.Cls.ignored");
        ignored.Status.Should().Be(TestStatus.Skipped);
        ignored.Message.Should().Be("Deliberately skipped");
    }

    [Fact]
    public void ReadNativeResultEvent_reads_a_failure_with_joined_messages()
    {
        //Arrange
        using var document = JsonDocument.Parse("""
            {"$type":"test-failed","TestUniqueID":"abc","ExecutionTime":0.25,
             "ExceptionTypes":["System.InvalidOperationException"],
             "Messages":["boom"],"StackTraces":["   at Ns.Cls.m() in /x/File.cs:line 5"]}
            """);
        var names = new Dictionary<string, string> { ["abc"] = "Ns.Cls.m" };

        //Act
        var row = XunitRunnerParsing.ReadNativeResultEvent("test-failed", document.RootElement, names);

        //Assert
        row.Status.Should().Be(TestStatus.Failed);
        row.DisplayName.Should().Be("Ns.Cls.m");
        row.MethodFullName.Should().Be("Ns.Cls.m");
        row.Message.Should().Be("boom");
        row.StackTrace.Should().Contain("/x/File.cs:line 5");
        row.DurationSeconds.Should().Be(0.25);
    }

    [Fact]
    public void ReadNativeResultEvent_resolves_the_display_name_from_the_starting_event()
    {
        //Arrange
        using var document = JsonDocument.Parse("""{"$type":"test-passed","TestUniqueID":"xyz","ExecutionTime":0.1}""");
        var names = new Dictionary<string, string> { ["xyz"] = "Ns.Cls.theory(value: 2)" };

        //Act
        var row = XunitRunnerParsing.ReadNativeResultEvent("test-passed", document.RootElement, names);

        //Assert
        row.Status.Should().Be(TestStatus.Passed);
        row.MethodFullName.Should().Be("Ns.Cls.theory");
    }

    [Fact]
    public void ReadNativeResultEvent_reads_a_skip_reason()
    {
        //Arrange
        using var document = JsonDocument.Parse(
            """{"$type":"test-skipped","TestUniqueID":"s1","Reason":"not on this platform"}""");
        var names = new Dictionary<string, string> { ["s1"] = "Ns.Cls.s" };

        //Act
        var row = XunitRunnerParsing.ReadNativeResultEvent("test-skipped", document.RootElement, names);

        //Assert
        row.Status.Should().Be(TestStatus.Skipped);
        row.Message.Should().Be("not on this platform");
    }
}
