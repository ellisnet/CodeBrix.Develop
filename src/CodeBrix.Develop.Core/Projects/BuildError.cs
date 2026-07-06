//
// BuildError.cs
//
// Author:
//       Michael Hutchinson <m.j.hutchinson@gmail.com>
//
// Copyright (c) 2015 Xamarin Inc.
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from MonoDevelop for CodeBrix.Develop: .NET 10, modern C#)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System.Text;

namespace CodeBrix.Develop.Core.Projects; //was previously: MonoDevelop.Projects

/// <summary>
/// A single error or warning produced by a build, usually parsed from an
/// MSBuild-format console output line.
/// </summary>
public class BuildError
{
    /// <summary>Creates an empty build error.</summary>
    public BuildError() : this(string.Empty, 0, 0, string.Empty, string.Empty)
    {
    }

    /// <summary>Creates a build error with the given location and message details.</summary>
    public BuildError(string fileName, int line, int column, string errorNumber, string errorText)
    {
        FileName = fileName;
        Line = line;
        Column = column;
        ErrorNumber = errorNumber;
        ErrorText = errorText;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(FileName))
        {
            sb.Append(FileName);
            if (Line > 1)
            {
                sb.Append('(').Append(Line);
                if (Column > 1)
                    sb.Append(',').Append(Column);
                sb.Append(')');
            }
            sb.Append(" : ");
        }
        sb.Append(IsWarning ? "warning" : "error");

        if (!string.IsNullOrEmpty(ErrorNumber))
            sb.Append(' ').Append(ErrorNumber);

        sb.Append(": ").Append(ErrorText);
        return sb.ToString();
    }

    /// <summary>The file the error occurred in (may be a tool name, or empty).</summary>
    public string FileName { get; set; }

    /// <summary>The MSBuild subcategory text, if any.</summary>
    public string Subcategory { get; set; }

    /// <summary>Whether this is a warning rather than an error.</summary>
    public bool IsWarning { get; set; }

    /// <summary>The error code, e.g. "CS0103".</summary>
    public string ErrorNumber { get; set; }

    /// <summary>The error message text.</summary>
    public string ErrorText { get; set; }

    /// <summary>The MSBuild help keyword, if any.</summary>
    public string HelpKeyword { get; set; }

    /// <summary>The 1-based start line, or 0 when unknown.</summary>
    public int Line { get; set; }

    /// <summary>The 1-based start column, or 0 when unknown.</summary>
    public int Column { get; set; }

    /// <summary>The 1-based end line, or 0 when unknown.</summary>
    public int EndLine { get; set; }

    /// <summary>The 1-based end column, or 0 when unknown.</summary>
    public int EndColumn { get; set; }

    /// <summary>
    /// Parses a single console output line in the standard MSBuild error
    /// format, returning null when the line is not an error or warning.
    /// </summary>
    public static BuildError FromMSBuildErrorFormat(string lineText)
    {
        var result = MSBuildErrorParser.TryParseLine(lineText);
        if (result == null)
            return null;

        return new BuildError
        {
            FileName    = result.Origin ?? "",
            Subcategory = result.Subcategory,
            IsWarning   = !result.IsError,
            ErrorNumber = result.Code,
            ErrorText   = result.Message,
            Line        = result.Line,
            EndLine     = result.EndLine,
            Column      = result.Column,
            EndColumn   = result.EndColumn,
            HelpKeyword = result.HelpKeyword,
        };
    }
}
