// Copyright (c) Microsoft. All rights reserved.
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from MonoDevelop.Xml for CodeBrix.Develop; see
//      THIRD-PARTY-NOTICES.txt)
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
#nullable enable


using System.IO;

namespace CodeBrix.Develop.Core.Xml.Parser; //was previously: MonoDevelop.Xml.Parser

public interface ITextSource
{
	int Length { get; }
	char this[int offset] { get; }
	string GetText (int begin, int length);
	TextReader CreateReader ();
}

public static class TextSourceExtensions
{
	public static string GetTextBetween (this ITextSource source, int begin, int end) => source.GetText (begin, end - begin);
}
