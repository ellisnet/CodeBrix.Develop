//
// DiagnosticInfo.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Core.TypeSystem;

/// <summary>Severity of a <see cref="DiagnosticInfo"/>.</summary>
public enum DiagnosticInfoSeverity
{
    /// <summary>Informational message.</summary>
    Info,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error.</summary>
    Error,
}

/// <summary>
/// One live diagnostic (compiler or XAML analysis) for an open document,
/// decoupled from Roslyn types so UI code does not need to reference
/// Microsoft.CodeAnalysis. Offsets are UTF-16 code units.
/// </summary>
public class DiagnosticInfo
{
    /// <summary>The diagnostic id ("CS0103", "XAML0001", ...), if any.</summary>
    public string Id { get; set; }

    /// <summary>The human-readable message.</summary>
    public string Message { get; set; }

    /// <summary>The severity.</summary>
    public DiagnosticInfoSeverity Severity { get; set; }

    /// <summary>UTF-16 start offset of the squiggled span.</summary>
    public int Start { get; set; }

    /// <summary>UTF-16 length of the squiggled span.</summary>
    public int Length { get; set; }
}
