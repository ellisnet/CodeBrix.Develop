//
// XunitRunnerParsing.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (parses the two CLI dialects of xUnit.net v3 test executables: the
//      native in-process runner's JSON reporter, and the
//      Microsoft.Testing.Platform runner's console output + xUnit XML report)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeBrix.Develop.Core.Testing;

/// <summary>One test result row as reported by the runner (a theory data row reports individually).</summary>
class TestRowResult
{
    public string DisplayName = "";
    public string MethodFullName = "";
    public TestStatus Status = TestStatus.NotRun;
    public string Message = "";
    public string StackTrace = "";
    public double DurationSeconds;
}

/// <summary>
/// Stateless parsing helpers for the output of xUnit.net v3 test
/// executables, in both their native-runner and Microsoft.Testing.Platform
/// CLI dialects (see the runner-mode notes on <see cref="TestService"/>).
/// </summary>
static class XunitRunnerParsing
{
    static readonly Regex ansiRegex = new Regex(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);

    // "passed Ns.Cls.method(args) (8ms)" — the MTP runner's Detailed output.
    static readonly Regex mtpResultLineRegex = new Regex(
        @"^(?<outcome>passed|failed|skipped) (?<name>.*) \((?<duration>[^()]*)\)$",
        RegexOptions.Compiled);

    /// <summary>Removes ANSI color escape sequences from a runner output line.</summary>
    public static string StripAnsi(string line)
        => line.IndexOf('\x1B') < 0 ? line : ansiRegex.Replace(line, "");

    /// <summary>
    /// The runner-reported display name reduced to the method's fully
    /// qualified name: a theory row's "(…)" argument suffix is stripped.
    /// </summary>
    public static string MethodFullNameFromDisplayName(string displayName)
    {
        var parenthesis = displayName.IndexOf('(');
        return parenthesis < 0 ? displayName : displayName[..parenthesis];
    }

    /// <summary>
    /// Parses one line of the NATIVE runner's "-reporter json" stream.
    /// Returns the event type (e.g. "test-starting", "test-passed") with the
    /// parsed document, or null when the line is not a reporter event.
    /// The document must be disposed by the caller.
    /// </summary>
    public static string TryParseNativeEvent(string line, out JsonDocument document)
    {
        document = null;
        var text = StripAnsi(line).Trim();
        if (text.Length == 0 || text[0] != '{' || text[^1] != '}')
            return null;
        try
        {
            var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.TryGetProperty("$type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                document = parsed;
                return type.GetString();
            }
            parsed.Dispose();
        }
        catch (JsonException)
        {
            // Not an event line — plain test output that happens to look like JSON.
        }
        return null;
    }

    /// <summary>
    /// Parses one line of the MTP runner's "--output Detailed" stream into a
    /// live result row (message/stack detail lines follow separately and are
    /// reconciled from the XML report). Returns null for non-result lines.
    /// </summary>
    public static TestRowResult TryParseMtpResultLine(string line)
    {
        var match = mtpResultLineRegex.Match(line);
        if (!match.Success)
            return null;
        var displayName = match.Groups["name"].Value;
        return new TestRowResult
        {
            DisplayName = displayName,
            MethodFullName = MethodFullNameFromDisplayName(displayName),
            Status = match.Groups["outcome"].Value switch
            {
                "passed" => TestStatus.Passed,
                "failed" => TestStatus.Failed,
                _ => TestStatus.Skipped,
            },
        };
    }

    /// <summary>
    /// Recognizes the MTP runner's produced-artifact lines ("- /path/x.xunit")
    /// and returns the report path, or null.
    /// </summary>
    public static string TryParseMtpArtifactLine(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal) && text.EndsWith(".xunit", StringComparison.Ordinal))
            return text[2..];
        return null;
    }

    /// <summary>
    /// Parses an xUnit.net v2+ XML report (the MTP runner's --report-xunit
    /// artifact) into result rows.
    /// </summary>
    public static List<TestRowResult> ParseXunitXmlReport(string xml)
    {
        var rows = new List<TestRowResult>();
        var document = XDocument.Parse(xml);
        foreach (var test in document.Descendants("test"))
        {
            var type = test.Attribute("type")?.Value ?? "";
            var method = test.Attribute("method")?.Value ?? "";
            var row = new TestRowResult
            {
                DisplayName = test.Attribute("name")?.Value ?? $"{type}.{method}",
                MethodFullName = type.Length > 0 && method.Length > 0 ? $"{type}.{method}" : "",
                Status = test.Attribute("result")?.Value switch
                {
                    "Pass" => TestStatus.Passed,
                    "Fail" => TestStatus.Failed,
                    "Skip" => TestStatus.Skipped,
                    _ => TestStatus.NotRun,
                },
            };
            if (row.MethodFullName.Length == 0)
                row.MethodFullName = MethodFullNameFromDisplayName(row.DisplayName);
            if (double.TryParse(test.Attribute("time")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                row.DurationSeconds = time;
            if (test.Element("failure") is { } failure)
            {
                row.Message = failure.Element("message")?.Value ?? "";
                row.StackTrace = failure.Element("stack-trace")?.Value ?? "";
            }
            else if (row.Status == TestStatus.Skipped)
            {
                row.Message = test.Element("reason")?.Value ?? "";
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Reads a native-runner test result event (test-passed / test-failed /
    /// test-skipped / test-not-run) into a result row. The display name is
    /// resolved via <paramref name="displayNamesByTestId"/>, populated from
    /// the preceding test-starting events (result events do not repeat it).
    /// </summary>
    public static TestRowResult ReadNativeResultEvent(string eventType, JsonElement root,
        IReadOnlyDictionary<string, string> displayNamesByTestId)
    {
        var status = eventType switch
        {
            "test-passed" => TestStatus.Passed,
            "test-failed" => TestStatus.Failed,
            "test-skipped" => TestStatus.Skipped,
            "test-not-run" => TestStatus.NotRun,
            _ => (TestStatus?) null,
        } ?? throw new ArgumentException($"Not a result event: {eventType}", nameof(eventType));

        var displayName = "";
        if (root.TryGetProperty("TestDisplayName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            displayName = nameElement.GetString();
        else if (root.TryGetProperty("TestUniqueID", out var idElement) && idElement.ValueKind == JsonValueKind.String
            && displayNamesByTestId.TryGetValue(idElement.GetString(), out var known))
            displayName = known;

        var row = new TestRowResult
        {
            DisplayName = displayName,
            MethodFullName = MethodFullNameFromDisplayName(displayName),
            Status = status,
        };
        if (root.TryGetProperty("ExecutionTime", out var timeElement) && timeElement.ValueKind == JsonValueKind.Number)
            row.DurationSeconds = timeElement.GetDouble();
        if (status == TestStatus.Failed)
        {
            row.Message = JoinStringArray(root, "Messages");
            row.StackTrace = JoinStringArray(root, "StackTraces");
        }
        else if (status == TestStatus.Skipped && root.TryGetProperty("Reason", out var reason) && reason.ValueKind == JsonValueKind.String)
        {
            row.Message = reason.GetString();
        }
        return row;
    }

    static string JoinStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join("\n", array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()));
    }
}
