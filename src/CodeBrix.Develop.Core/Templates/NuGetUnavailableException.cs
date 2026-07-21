//
// NuGetUnavailableException.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// Thrown when nuget.org could not supply version information the package
/// version policy needs. nuget.org is the only authoritative source of
/// package versions, so a failed lookup ends the version-bump operation
/// before any project file is written. This is the benign failure: the
/// generated solution keeps the versions it was created with, and the New
/// Application process still succeeds.
/// </summary>
public class NuGetUnavailableException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public NuGetUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public NuGetUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
