//
// BuildService.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop's project build operations, rebuilt on the
//      dotnet CLI for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// Builds, cleans, and runs solutions and projects by driving the dotnet
/// CLI, streaming console output and parsing MSBuild-format errors.
/// </summary>
public class BuildService
{
    /// <summary>Raised for every line of build/run output, on a background thread.</summary>
    public event Action<string> OutputReceived;

    /// <summary>Whether a build or run operation is currently in progress.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Runs "dotnet build" against the given solution or project file.</summary>
    public Task<BuildResult> BuildAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("build", target, cancellationToken);

    /// <summary>Runs "dotnet clean" against the given solution or project file.</summary>
    public Task<BuildResult> CleanAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("clean", target, cancellationToken);

    /// <summary>Runs "dotnet build --no-incremental" against the given solution or project file.</summary>
    public Task<BuildResult> RebuildAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("build", target, cancellationToken, "--no-incremental");

    /// <summary>Runs "dotnet restore" against the given solution or project file.</summary>
    public Task<BuildResult> RestoreAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("restore", target, cancellationToken);

    async Task<BuildResult> RunBuildVerbAsync(string verb, FilePath target, CancellationToken cancellationToken, params string[] extraArguments)
    {
        var result = new BuildResult();
        var stopwatch = Stopwatch.StartNew();
        IsBusy = true;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = target.ParentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(verb);
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-verbosity:minimal");
            foreach (var argument in extraArguments)
                startInfo.ArgumentList.Add(argument);

            OutputReceived?.Invoke($"dotnet {string.Join(' ', startInfo.ArgumentList)}");

            // MSBuild repeats each diagnostic in the end-of-build summary;
            // key on location+code+message so each one is reported once.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var exitCode = await RunProcessAsync(startInfo, line =>
            {
                OutputReceived?.Invoke(line);
                var error = BuildError.FromMSBuildErrorFormat(line);
                if (error != null && seen.Add($"{error.FileName}|{error.Line}|{error.Column}|{error.ErrorNumber}|{error.ErrorText}"))
                    result.Append(error);
            }, cancellationToken).ConfigureAwait(false);

            result.Success = exitCode == 0;
        }
        finally
        {
            IsBusy = false;
            stopwatch.Stop();
            result.Elapsed = stopwatch.Elapsed;
        }
        return result;
    }

    /// <summary>
    /// Starts "dotnet run" for the given project, streaming its output.
    /// Returns the exit code when the application terminates.
    /// </summary>
    public async Task<int> RunAsync(DotNetProject project, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project.FileName);

        OutputReceived?.Invoke($"dotnet run --project {project.FileName}");
        var exitCode = await RunProcessAsync(startInfo, line => OutputReceived?.Invoke(line), cancellationToken).ConfigureAwait(false);
        OutputReceived?.Invoke($"The application exited with code {exitCode}.");
        return exitCode;
    }

    static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, Action<string> onLine, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start the dotnet process");
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
