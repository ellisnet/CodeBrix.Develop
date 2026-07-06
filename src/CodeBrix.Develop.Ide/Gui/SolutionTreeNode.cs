//
// SolutionTreeNode.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Ide.Gui.Pads.ProjectPad tree nodes, rebuilt
//      as a GObject list-model item for GTK 4)
// SPDX-License-Identifier: MIT
//

using System;
using CodeBrix.Develop.Core;
using GObject = CodeBrix.Develop.UI.GObject;

namespace CodeBrix.Develop.Ide.Gui;

/// <summary>The kind of node shown in the Solution pad tree.</summary>
public enum SolutionTreeNodeKind
{
    /// <summary>The solution root node.</summary>
    Solution,
    /// <summary>A solution folder (e.g. "Libraries", "Tests") grouping projects; nothing on disk.</summary>
    SolutionFolder,
    /// <summary>A project node.</summary>
    Project,
    /// <summary>A directory inside a project.</summary>
    Folder,
    /// <summary>A file.</summary>
    File,
}

/// <summary>
/// A node of the Solution pad tree. Registered as a GObject subclass so
/// instances can live inside Gio list models feeding the GTK 4 ListView.
/// </summary>
[GObject.Subclass<GObject.Object>]
public partial class SolutionTreeNode
{
    /// <summary>The text shown for the node.</summary>
    public string Title { get; set; } = "";

    /// <summary>The themed icon name shown for the node.</summary>
    public string IconName { get; set; } = "text-x-generic-symbolic";

    /// <summary>The file-system path the node represents.</summary>
    public string Path { get; set; } = "";

    /// <summary>What the node represents.</summary>
    public SolutionTreeNodeKind Kind { get; set; }

    /// <summary>Whether the node can be expanded to show children.</summary>
    public bool HasChildren => Kind != SolutionTreeNodeKind.File;

    /// <summary>The file-system path as a <see cref="FilePath"/>.</summary>
    public FilePath FilePath => new FilePath(Path);

    /// <summary>Creates a node for the given kind, title, and path.</summary>
    public static SolutionTreeNode Create(SolutionTreeNodeKind kind, string title, FilePath path)
    {
        var node = NewWithProperties(Array.Empty<GObject.ConstructArgument>());
        node.Kind = kind;
        node.Title = title;
        node.Path = path.IsNull ? "" : (string) path;
        node.IconName = kind switch
        {
            SolutionTreeNodeKind.Solution => "solution-16",
            SolutionTreeNodeKind.SolutionFolder => "folder-solution-16",
            SolutionTreeNodeKind.Project when path.HasExtension(".shproj") => "project-crossplatform-shared-32",
            SolutionTreeNodeKind.Project => "project-16",
            SolutionTreeNodeKind.Folder => "folder-generic-16",
            _ => GetFileIconName(path),
        };
        return node;
    }

    // Logical MonoDevelop icon names by file extension.
    static string GetFileIconName(FilePath path)
    {
        var extension = path.IsNullOrEmpty ? "" : path.Extension.ToLowerInvariant();
        return extension switch
        {
            ".cs" or ".fs" or ".vb" => "file-source-16",
            ".xml" or ".xaml" or ".axaml" or ".html" or ".htm" or ".config"
                or ".csproj" or ".fsproj" or ".shproj" or ".projitems"
                or ".sln" or ".slnx" or ".props" or ".targets" => "file-web-16",
            ".css" => "file-css-16",
            ".scss" => "file-scss-16",
            ".less" => "file-less-16",
            ".js" or ".mjs" => "file-js-16",
            ".ts" => "file-ts-16",
            ".txt" or ".md" or ".log" => "file-text-16",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or ".bmp" or ".webp" => "file-image-16",
            ".resx" or ".resource" => "file-resource-16",
            ".json" or ".yaml" or ".yml" or ".sh" or ".py" => "file-script-16",
            _ => "file-generic-16",
        };
    }
}
