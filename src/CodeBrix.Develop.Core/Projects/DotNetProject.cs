//
// DotNetProject.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Projects.DotNetProject, rebuilt for
//      SDK-style projects in CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// A project loaded from an SDK-style .csproj file, or a Visual Studio
/// shared project (.shproj — a container of source files compiled into each
/// project that imports its sibling .projitems, producing no assembly of
/// its own).
/// </summary>
public class DotNetProject
{
    static readonly string[] excludedDirectoryNames = { "bin", "obj", ".git", ".vs", "node_modules" };

    /// <summary>The full path of the .csproj file.</summary>
    public FilePath FileName { get; private set; }

    /// <summary>The project name (the file name without extension).</summary>
    public string Name => FileName.FileNameWithoutExtension;

    /// <summary>The directory containing the project file.</summary>
    public FilePath BaseDirectory => FileName.ParentDirectory;

    /// <summary>
    /// The solution folder this project appears under in its solution (e.g.
    /// "Tests"), or "" when it sits at the solution root. Solution folders
    /// are an organizational grouping only — nothing exists on disk.
    /// </summary>
    public string SolutionFolder { get; internal set; } = "";

    /// <summary>The Sdk attribute value, e.g. "Microsoft.NET.Sdk".</summary>
    public string Sdk { get; private set; }

    /// <summary>The OutputType property (defaults to "Library").</summary>
    public string OutputType { get; private set; }

    /// <summary>The target framework monikers declared by the project.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; private set; }

    /// <summary>Whether the project produces a runnable executable.</summary>
    public bool IsExecutable
        => !IsSharedProject
        && (string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this is a shared project (.shproj): it is never built or
    /// loaded into the type system itself — its files are compiled into
    /// each project that imports the sibling .projitems file.
    /// </summary>
    public bool IsSharedProject => FileName.HasExtension(".shproj");

    /// <summary>
    /// Files included with Link metadata: they live outside the project
    /// folder but appear in the project at their virtual link paths.
    /// </summary>
    public IReadOnlyList<LinkedProjectFile> LinkedFiles { get; private set; } = Array.Empty<LinkedProjectFile>();

    /// <summary>
    /// Project-relative folder paths declared with &lt;Folder Include="…"/&gt;
    /// items (normalized, no trailing separator) — folders the project shows
    /// even when nothing exists on disk.
    /// </summary>
    public IReadOnlyList<string> DeclaredFolders { get; private set; } = Array.Empty<string>();

    /// <summary>The NuGet packages the project references, in declaration order.</summary>
    public IReadOnlyList<ProjectPackageReference> PackageReferences { get; private set; } = Array.Empty<ProjectPackageReference>();

    /// <summary>
    /// Whether the project has a PackageReference to the given package id
    /// (NuGet ids compare case-insensitively).
    /// </summary>
    public bool HasPackageReference(string packageId)
        => PackageReferences.Any(reference => string.Equals(reference.Id, packageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this is a test project the IDE can discover and run tests in:
    /// an xUnit.net v3 project, whose xunit.v3 package makes the build output
    /// a self-executing test binary.
    /// </summary>
    public bool IsTestProject => !IsSharedProject && HasPackageReference("xunit.v3");

    /// <summary>
    /// Whether the test project opts into the Microsoft.Testing.Platform
    /// runner (&lt;UseMicrosoftTestingPlatformRunner&gt;true&lt;/&gt;, the
    /// CodeBrix family test convention) — its executable then speaks the MTP
    /// command line instead of the native xUnit.net runner CLI.
    /// </summary>
    public bool UsesMicrosoftTestingPlatformRunner { get; private set; }

    /// <summary>Loads a project from an SDK-style .csproj or a shared .shproj file.</summary>
    public static DotNetProject Load(FilePath fileName)
    {
        if (!File.Exists(fileName))
            throw new FileNotFoundException("Project file not found", fileName);

        var project = new DotNetProject { FileName = fileName.FullPath };
        project.ParseProjectFile();
        return project;
    }

    /// <summary>
    /// Re-reads the project file from disk, refreshing all parsed properties
    /// — e.g. after package-reference versions were rewritten.
    /// </summary>
    public void RefreshFromDisk() => ParseProjectFile();

    void ParseProjectFile()
    {
        var project = this;
        var doc = XDocument.Load(FileName);
        var root = doc.Root;
        project.Sdk = root?.Attribute("Sdk")?.Value ?? "";

        var properties = (root?.Elements("PropertyGroup").Elements() ?? Enumerable.Empty<XElement>()).ToList();
        project.OutputType = GetProperty(properties, "OutputType") ?? "Library";
        project.UsesMicrosoftTestingPlatformRunner =
            string.Equals(GetProperty(properties, "UseMicrosoftTestingPlatformRunner"), "true", StringComparison.OrdinalIgnoreCase);

        var single = GetProperty(properties, "TargetFramework");
        var multiple = GetProperty(properties, "TargetFrameworks");
        if (!string.IsNullOrEmpty(multiple))
            project.TargetFrameworks = multiple.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else if (!string.IsNullOrEmpty(single))
            project.TargetFrameworks = new[] { single };
        else
            project.TargetFrameworks = Array.Empty<string>();

        var linkedFiles = new List<LinkedProjectFile>();
        var declaredFolders = new List<string>();
        var packageReferences = new List<ProjectPackageReference>();
        foreach (var item in root?.Elements("ItemGroup").Elements() ?? Enumerable.Empty<XElement>())
        {
            var include = item.Attribute("Include")?.Value;
            // Globs and MSBuild expressions would need full evaluation; only
            // literal paths are resolved here.
            if (string.IsNullOrEmpty(include) || include.Contains('$') || include.Contains('*'))
                continue;
            if (item.Name.LocalName == "PackageReference")
            {
                packageReferences.Add(new ProjectPackageReference
                {
                    Id = include,
                    Version = item.Attribute("Version")?.Value ?? item.Element("Version")?.Value ?? "",
                });
                continue;
            }
            if (item.Name.LocalName == "Folder")
            {
                var folder = NormalizeRelativePath(include);
                if (folder.Length > 0)
                    declaredFolders.Add(folder);
                continue;
            }
            var link = item.Attribute("Link")?.Value ?? item.Element("Link")?.Value;
            if (string.IsNullOrEmpty(link) || link.Contains('$'))
                continue;
            linkedFiles.Add(new LinkedProjectFile
            {
                RealPath = new FilePath(Path.GetFullPath(Path.Combine(project.BaseDirectory, NormalizeRelativePath(include)))),
                LinkPath = NormalizeRelativePath(link),
            });
        }
        project.LinkedFiles = linkedFiles;
        project.DeclaredFolders = declaredFolders;
        project.PackageReferences = packageReferences;
    }

    static string GetProperty(IEnumerable<XElement> properties, string name)
        => properties.FirstOrDefault(p => p.Name.LocalName == name)?.Value;

    /// <summary>
    /// The path of the built executable (the Linux apphost) for the given
    /// configuration, assuming the SDK's DEFAULT output layout. Projects can
    /// redirect their output (OutputPath, AssemblyName,
    /// AppendTargetFrameworkToOutputPath, UseArtifactsOutput, …), so prefer
    /// <see cref="GetOutputExecutableAsync"/>, which asks MSBuild; this
    /// convention-based guess is its fallback.
    /// </summary>
    public FilePath GetOutputExecutable(string configuration = "Debug")
    {
        var targetFramework = TargetFrameworks.Count > 0 ? TargetFrameworks[0] : "net10.0";
        return new FilePath(Path.Combine(BaseDirectory, "bin", configuration, targetFramework, Name));
    }

    /// <summary>
    /// Resolves the built executable for the given configuration by asking
    /// MSBuild for the project's evaluated RunCommand and TargetPath, so
    /// customized output layouts are honored. Returns the apphost when it
    /// exists on disk, otherwise the managed output assembly (which the
    /// debugger accepts just as well), otherwise the SDK-default guess of
    /// <see cref="GetOutputExecutable"/>.
    /// </summary>
    public async Task<FilePath> GetOutputExecutableAsync(string configuration = "Debug", CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = await EvaluateOutputPropertiesAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (properties.TryGetValue("RunCommand", out var runCommand) && File.Exists(runCommand))
                return new FilePath(runCommand);
            if (properties.TryGetValue("TargetPath", out var targetPath) && !string.IsNullOrEmpty(targetPath))
                return new FilePath(targetPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggingService.LogWarning($"MSBuild output-path evaluation failed for {Name}; assuming the default layout. {ex.Message}");
        }
        return GetOutputExecutable(configuration);
    }

    // Evaluates the project (no build) with "dotnet msbuild -getProperty:",
    // returning the requested properties from the JSON it prints.
    async Task<Dictionary<string, string>> EvaluateOutputPropertiesAsync(string configuration, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(FileName);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add($"-property:Configuration={configuration}");
        // A multi-targeting project has one output per framework and defines
        // neither RunCommand nor TargetPath in its outer evaluation; pin the
        // first framework, matching what GetOutputExecutable assumes.
        if (TargetFrameworks.Count > 1)
            startInfo.ArgumentList.Add($"-property:TargetFramework={TargetFrameworks[0]}");
        startInfo.ArgumentList.Add("-getProperty:RunCommand,TargetPath");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start the dotnet process");
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

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"dotnet msbuild exited with code {process.ExitCode}: {detail.Trim()}");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.TryGetProperty("Properties", out var table) && table.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in table.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = property.Value.GetString();
            }
        }
        return result;
    }

    /// <summary>
    /// Enumerates the files belonging to this project, mirroring the SDK's
    /// default globbing: everything under the project directory except
    /// bin/, obj/, and other tooling folders.
    /// </summary>
    public IEnumerable<FilePath> GetFiles()
        => EnumerateFiles(BaseDirectory);

    /// <summary>
    /// The linked files that appear directly inside the given
    /// project-relative directory ("" for the project root).
    /// </summary>
    public IEnumerable<LinkedProjectFile> GetLinkedFilesIn(string relativeDirectory)
    {
        var directory = NormalizeRelativePath(relativeDirectory ?? "");
        foreach (var linked in LinkedFiles)
        {
            if (string.Equals(Path.GetDirectoryName(linked.LinkPath) ?? "", directory, StringComparison.Ordinal))
                yield return linked;
        }
    }

    /// <summary>
    /// The names of the VIRTUAL child folders of the given project-relative
    /// directory ("" for the project root): folders implied by linked-file
    /// link paths or declared as &lt;Folder/&gt; items, that do not exist on
    /// disk (existing ones are shown by normal directory enumeration).
    /// </summary>
    public IEnumerable<string> GetVirtualFolderNamesIn(string relativeDirectory)
    {
        var directory = NormalizeRelativePath(relativeDirectory ?? "");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = LinkedFiles.Select(linked => Path.GetDirectoryName(linked.LinkPath) ?? "").Concat(DeclaredFolders);
        foreach (var candidate in candidates)
        {
            var segment = GetChildSegment(candidate, directory);
            if (segment != null && !Directory.Exists(Path.Combine(BaseDirectory, directory, segment)))
                names.Add(segment);
        }
        return names;
    }

    // The immediate child-folder name that a relative path contributes to a
    // directory, or null when the path is not beneath it ("A/B/C" is "A" at
    // the root and "B" inside "A").
    static string GetChildSegment(string path, string relativeDirectory)
    {
        if (path.Length == 0)
            return null;
        var rest = path;
        if (relativeDirectory.Length > 0)
        {
            var prefix = relativeDirectory + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                return null;
            rest = path[prefix.Length..];
            if (rest.Length == 0)
                return null;
        }
        var separator = rest.IndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? rest : rest[..separator];
    }

    // MSBuild item paths use backslashes by convention; normalize either
    // separator style to the native one and drop trailing separators.
    static string NormalizeRelativePath(string path)
        => path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Enumerates the immediate subdirectories of a project folder that belong to the project.</summary>
    public static IEnumerable<FilePath> GetVisibleDirectories(FilePath directory)
        => Directory.EnumerateDirectories(directory)
            .Where(d => !IsExcluded(Path.GetFileName(d)))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => (FilePath) d);

    /// <summary>Enumerates the files directly inside a project folder that belong to the project.</summary>
    public static IEnumerable<FilePath> GetVisibleFiles(FilePath directory)
        => Directory.EnumerateFiles(directory)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(f => (FilePath) f);

    static bool IsExcluded(string directoryName)
        => directoryName.StartsWith('.')
        || excludedDirectoryNames.Contains(directoryName, StringComparer.OrdinalIgnoreCase);

    static IEnumerable<FilePath> EnumerateFiles(FilePath directory)
    {
        foreach (var file in GetVisibleFiles(directory))
            yield return file;
        foreach (var sub in GetVisibleDirectories(directory))
            foreach (var file in EnumerateFiles(sub))
                yield return file;
    }
}
