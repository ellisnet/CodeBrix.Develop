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

    /// <summary>
    /// Chooses the .NET SDK for a build target for THIS service, overriding
    /// the IDE-wide <see cref="DotNetCli.SdkForTarget"/>; null (the default)
    /// defers to that shared choice. A project targeting a newer .NET than
    /// the system SDK is built with the installation that can actually build
    /// it, rather than failing with a misleading "target platform identifier
    /// ... was not recognized".
    /// </summary>
    public Func<FilePath, DotNetSdkInstallation> SdkForTarget { get; set; }

    /// <summary>
    /// Applies the chosen SDK to a process through <see cref="DotNetCli"/>:
    /// the executable to run, DOTNET_ROOT so a side-by-side installation
    /// resolves its own packs, and an environment free of this process's
    /// MSBuild registration. Returns the command name for the echoed command
    /// line.
    /// </summary>
    internal string ApplySdk(ProcessStartInfo startInfo, FilePath target)
    {
        var sdk = (SdkForTarget ?? DotNetCli.SdkForTarget)?.Invoke(target);
        return DotNetCli.ApplySdk(startInfo, sdk);
    }

    /// <summary>Whether a build or run operation is currently in progress.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Runs "dotnet build" against the given solution or project file.</summary>
    public Task<BuildResult> BuildAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("build", target, cancellationToken);

    /// <summary>
    /// Runs "dotnet build" with extra command-line arguments — the seam the
    /// frame-buffer emulator uses to inject its head-swap MSBuild property
    /// without ever touching the user's project files.
    /// </summary>
    public Task<BuildResult> BuildAsync(FilePath target, CancellationToken cancellationToken, params string[] extraArguments)
        => RunBuildVerbAsync("build", target, cancellationToken, extraArguments);

    /// <summary>Runs "dotnet clean" against the given solution or project file.</summary>
    public Task<BuildResult> CleanAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("clean", target, cancellationToken);

    /// <summary>Runs "dotnet build --no-incremental" against the given solution or project file.</summary>
    public Task<BuildResult> RebuildAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("build", target, cancellationToken, "--no-incremental");

    /// <summary>Runs "dotnet restore" against the given solution or project file.</summary>
    public Task<BuildResult> RestoreAsync(FilePath target, CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("restore", target, cancellationToken);

    /// <summary>
    /// Runs "dotnet publish" for the given project, framework-dependent, for
    /// the given runtime identifier, into <paramref name="outputDirectory"/> —
    /// the deploy staging for running the app on a remote device.
    /// </summary>
    public Task<BuildResult> PublishAsync(FilePath project, string runtimeIdentifier, FilePath outputDirectory,
        CancellationToken cancellationToken = default)
        => RunBuildVerbAsync("publish", project, cancellationToken,
            "-c", "Debug", "-r", runtimeIdentifier, "--self-contained", "false", "-o", outputDirectory);

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
            var command = ApplySdk(startInfo, target);
            startInfo.ArgumentList.Add(verb);
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-verbosity:minimal");
            foreach (var argument in extraArguments)
                startInfo.ArgumentList.Add(argument);

            OutputReceived?.Invoke($"{command} {string.Join(' ', startInfo.ArgumentList)}");

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
    public Task<int> RunAsync(DotNetProject project, CancellationToken cancellationToken = default)
        => RunAsync(project, null, cancellationToken);

    /// <summary>
    /// Starts "dotnet run" for the given project with extra command-line
    /// arguments, streaming its output. Returns the exit code when the
    /// application terminates.
    /// </summary>
    /// <param name="additionalArguments">
    /// Arguments appended after "--project &lt;path&gt;", or null for none.
    /// An Android launch passes "--device &lt;serial&gt;" here: with more than
    /// one device attached the SDK refuses to guess ("Unable to run this
    /// project because multiple devices are available"), and without it the
    /// device picker in the toolbar would not actually decide anything.
    /// NOTE this is a dotnet-run OPTION, not the MSBuild AdbTarget property
    /// that the build/Install path uses — "dotnet run -p:AdbTarget=…" does
    /// not select a device.
    /// </param>
    public async Task<int> RunAsync(DotNetProject project,
        IReadOnlyList<string> additionalArguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var command = ApplySdk(startInfo, project.FileName);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project.FileName);
        if (additionalArguments != null)
        {
            foreach (var argument in additionalArguments)
                startInfo.ArgumentList.Add(argument);
        }

        OutputReceived?.Invoke($"{command} {string.Join(' ', startInfo.ArgumentList)}");
        var exitCode = await RunProcessAsync(startInfo, line => OutputReceived?.Invoke(line), cancellationToken).ConfigureAwait(false);
        OutputReceived?.Invoke($"The application exited with code {exitCode}.");
        return exitCode;
    }

    /// <summary>
    /// Starts an already-built executable directly — no dotnet CLI in between —
    /// with extra environment variables, streaming its output. The
    /// cancellation token is a HARD KILL (the whole process tree); callers
    /// wanting a graceful stop first (the frame-buffer emulator closes its
    /// socket and lets the app power itself off) cancel this token only as
    /// their backstop. Returns the exit code when the application terminates.
    /// </summary>
    public async Task<int> RunExecutableAsync(FilePath executable, FilePath workingDirectory,
        IReadOnlyDictionary<string, string> environment, CancellationToken killToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (environment != null)
        {
            foreach (var pair in environment)
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
        }

        OutputReceived?.Invoke($"{executable}");
        var exitCode = await RunProcessAsync(startInfo, line => OutputReceived?.Invoke(line), killToken).ConfigureAwait(false);
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
