//
// ProjectPackageReference.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Core.Projects;

/// <summary>
/// A NuGet package reference declared by a project: the package id and the
/// referenced version ("" when the item carries no Version, e.g. under
/// central package management).
/// </summary>
public sealed class ProjectPackageReference
{
    /// <summary>The NuGet package id.</summary>
    public string Id { get; init; }

    /// <summary>The referenced version string, or "" when not specified.</summary>
    public string Version { get; init; }
}
