//
// ClassifiedSpanInfo.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Core.TypeSystem;

/// <summary>
/// One classified span of a source document, decoupled from Roslyn types so
/// UI code does not need to reference Microsoft.CodeAnalysis. Offsets are
/// UTF-16 code units.
/// </summary>
public class ClassifiedSpanInfo
{
    /// <summary>UTF-16 start offset of the span.</summary>
    public int Start { get; set; }

    /// <summary>UTF-16 length of the span.</summary>
    public int Length { get; set; }

    /// <summary>
    /// The Roslyn classification type name ("class name", "keyword - control",
    /// "parameter name", ...).
    /// </summary>
    public string Classification { get; set; }
}
