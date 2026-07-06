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
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// A .NET project loaded from an SDK-style .csproj file.
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

    /// <summary>The Sdk attribute value, e.g. "Microsoft.NET.Sdk".</summary>
    public string Sdk { get; private set; }

    /// <summary>The OutputType property (defaults to "Library").</summary>
    public string OutputType { get; private set; }

    /// <summary>The target framework monikers declared by the project.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; private set; }

    /// <summary>Whether the project produces a runnable executable.</summary>
    public bool IsExecutable
        => string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads a project from an SDK-style .csproj file.</summary>
    public static DotNetProject Load(FilePath fileName)
    {
        if (!File.Exists(fileName))
            throw new FileNotFoundException("Project file not found", fileName);

        var project = new DotNetProject { FileName = fileName.FullPath };

        var doc = XDocument.Load(fileName);
        var root = doc.Root;
        project.Sdk = root?.Attribute("Sdk")?.Value ?? "";

        var properties = (root?.Elements("PropertyGroup").Elements() ?? Enumerable.Empty<XElement>()).ToList();
        project.OutputType = GetProperty(properties, "OutputType") ?? "Library";

        var single = GetProperty(properties, "TargetFramework");
        var multiple = GetProperty(properties, "TargetFrameworks");
        if (!string.IsNullOrEmpty(multiple))
            project.TargetFrameworks = multiple.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else if (!string.IsNullOrEmpty(single))
            project.TargetFrameworks = new[] { single };
        else
            project.TargetFrameworks = Array.Empty<string>();

        return project;
    }

    static string GetProperty(IEnumerable<XElement> properties, string name)
        => properties.FirstOrDefault(p => p.Name.LocalName == name)?.Value;

    /// <summary>
    /// Enumerates the files belonging to this project, mirroring the SDK's
    /// default globbing: everything under the project directory except
    /// bin/, obj/, and other tooling folders.
    /// </summary>
    public IEnumerable<FilePath> GetFiles()
        => EnumerateFiles(BaseDirectory);

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
