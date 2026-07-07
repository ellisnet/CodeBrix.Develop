// Copyright (c) Microsoft. All rights reserved.
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from MonoDevelop.Xml for CodeBrix.Develop; see
//      THIRD-PARTY-NOTICES.txt)
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
#nullable enable



using System;

namespace CodeBrix.Develop.Core.Xml.Analysis; //was previously: MonoDevelop.Xml.Analysis

[Flags]
public enum XmlDiagnosticSeverity
{
	None = 0,
	Suggestion = 1 << 0,
	Warning = 1 << 1,
	Error = 1 << 2
}
