//
// FilePath.cs
//
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2011 Novell, Inc (http://www.novell.com)
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core; //was previously: MonoDevelop.Core

/// <summary>
/// An immutable file-system path with value semantics and rich path
/// operations, used throughout CodeBrix.Develop instead of raw strings.
/// Implicitly convertible to and from <see cref="string"/>.
/// </summary>
public readonly struct FilePath : IComparable<FilePath>, IComparable, IEquatable<FilePath>
{
    /// <summary>Comparer matching the case sensitivity of the host file system.</summary>
    public static readonly StringComparer PathComparer =
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Comparison mode matching the case sensitivity of the host file system.</summary>
    public static readonly StringComparison PathComparison =
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    readonly string fileName;

    /// <summary>A null path.</summary>
    public static readonly FilePath Null = new FilePath(null);

    /// <summary>An empty (zero-length) path.</summary>
    public static readonly FilePath Empty = new FilePath(string.Empty);

    /// <summary>Creates a path from a string; file:// URIs are converted to local paths.</summary>
    public FilePath(string name)
    {
        if (name != null && name.Length > 6 && name[0] == 'f' && name.StartsWith("file://", StringComparison.Ordinal))
            name = new Uri(name).LocalPath;

        fileName = name;
    }

    /// <summary>Whether the underlying path string is null.</summary>
    public bool IsNull => fileName == null;

    /// <summary>Whether the underlying path string is null or empty.</summary>
    public bool IsNullOrEmpty => string.IsNullOrEmpty(fileName);

    /// <summary>Whether the underlying path string is not null.</summary>
    public bool IsNotNull => fileName != null;

    /// <summary>Whether the underlying path string is empty (but not null).</summary>
    public bool IsEmpty => fileName != null && fileName.Length == 0;

    /// <summary>Resolves symbolic links, returning the final physical path.</summary>
    public FilePath ResolveLinks()
    {
        if (OperatingSystem.IsWindows())
            return Path.GetFullPath(this);

        try
        {
            //was previously a libc realpath() P/Invoke; .NET now resolves links itself
            var target = File.ResolveLinkTarget(fileName, returnFinalTarget: true);
            return target != null ? target.FullName : Path.GetFullPath(fileName);
        }
        catch (Exception)
        {
            return Path.GetFullPath(fileName);
        }
    }

    /// <summary>The absolute form of this path.</summary>
    public FilePath FullPath
        => new FilePath(!string.IsNullOrEmpty(fileName) ? Path.GetFullPath(fileName) : "");

    /// <summary>Whether this path refers to an existing directory.</summary>
    public bool IsDirectory => Directory.Exists(FullPath);

    /// <summary>
    /// Returns a path in standard form, which can be used to be compared
    /// for equality with other canonical paths. It is similar to FullPath,
    /// but unlike FullPath, the directory "/a/b" is considered equal to "/a/b/"
    /// </summary>
    public FilePath CanonicalPath
    {
        get
        {
            if (fileName == null)
                return Null;
            if (fileName.Length == 0)
                return Empty;
            string fp = Path.GetFullPath(fileName);
            if (fp.Length > 0)
            {
                if (fp[fp.Length - 1] == Path.DirectorySeparatorChar)
                    return fp.TrimEnd(Path.DirectorySeparatorChar);
                if (fp[fp.Length - 1] == Path.AltDirectorySeparatorChar)
                    return fp.TrimEnd(Path.AltDirectorySeparatorChar);
            }
            return fp;
        }
    }

    /// <summary>The file name and extension without the directory.</summary>
    public string FileName => Path.GetFileName(fileName);

    internal bool HasFileName(string name)
    {
        return fileName.Length > name.Length
            && fileName.EndsWith(name, PathComparison)
            && fileName[fileName.Length - name.Length - 1] == Path.DirectorySeparatorChar;
    }

    /// <summary>The extension of the file name, including the leading dot.</summary>
    public string Extension => Path.GetExtension(fileName);

    /// <summary>Whether the file name ends with the given extension (compared with the host file-system case sensitivity).</summary>
    public bool HasExtension(string extension)
    {
        return fileName.Length > extension.Length
            && (extension == string.Empty
                ? HasNoExtension(fileName)
                : fileName.EndsWith(extension, PathComparison) && fileName[fileName.Length - extension.Length] == '.');

        static bool HasNoExtension(string path)
        {
            // Look for the last dot that's after the last path separator
            for (int i = path.Length - 1; i >= 0; --i)
            {
                var ch = path[i];
                if (ch == '.')
                {
                    // If the dot is the last character we have no extension
                    return i == path.Length - 1;
                }

                if (ch == Path.DirectorySeparatorChar)
                    return true;
            }

            return true;
        }
    }

    /// <summary>The file name without directory or extension.</summary>
    public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(fileName);

    /// <summary>The directory containing this path.</summary>
    public FilePath ParentDirectory => new FilePath(Path.GetDirectoryName(fileName));

    /// <summary>Whether this path is rooted.</summary>
    public bool IsAbsolute => Path.IsPathRooted(fileName);

    /// <summary>Whether this path is located inside (or equals a child of) the given base path.</summary>
    public bool IsChildPathOf(FilePath basePath)
    {
        if (string.IsNullOrEmpty(basePath.fileName) || string.IsNullOrEmpty(fileName))
            return false;
        bool startsWith = fileName.StartsWith(basePath.fileName, PathComparison);
        if (startsWith && basePath.fileName[basePath.fileName.Length - 1] != Path.DirectorySeparatorChar &&
            basePath.fileName[basePath.fileName.Length - 1] != Path.AltDirectorySeparatorChar)
        {
            // If the last character isn't a path separator character, check whether the string we're searching in
            // has more characters than the string we're looking for then check the character.
            // Otherwise, if the path lengths are equal, we return false.
            if (fileName.Length > basePath.fileName.Length)
                startsWith &= fileName[basePath.fileName.Length] == Path.DirectorySeparatorChar || fileName[basePath.fileName.Length] == Path.AltDirectorySeparatorChar;
            else
                startsWith = false;
        }
        return startsWith;
    }

    /// <summary>Returns this path with the extension replaced.</summary>
    public FilePath ChangeExtension(string ext) => Path.ChangeExtension(fileName, ext);

    /// <summary>
    /// Returns a file path with the name changed to the provided name, but keeping the extension
    /// </summary>
    /// <returns>The new file path</returns>
    /// <param name="newName">New file name</param>
    public FilePath ChangeName(string newName) => ParentDirectory.Combine(newName) + Extension;

    /// <summary>Combines this path with the given path segments.</summary>
    public FilePath Combine(params FilePath[] paths)
    {
        string path = fileName;
        foreach (FilePath p in paths)
            path = Path.Combine(path, p.fileName);
        return new FilePath(path);
    }

    /// <summary>Combines this path with the given path segment.</summary>
    public FilePath Combine(FilePath path) => new FilePath(Path.Combine(fileName, path.fileName));

    /// <summary>Combines this path with the given path segments.</summary>
    public FilePath Combine(FilePath path1, FilePath path2) => new FilePath(Path.Combine(fileName, path1.fileName, path2.fileName));

    /// <summary>Combines this path with the given path segments.</summary>
    public FilePath Combine(params string[] paths) => new FilePath(Path.Combine(fileName, Path.Combine(paths)));

    /// <summary>Combines this path with the given path segment.</summary>
    public FilePath Combine(string path) => new FilePath(Path.Combine(fileName, path));

    /// <summary>Combines this path with the given path segments.</summary>
    public FilePath Combine(string path1, string path2) => new FilePath(Path.Combine(fileName, path1, path2));

    /// <summary>Deletes the file or directory (recursively) on a background thread.</summary>
    public Task DeleteAsync() => Task.Run((Action) Delete);

    /// <summary>Deletes the file or directory (recursively), clearing read-only attributes first.</summary>
    public void Delete()
    {
        // Ensure that this file/directory and all children are writable
        MakeWritable(true);

        // Also ensure the directory containing this file/directory is writable,
        // otherwise we will not be able to delete it
        ParentDirectory.MakeWritable(false);

        if (Directory.Exists(this))
            Directory.Delete(this, true);
        else if (File.Exists(this))
            File.Delete(this);
    }

    /// <summary>Clears the read-only attribute of the file or directory.</summary>
    public void MakeWritable() => MakeWritable(false);

    /// <summary>Clears the read-only attribute of the file or directory, optionally recursing into children.</summary>
    public void MakeWritable(bool recurse)
    {
        if (Directory.Exists(this))
        {
            try
            {
                var info = new DirectoryInfo(this);
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
            catch
            {
            }

            if (recurse)
            {
                foreach (var sub in Directory.GetFileSystemEntries(this))
                    ((FilePath) sub).MakeWritable(recurse);
            }
        }
        else if (File.Exists(this))
        {
            try
            {
                var info = new FileInfo(this);
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Builds a path by combining all provided path sections
    /// </summary>
    public static FilePath Build(params string[] paths) => Empty.Combine(paths);

    /// <summary>Builds a path from a single section.</summary>
    public static FilePath Build(string path) => Empty.Combine(path);

    /// <summary>Builds a path from two sections.</summary>
    public static FilePath Build(string path1, string path2) => Empty.Combine(path1, path2);

    /// <summary>Returns the deepest common parent directory of the given paths.</summary>
    public static FilePath GetCommonRootPath(IEnumerable<FilePath> paths)
    {
        FilePath root = Null;
        foreach (FilePath p in paths)
        {
            if (root.IsNull)
                root = p;
            else if (root == p)
                continue;
            else if (root.IsChildPathOf(p))
                root = p;
            else
            {
                while (!root.IsNullOrEmpty && !p.IsChildPathOf(root))
                    root = root.ParentDirectory;
            }
        }
        return root;
    }

    /// <summary>Returns this path as an absolute path, resolving it against the given base path when relative.</summary>
    public FilePath ToAbsolute(FilePath basePath)
    {
        if (IsAbsolute)
            return FullPath;
        return Combine(basePath, this).FullPath;
    }

    /// <summary>Returns this path expressed relative to the given base path.</summary>
    public FilePath ToRelative(FilePath basePath)
    {
        //was previously FileService.AbsoluteToRelativePath(); .NET now provides the equivalent
        return Path.GetRelativePath(basePath.fileName, fileName);
    }

    /// <summary>Converts a string to a <see cref="FilePath"/>.</summary>
    public static implicit operator FilePath(string name) => new FilePath(name);

    /// <summary>Converts a <see cref="FilePath"/> to its underlying string.</summary>
    public static implicit operator string(FilePath filePath) => filePath.fileName;

    /// <summary>Compares two paths using the host file-system case sensitivity.</summary>
    public static bool operator ==(FilePath name1, FilePath name2) => PathComparer.Equals(name1.fileName, name2.fileName);

    /// <summary>Compares two paths using the host file-system case sensitivity.</summary>
    public static bool operator !=(FilePath name1, FilePath name2) => !(name1 == name2);

    /// <inheritdoc/>
    public override bool Equals(object obj)
    {
        if (obj is not FilePath fn)
            return false;
        return this == fn;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (fileName == null)
            return 0;
        return PathComparer.GetHashCode(fileName);
    }

    /// <inheritdoc/>
    public override string ToString() => fileName;

    /// <inheritdoc/>
    public int CompareTo(FilePath filePath) => PathComparer.Compare(fileName, filePath.fileName);

    int IComparable.CompareTo(object obj)
    {
        if (obj is not FilePath other)
            return -1;
        return CompareTo(other);
    }

    /// <inheritdoc/>
    public bool Equals(FilePath other) => this == other;
}

/// <summary>Helper extension methods for working with collections of <see cref="FilePath"/>.</summary>
public static class FilePathUtil
{
    /// <summary>Converts an array of paths to an array of strings.</summary>
    public static string[] ToStringArray(this FilePath[] paths)
    {
        var array = new string[paths.Length];
        for (int n = 0; n < paths.Length; n++)
            array[n] = paths[n].ToString();
        return array;
    }

    /// <summary>Converts an array of strings to an array of paths.</summary>
    public static FilePath[] ToFilePathArray(this string[] paths)
    {
        var array = new FilePath[paths.Length];
        for (int n = 0; n < paths.Length; n++)
            array[n] = paths[n];
        return array;
    }

    /// <summary>Enumerates the given paths as strings.</summary>
    public static IEnumerable<string> ToPathStrings(this IEnumerable<FilePath> paths)
    {
        foreach (FilePath p in paths)
            yield return p.ToString();
    }
}
