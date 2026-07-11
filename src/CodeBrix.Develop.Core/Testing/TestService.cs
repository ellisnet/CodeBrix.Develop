//
// TestService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (the CodeBrix.Develop analogue of MonoDevelop.UnitTesting's
//      UnitTestService + VsTest run adapter, rebuilt to drive xUnit.net v3
//      self-executing test projects directly)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Develop.Core.Projects;

namespace CodeBrix.Develop.Core.Testing;

/// <summary>
/// Discovers and runs the automated tests of the loaded solution. Discovery
/// is an instant Roslyn syntax scan (no build); runs build the test project
/// and execute its self-executing xUnit.net v3 binary directly, streaming
/// per-test results. Both runner CLI dialects are spoken: the native
/// in-process runner (JSON reporter), and the Microsoft.Testing.Platform
/// runner (detailed console output + xUnit XML report) that projects opt
/// into with &lt;UseMicrosoftTestingPlatformRunner&gt;. All events are
/// raised on background threads — UI consumers must marshal.
/// </summary>
public static class TestService
{
    static readonly object treeGate = new object();
    static readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
    static readonly BuildService buildService = new BuildService();
    static IReadOnlyList<TestNode> roots = Array.Empty<TestNode>();

    static TestService()
    {
        buildService.OutputReceived += line => OutputReceived?.Invoke(line);
    }

    /// <summary>The current test forest: one root node per test project, or empty.</summary>
    public static IReadOnlyList<TestNode> Roots => roots;

    /// <summary>Whether a test run is currently in progress.</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>Raised after discovery replaced the test forest.</summary>
    public static event Action TestsChanged;

    /// <summary>Raised when a test run starts (the targeted methods are already marked Running).</summary>
    public static event Action RunStarted;

    /// <summary>Raised each time a method node's status/result changed during a run.</summary>
    public static event Action<TestNode> TestFinished;

    /// <summary>Raised once when a test run completes (successfully or not).</summary>
    public static event Action<TestRunSummary> RunFinished;

    /// <summary>Raised for every line of build/runner output.</summary>
    public static event Action<string> OutputReceived;

    /// <summary>Whether the solution contains at least one runnable test project.</summary>
    public static bool SolutionHasTests(Solution solution)
        => solution != null && solution.Projects.Any(project => project.IsTestProject);

    /// <summary>
    /// Rebuilds the test forest for the given solution by scanning its test
    /// projects' sources. Statuses and results of tests that still exist are
    /// preserved. Concurrent calls coalesce; completion raises
    /// <see cref="TestsChanged"/>.
    /// </summary>
    public static async Task RefreshAsync(Solution solution)
    {
        if (solution == null || !SolutionHasTests(solution))
        {
            Clear();
            return;
        }

        await refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var forest = await Task.Run(() =>
            {
                var scanned = new List<TestNode>();
                foreach (var project in solution.Projects.Where(p => p.IsTestProject))
                {
                    var root = TestDiscovery.ScanProject(project);
                    if (root != null)
                        scanned.Add(root);
                }
                return scanned;
            }).ConfigureAwait(false);

            lock (treeGate)
            {
                PreserveResults(roots, forest);
                roots = forest;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Test discovery failed", ex);
        }
        finally
        {
            refreshLock.Release();
        }
        TestsChanged?.Invoke();
    }

    /// <summary>Empties the test forest (the solution closed).</summary>
    public static void Clear()
    {
        lock (treeGate)
            roots = Array.Empty<TestNode>();
        TestsChanged?.Invoke();
    }

    // Carries the previous run results over to the freshly scanned forest,
    // matched by project + method full name.
    static void PreserveResults(IReadOnlyList<TestNode> oldForest, IReadOnlyList<TestNode> newForest)
    {
        var known = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        foreach (var root in oldForest)
        {
            foreach (var method in root.EnumerateMethods())
                known[$"{root.Project.FileName}|{method.FullName}"] = method;
        }
        if (known.Count == 0)
            return;
        foreach (var root in newForest)
        {
            foreach (var method in root.EnumerateMethods())
            {
                if (!known.TryGetValue($"{root.Project.FileName}|{method.FullName}", out var old))
                    continue;
                method.LastResult = old.LastResult;
                method.Status = old.Status == TestStatus.Running ? TestStatus.NotRun : old.Status;
            }
        }
    }

    /// <summary>The method nodes declared in the given source file, in line order.</summary>
    public static IReadOnlyList<TestNode> GetTestsInFile(FilePath file)
    {
        var methods = new List<TestNode>();
        foreach (var root in roots)
        {
            foreach (var method in root.EnumerateMethods())
            {
                if (method.SourceFile == file)
                    methods.Add(method);
            }
        }
        methods.Sort((a, b) => a.SourceLine.CompareTo(b.SourceLine));
        return methods;
    }

    /// <summary>
    /// The test method the given 1-based line falls in (the method declared
    /// at or nearest above the line), or null when the line precedes every
    /// test in the file — the "run test at caret" resolution.
    /// </summary>
    public static TestNode FindTestAtLine(FilePath file, int line)
    {
        TestNode best = null;
        foreach (var method in GetTestsInFile(file))
        {
            if (method.SourceLine <= line)
                best = method;
        }
        return best;
    }

    /// <summary>
    /// The runner filter arguments selecting exactly this node's subtree, in
    /// the CLI dialect of the node's project — what a debug launch passes to
    /// the test executable. Empty for a whole-project node.
    /// </summary>
    public static IReadOnlyList<string> GetFilterArguments(TestNode node)
    {
        var mtp = node.Project.UsesMicrosoftTestingPlatformRunner;
        switch (node.Kind)
        {
            case TestNodeKind.Namespace:
                // The exact namespace plus its sub-namespaces (repeated
                // same-type filters OR together).
                var namespaceOption = mtp ? "--filter-namespace" : "-namespace";
                return new[] { namespaceOption, node.FullName, namespaceOption, node.FullName + ".*" };
            case TestNodeKind.Class:
                return new[] { mtp ? "--filter-class" : "-class", node.FullName };
            case TestNodeKind.Method:
                return new[] { mtp ? "--filter-method" : "-method", node.FullName };
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Builds the targeted projects and runs the selected tests, streaming
    /// per-test results via <see cref="TestFinished"/>. An empty selection
    /// runs every test in the solution. Returns the run summary (also raised
    /// via <see cref="RunFinished"/>). Cancellation kills the runner.
    /// </summary>
    public static async Task<TestRunSummary> RunAsync(IReadOnlyList<TestNode> selection, CancellationToken cancellationToken = default)
    {
        var summary = new TestRunSummary();
        if (IsRunning)
        {
            summary.Error = "A test run is already in progress";
            return summary;
        }
        IsRunning = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var targets = selection == null || selection.Count == 0
                ? roots.ToList()
                : Normalize(selection);
            var byProject = targets.GroupBy(node => node.ProjectRoot).ToList();
            if (byProject.Count == 0)
            {
                summary.Error = "No tests to run";
                return summary;
            }

            foreach (var group in byProject)
            {
                foreach (var target in group)
                {
                    foreach (var method in target.EnumerateMethods())
                        method.Status = TestStatus.Running;
                }
            }
            RunStarted?.Invoke();

            foreach (var group in byProject)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var project = group.Key.Project;

                var buildResult = await buildService.BuildAsync(project.FileName, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!buildResult.Success)
                {
                    summary.BuildFailed = true;
                    summary.Error = $"The build of {project.Name} failed — tests did not run.";
                    return summary;
                }

                var executable = await project.GetOutputExecutableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!File.Exists(executable))
                {
                    summary.Error = $"The built test executable was not found: {executable}";
                    return summary;
                }
                await RunProjectTestsAsync(group.Key, group.ToList(), executable, summary, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }
        catch (Exception ex)
        {
            LoggingService.LogError("The test run failed", ex);
            summary.Error = ex.Message;
        }
        finally
        {
            // Anything still marked Running did not report (filtered out,
            // cancelled, or the runner died) — fall back to its last result.
            foreach (var root in roots)
            {
                foreach (var method in root.EnumerateMethods())
                {
                    if (method.Status == TestStatus.Running)
                        method.Status = method.LastResult?.Status ?? TestStatus.NotRun;
                }
            }
            IsRunning = false;
            stopwatch.Stop();
            summary.Elapsed = stopwatch.Elapsed;
            RunFinished?.Invoke(summary);
        }
        return summary;
    }

    // Drops selected nodes that another selected node already contains.
    static List<TestNode> Normalize(IReadOnlyList<TestNode> selection)
    {
        var set = new HashSet<TestNode>(selection);
        var result = new List<TestNode>();
        foreach (var node in selection)
        {
            var covered = false;
            for (var ancestor = node.Parent; ancestor != null && !covered; ancestor = ancestor.Parent)
                covered = set.Contains(ancestor);
            if (!covered && !result.Contains(node))
                result.Add(node);
        }
        return result;
    }

    static List<string> BuildFilterArguments(List<TestNode> targets)
    {
        // Whole project selected → no filter. A single node → its own
        // (optimal) filter. A multi-selection is expanded to per-method
        // filters: repeated SAME-type simple filters OR together, while
        // mixing filter types would AND them — never what a multi-selection
        // means.
        if (targets.Any(target => target.Kind == TestNodeKind.Project))
            return new List<string>();
        if (targets.Count == 1)
            return GetFilterArguments(targets[0]).ToList();

        var mtp = targets[0].Project.UsesMicrosoftTestingPlatformRunner;
        var arguments = new List<string>();
        foreach (var method in targets.SelectMany(target => target.EnumerateMethods()).Distinct())
        {
            arguments.Add(mtp ? "--filter-method" : "-method");
            arguments.Add(method.FullName);
        }
        return arguments;
    }

    static async Task RunProjectTestsAsync(TestNode projectRoot, List<TestNode> targets, FilePath executable,
        TestRunSummary summary, CancellationToken cancellationToken)
    {
        var project = projectRoot.Project;
        var mtp = project.UsesMicrosoftTestingPlatformRunner;
        var methodsByFullName = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        foreach (var method in projectRoot.EnumerateMethods())
            methodsByFullName[method.FullName] = method;
        var accumulators = new Dictionary<TestNode, RowAccumulator>();

        var arguments = BuildFilterArguments(targets);
        string resultsDirectory = null;
        if (mtp)
        {
            resultsDirectory = Path.Combine(Path.GetTempPath(), "codebrix-develop", "test-results", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultsDirectory);
            arguments.AddRange(new[]
            {
                "--output", "Detailed", "--no-ansi", "--no-progress",
                "--report-xunit", "--results-directory", resultsDirectory,
            });
        }
        else
        {
            arguments.AddRange(new[] { "-reporter", "json", "-noLogo" });
        }

        var startInfo = CreateRunnerStartInfo(executable, arguments, project.BaseDirectory);
        OutputReceived?.Invoke($"{startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}");

        var displayNamesByTestId = new Dictionary<string, string>(StringComparer.Ordinal);
        var liveRows = new List<TestRowResult>();
        string reportPath = null;

        var exitCode = await RunProcessAsync(startInfo, line =>
        {
            if (mtp)
            {
                OutputReceived?.Invoke(line);
                if (XunitRunnerParsing.TryParseMtpResultLine(line) is { } liveRow)
                {
                    liveRows.Add(liveRow);
                    // Live feedback only — message/stack/duration are
                    // reconciled from the XML report after the run. A
                    // failure always wins; otherwise only the first row of
                    // the method flips it from Running.
                    if (FindOrAddMethod(projectRoot, methodsByFullName, liveRow.MethodFullName) is { } liveMethod
                        && (liveRow.Status == TestStatus.Failed || liveMethod.Status == TestStatus.Running)
                        && liveMethod.Status != liveRow.Status)
                    {
                        liveMethod.Status = liveRow.Status;
                        TestFinished?.Invoke(liveMethod);
                    }
                }
                else if (XunitRunnerParsing.TryParseMtpArtifactLine(line) is { } artifact)
                {
                    reportPath = artifact;
                }
                return;
            }

            var eventType = XunitRunnerParsing.TryParseNativeEvent(line, out var document);
            if (eventType == null)
            {
                var text = XunitRunnerParsing.StripAnsi(line);
                if (text.Trim().Length > 0)
                    OutputReceived?.Invoke(text);
                return;
            }
            using (document)
            {
                switch (eventType)
                {
                    case "test-starting":
                        if (document.RootElement.TryGetProperty("TestUniqueID", out var id)
                            && document.RootElement.TryGetProperty("TestDisplayName", out var name))
                            displayNamesByTestId[id.GetString()] = name.GetString();
                        break;
                    case "test-passed":
                    case "test-failed":
                    case "test-skipped":
                    case "test-not-run":
                        var row = XunitRunnerParsing.ReadNativeResultEvent(eventType, document.RootElement, displayNamesByTestId);
                        if (row.MethodFullName.Length > 0)
                            ApplyRow(projectRoot, methodsByFullName, accumulators, row, summary);
                        break;
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        if (mtp)
        {
            // The XML report is authoritative (it carries the failure
            // messages, stacks, and times the live lines lack); the live
            // rows remain the fallback if the runner died before writing it.
            var reportRows = TryReadXmlReport(reportPath, resultsDirectory) ?? liveRows;
            foreach (var row in reportRows.Where(r => r.MethodFullName.Length > 0))
                ApplyRow(projectRoot, methodsByFullName, accumulators, row, summary);
            TryDeleteDirectory(resultsDirectory);
        }

        // Native runners exit 1 with failing tests, MTP runners 2 — those are
        // results, not errors. Anything else (without results) is a crash.
        if (summary.Total == 0 && exitCode != 0 && !cancellationToken.IsCancellationRequested)
            summary.Error = $"The test runner for {project.Name} exited with code {exitCode} without reporting results.";
    }

    static ProcessStartInfo CreateRunnerStartInfo(FilePath executable, List<string> arguments, FilePath workingDirectory)
    {
        // GetOutputExecutableAsync may fall back to the managed .dll (e.g. a
        // customized output layout without an apphost) — run that via dotnet.
        ProcessStartInfo startInfo;
        if (executable.HasExtension(".dll"))
        {
            startInfo = new ProcessStartInfo("dotnet");
            startInfo.ArgumentList.Add(executable);
        }
        else
        {
            startInfo = new ProcessStartInfo(executable);
        }
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.EnvironmentVariables["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    static List<TestRowResult> TryReadXmlReport(string reportPath, string resultsDirectory)
    {
        try
        {
            // The artifact line names the report; if it was missed, a lone
            // .xunit file in the results directory is just as good.
            if (reportPath == null && Directory.Exists(resultsDirectory))
                reportPath = Directory.EnumerateFiles(resultsDirectory, "*.xunit").FirstOrDefault();
            if (reportPath == null || !File.Exists(reportPath))
                return null;
            return XunitRunnerParsing.ParseXunitXmlReport(File.ReadAllText(reportPath));
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Could not read the test report: {ex.Message}");
            return null;
        }
    }

    static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (directory != null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best effort — temp cleanup only
        }
    }

    static TestNode FindOrAddMethod(TestNode projectRoot, Dictionary<string, TestNode> methodsByFullName, string methodFullName)
    {
        if (methodFullName.Length == 0)
            return null;
        if (methodsByFullName.TryGetValue(methodFullName, out var method))
            return method;

        // A test the syntax scan missed (e.g. a derived fact attribute) —
        // add it to the tree so its result is not lost. "Ns.Cls.method"
        // splits on '.'; nested classes keep their '+' inside one segment.
        var segments = methodFullName.Split('.');
        if (segments.Length < 2)
            return null;
        var methodName = segments[^1];
        var className = segments[^2];
        var namespaceName = string.Join(".", segments[..^2]);

        lock (treeGate)
        {
            var parent = (TestNode) null;
            if (namespaceName.Length > 0)
            {
                parent = projectRoot.Children.FirstOrDefault(c => c.Kind == TestNodeKind.Namespace && c.FullName == namespaceName);
                if (parent == null)
                {
                    parent = new TestNode(TestNodeKind.Namespace, namespaceName, namespaceName, projectRoot.Project);
                    projectRoot.AddChild(parent);
                }
            }
            else
            {
                parent = projectRoot;
            }
            var classFullName = namespaceName.Length > 0 ? $"{namespaceName}.{className}" : className;
            var classNode = parent.Children.FirstOrDefault(c => c.Kind == TestNodeKind.Class && c.FullName == classFullName);
            if (classNode == null)
            {
                classNode = new TestNode(TestNodeKind.Class, className, classFullName, projectRoot.Project);
                parent.AddChild(classNode);
            }
            method = new TestNode(TestNodeKind.Method, methodName, methodFullName, projectRoot.Project);
            classNode.AddChild(method);
        }
        methodsByFullName[methodFullName] = method;
        TestsChanged?.Invoke();
        return method;
    }

    static void ApplyRow(TestNode projectRoot, Dictionary<string, TestNode> methodsByFullName,
        Dictionary<TestNode, RowAccumulator> accumulators, TestRowResult row, TestRunSummary summary)
    {
        var method = FindOrAddMethod(projectRoot, methodsByFullName, row.MethodFullName);
        if (method == null)
            return;

        if (!accumulators.TryGetValue(method, out var accumulator))
        {
            accumulator = new RowAccumulator();
            accumulators[method] = accumulator;
        }
        accumulator.Add(row);

        summary.Total++;
        switch (row.Status)
        {
            case TestStatus.Passed: summary.Passed++; break;
            case TestStatus.Failed: summary.Failed++; break;
            case TestStatus.Skipped: summary.Skipped++; break;
        }

        method.LastResult = accumulator.ToResult();
        method.Status = method.LastResult.Status;
        TestFinished?.Invoke(method);
    }

    // Aggregates a method's result rows (one per theory data row) into the
    // single result its tree node shows.
    class RowAccumulator
    {
        readonly List<TestRowResult> rows = new List<TestRowResult>();

        public void Add(TestRowResult row) => rows.Add(row);

        public TestRunResult ToResult()
        {
            var result = new TestRunResult
            {
                DurationSeconds = rows.Sum(row => row.DurationSeconds),
            };
            if (rows.Any(row => row.Status == TestStatus.Failed))
                result.Status = TestStatus.Failed;
            else if (rows.Any(row => row.Status == TestStatus.Passed))
                result.Status = TestStatus.Passed;
            else if (rows.Any(row => row.Status == TestStatus.Skipped))
                result.Status = TestStatus.Skipped;

            var failures = rows.Where(row => row.Status == TestStatus.Failed).ToList();
            if (failures.Count == 1)
            {
                result.Message = failures[0].Message;
                result.StackTrace = failures[0].StackTrace;
            }
            else if (failures.Count > 1)
            {
                // A multi-row theory failure: prefix each message/stack with
                // its row's display name so they stay tellable apart.
                result.Message = string.Join("\n\n", failures.Select(row => $"{row.DisplayName}:\n{row.Message}"));
                result.StackTrace = string.Join("\n\n", failures.Select(row => $"{row.DisplayName}:\n{row.StackTrace}"));
            }
            else if (result.Status == TestStatus.Skipped)
            {
                result.Message = rows.FirstOrDefault(row => row.Status == TestStatus.Skipped)?.Message ?? "";
            }
            return result;
        }
    }

    static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, Action<string> onLine, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start the test runner process");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // the process may have already exited
            }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }
}
