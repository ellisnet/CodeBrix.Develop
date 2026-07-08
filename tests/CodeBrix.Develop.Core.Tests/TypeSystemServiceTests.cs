//
// TypeSystemServiceTests.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System.Collections.Generic;
using CodeBrix.Develop.Core;
using CodeBrix.Develop.Core.TypeSystem;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class TypeSystemServiceTests
{
    [Fact]
    public void IsBreakableLine_distinguishes_code_from_blank_comment_and_declaration_lines()
    {
        //Arrange — line numbers are 1-based; see expectations below.
        const string source = """
            using System;

            namespace Demo;

            public abstract class Widget
            {
                int count = 40 + 2;

                public int Count => count;

                public abstract void NotHere();

                public void Run()
                {
                    // a comment-only line
                    Console.WriteLine(count);
                }
            }
            """;
        var file = new FilePath("/tmp/Widget.cs");

        //Act
        var breakable = new List<int>();
        for (var line = 1; line <= 18; line++)
        {
            if (TypeSystemService.IsBreakableLine(file, source, line))
                breakable.Add(line);
        }

        //Assert — the field initializer (7), the expression-bodied property
        //(9), the Run header/braces/statement (13, 14, 16, 17); NOT usings,
        //namespace/type headers, type braces, blanks, comments, or the
        //bodiless abstract method.
        string.Join(",", breakable).Should().Be("7,9,13,14,16,17");
    }

    [Fact]
    public void IsBreakableLine_is_false_beyond_the_end_of_the_file()
        => TypeSystemService.IsBreakableLine(new FilePath("/tmp/Widget.cs"), "var x = 1;", 5).Should().BeFalse();

    [Fact]
    public void IsBreakableLine_permits_non_csharp_files()
        => TypeSystemService.IsBreakableLine(new FilePath("/tmp/Main.xaml"), "<Page/>", 1).Should().BeTrue();
}
