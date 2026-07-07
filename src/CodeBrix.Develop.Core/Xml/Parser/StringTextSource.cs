// Copyright (c) Microsoft. All rights reserved.
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from MonoDevelop.Xml for CodeBrix.Develop; see
//      THIRD-PARTY-NOTICES.txt)
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
#nullable enable


using System.IO;

namespace CodeBrix.Develop.Core.Xml.Parser; //was previously: MonoDevelop.Xml.Parser

public class StringTextSource : ITextSource
{
	readonly string content;

	public StringTextSource (string content)
	{
		this.content = content;
	}

	public string? FileName { get; set; }
	public int Length => content.Length;
	public TextReader CreateReader () => new StringReader (content);
	public char this[int offset] => content[offset];
	public string GetText (int begin, int length) => content.Substring (begin, length);
}
