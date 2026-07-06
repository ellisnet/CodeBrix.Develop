//
// Solution.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Projects.Solution, rebuilt for
//      .sln/.slnx solutions in CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// A solution loaded from a .sln (classic), .slnx (XML), or single .csproj
/// file, holding the set of .NET projects it references.
/// </summary>
public class Solution
{
    // Project("{GUID}") = "Name", "relative\path.csproj", "{GUID}"
    static readonly Regex slnProjectRegex = new Regex(
        "^Project\\(\"\\{[^}]*\\}\"\\)\\s*=\\s*\"(?<name>[^\"]*)\",\\s*\"(?<path>[^\"]*)\",\\s*\"\\{(?<guid>[^}]*)\\}\"",
        RegexOptions.Compiled);

    readonly List<DotNetProject> projects = new List<DotNetProject>();

    /// <summary>The full path of the solution file.</summary>
    public FilePath FileName { get; private set; }

    /// <summary>The solution name (the file name without extension).</summary>
    public string Name => FileName.FileNameWithoutExtension;

    /// <summary>The directory containing the solution file.</summary>
    public FilePath BaseDirectory => FileName.ParentDirectory;

    /// <summary>The projects in this solution, in declaration order.</summary>
    public IReadOnlyList<DotNetProject> Projects => projects;

    /// <summary>The first executable project, used as the default run target.</summary>
    public DotNetProject StartupProject => projects.FirstOrDefault(p => p.IsExecutable);

    /// <summary>
    /// Loads a solution from a .sln, .slnx, or .csproj file (a single project
    /// is wrapped in an implicit solution).
    /// </summary>
    public static Solution Load(FilePath fileName)
    {
        if (!File.Exists(fileName))
            throw new FileNotFoundException("Solution file not found", fileName);

        var solution = new Solution { FileName = fileName.FullPath };

        IEnumerable<FilePath> projectPaths;
        if (fileName.HasExtension(".slnx"))
            projectPaths = ParseSlnx(solution.FileName);
        else if (fileName.HasExtension(".sln"))
            projectPaths = ParseSln(solution.FileName);
        else if (fileName.Extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            projectPaths = new[] { solution.FileName };
        else
            throw new NotSupportedException($"Unsupported solution format: {fileName.Extension}");

        foreach (var path in projectPaths)
        {
            if (!path.HasExtension(".csproj"))
            {
                LoggingService.LogWarning($"Skipping non-C# project: {path}");
                continue;
            }
            if (!File.Exists(path))
            {
                LoggingService.LogWarning($"Skipping missing project: {path}");
                continue;
            }
            solution.projects.Add(DotNetProject.Load(path));
        }

        return solution;
    }

    static IEnumerable<FilePath> ParseSln(FilePath fileName)
    {
        var baseDirectory = fileName.ParentDirectory;
        foreach (var line in File.ReadLines(fileName))
        {
            var match = slnProjectRegex.Match(line.TrimStart());
            if (!match.Success)
                continue;
            var relative = match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
            // Solution folders appear as Project entries without a file extension
            if (string.IsNullOrEmpty(Path.GetExtension(relative)))
                continue;
            yield return baseDirectory.Combine(relative).FullPath;
        }
    }

    static IEnumerable<FilePath> ParseSlnx(FilePath fileName)
    {
        var baseDirectory = fileName.ParentDirectory;
        var doc = XDocument.Load(fileName);
        foreach (var element in doc.Descendants("Project"))
        {
            var path = element.Attribute("Path")?.Value;
            if (string.IsNullOrEmpty(path))
                continue;
            var relative = path.Replace('\\', Path.DirectorySeparatorChar);
            yield return baseDirectory.Combine(relative).FullPath;
        }
    }
}
