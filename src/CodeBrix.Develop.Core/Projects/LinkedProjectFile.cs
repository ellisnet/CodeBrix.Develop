//
// LinkedProjectFile.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// A file compiled or embedded into a project from outside its folder — an
/// item with Link metadata, e.g.
/// &lt;Compile Include="..\Shared\MainViewModel.cs" Link="ViewModels\MainViewModel.cs"/&gt;.
/// The file appears in the project tree at its virtual link path while
/// living elsewhere on disk.
/// </summary>
public sealed class LinkedProjectFile
{
    /// <summary>The real, absolute path of the file on disk.</summary>
    public FilePath RealPath { get; init; }

    /// <summary>
    /// The project-relative virtual path the file appears at (native
    /// directory separators, no leading separator).
    /// </summary>
    public string LinkPath { get; init; }
}
