//
// ApplicationTemplate.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (extracts the canonical CodeBrix.Platform application layout from the
//      TemplateApp.zip archive; see the CodeBrix.Platform AGENT-README)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeBrix.Develop.Core.Projects;

namespace CodeBrix.Develop.Core.Templates;

/// <summary>
/// The inputs for generating a new CodeBrix.Platform application.
/// </summary>
public class ApplicationTemplateOptions
{
    /// <summary>The application name (project names, namespaces, and the app root folder derive from it).</summary>
    public string Name { get; set; }

    /// <summary>The folder the application's root folder is created in.</summary>
    public string Location { get; set; }

    /// <summary>The platform heads to generate (at least one).</summary>
    public IReadOnlyList<PlatformHead> Heads { get; set; } = Array.Empty<PlatformHead>();

    /// <summary>The application's default text font (Open Sans unless chosen otherwise).</summary>
    public ApplicationFont Font { get; set; } = ApplicationFont.OpenSans;

    /// <summary>
    /// Extra class-library assembly suffixes (e.g. "Graphics"), each
    /// generated as src/libs/&lt;Name&gt;.&lt;Suffix&gt; with a matching
    /// tests/libs/&lt;Name&gt;.&lt;Suffix&gt;.Tests project.
    /// </summary>
    public IReadOnlyList<string> LibrarySuffixes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Generates a complete new CodeBrix.Platform application by extracting the
/// canonical layout from the embedded <see cref="TemplateArchive"/> and
/// transforming it: the template name becomes the chosen application name,
/// the shared project gets a fresh GUID, unselected platform heads are
/// dropped, and (when chosen) the default font is switched. Optional extra
/// libraries under src/libs with their test projects are generated alongside.
/// Whatever the archive contains is what a new application gets — updating the
/// archive updates the output with no change here.
/// <para>
/// Every package reference generated here carries a working version, taken
/// from the archive, derived from the archive, or hard-coded below; nothing
/// is looked up while generating. Bringing those versions up to date is
/// <see cref="ApplicationPackageVersionUpdater"/>'s job, and runs afterward
/// against the finished solution.
/// </para>
/// </summary>
public static class ApplicationTemplate
{
    // Identifier-style, dot-separated segments: valid as a .slnx/.csproj
    // file name, a folder name, and a C# root namespace.
    static readonly Regex namePattern = new Regex(
        @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

    // Name segments that shadow top-level SDK namespaces when used as a
    // project name / root namespace (see the AGENT-README naming rule).
    static readonly string[] reservedSegments = { "Windows", "System" };

    /// <summary>The packages referenced by each generated .Tests project.</summary>
    public static readonly IReadOnlyList<string> TestPackageIds = new[]
    {
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.NET.Test.Sdk",
        "SilverAssertions.ApacheLicenseForever",
        "xunit.runner.visualstudio",
        "xunit.v3",
    };

    /// <summary>
    /// The package whose version in the template supplies the generated test
    /// projects' Microsoft.Extensions.* versions.
    /// </summary>
    public const string HostingPackageId = "Microsoft.Extensions.Hosting";

    // The versions written for the test-project packages the template cannot
    // supply. They are a snapshot, deliberately: the version updater moves
    // them to the latest release on every successful run, and they only get
    // used as-is when nuget.org cannot be reached. Adding a package to
    // TestPackageIds means adding its version here.
    static readonly IReadOnlyDictionary<string, string> testPackageVersions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.NET.Test.Sdk"] = "18.8.1",
            ["SilverAssertions.ApacheLicenseForever"] = "1.0.164.180",
            ["xunit.runner.visualstudio"] = "3.1.5",
            ["xunit.v3"] = "3.2.2",
        };

    /// <summary>
    /// Validates an application name (or a library assembly suffix):
    /// identifier-style dot-separated segments, safe as a file and folder
    /// name, with no segment shadowing a top-level SDK namespace. Returns
    /// null when valid, otherwise the reason it is not.
    /// </summary>
    public static string GetNameError(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "A name is required.";
        if (!namePattern.IsMatch(name))
            return "Use letters, digits, and underscores in dot-separated segments, each starting with a letter (this becomes the .slnx/.csproj file name and the root namespace).";
        foreach (var segment in name.Split('.'))
        {
            if (reservedSegments.Contains(segment, StringComparer.Ordinal))
                return $"The segment \"{segment}\" would shadow the SDK's global {segment}.* namespaces; choose a different name.";
        }
        return null;
    }

    /// <summary>
    /// Validates a library assembly suffix: the same rules as an application
    /// name, plus it must not collide with the always-generated Core/UI
    /// projects or a platform head. Returns null when valid.
    /// </summary>
    public static string GetLibrarySuffixError(string suffix)
    {
        if (GetNameError(suffix) is string error)
            return error;
        if (string.Equals(suffix, "Core", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, "UI", StringComparison.OrdinalIgnoreCase))
            return $"\"{suffix}\" collides with the always-generated .{suffix} project.";
        foreach (var head in PlatformHeadInfo.All)
        {
            if (string.Equals(suffix, head.ProjectSuffix, StringComparison.OrdinalIgnoreCase))
                return $"\"{suffix}\" collides with the {head.DisplayName} head project.";
        }
        return null;
    }

    /// <summary>
    /// Generates the application below &lt;Location&gt;/&lt;Name&gt;/ and
    /// returns the path of the created .slnx file. The target folder must
    /// not already exist (an existing folder means the name is taken).
    /// </summary>
    public static FilePath Generate(ApplicationTemplateOptions options)
    {
        var name = options.Name;
        if (GetNameError(name) is string nameError)
            throw new ArgumentException(nameError, nameof(options));
        foreach (var suffix in options.LibrarySuffixes)
        {
            if (GetLibrarySuffixError(suffix) is string suffixError)
                throw new ArgumentException($"Library \"{suffix}\": {suffixError}", nameof(options));
        }
        if (options.LibrarySuffixes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.LibrarySuffixes.Count)
            throw new ArgumentException("Library suffixes must be unique.", nameof(options));
        if (options.Heads.Count == 0)
            throw new ArgumentException("At least one platform head is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Location))
            throw new ArgumentException("A location folder is required.", nameof(options));

        var root = Path.Combine(Path.GetFullPath(options.Location), name);
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException($"A folder named \"{name}\" already exists in {options.Location}.");

        var libraries = options.LibrarySuffixes.Select(suffix => $"{name}.{suffix}").ToList();

        // Everything the generated files need is validated before a single
        // file is written, so a template that cannot supply it fails with
        // nothing left on disk.
        var archiveBytes = TemplateArchive.GetActiveArchiveBytes();
        var hostingVersion = ReadHostingVersion(archiveBytes);
        var font = ApplicationFontInfo.Get(options.Font);
        if (options.Font != ApplicationFontInfo.DefaultFont && font.FallbackVersion == null)
            throw new InvalidOperationException(
                $"The {font.DisplayName} font has no package version; every font other than the default must supply one.");

        ExtractApplication(options, root, name, archiveBytes);
        GenerateLibraries(options, root, name, libraries, hostingVersion);

        var slnxPath = Path.Combine(root, $"{name}.slnx");
        LoggingService.LogInfo($"New CodeBrix.Platform application generated: {slnxPath}");
        return new FilePath(slnxPath);
    }

    // The Microsoft.Extensions.Hosting version the template's .Core project
    // carries — the version the generated test projects use for it and for
    // Microsoft.Extensions.DependencyInjection. A template that no longer
    // supplies it cannot produce a correct application.
    static string ReadHostingVersion(byte[] archiveBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".Core.csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            var version = PackageReferenceReader.ReadVersion(reader.ReadToEnd(), HostingPackageId);
            if (!string.IsNullOrWhiteSpace(version))
                return version;
            break;
        }
        throw new InvalidOperationException(
            $"The application template no longer supplies a {HostingPackageId} version in its .Core project; the generated test projects cannot be versioned.");
    }

    // Extracts the archive into <root>, applying the name/GUID/head/font
    // transforms. Directory entries are ignored (folders are created from the
    // files that land in them).
    static void ExtractApplication(ApplicationTemplateOptions options, string root, string name, byte[] archiveBytes)
    {
        var selectedSuffixes = new HashSet<string>(
            options.Heads.Select(head => PlatformHeadInfo.Get(head).ProjectSuffix), StringComparer.OrdinalIgnoreCase);
        var allHeadSuffixes = new HashSet<string>(
            PlatformHeadInfo.All.Select(head => head.ProjectSuffix), StringComparer.OrdinalIgnoreCase);

        var newGuid = Guid.NewGuid().ToString();
        var openSans = ApplicationFontInfo.Get(ApplicationFontInfo.DefaultFont);
        var font = ApplicationFontInfo.Get(options.Font);
        var switchFont = options.Font != ApplicationFontInfo.DefaultFont;

        var token = TemplateArchive.TemplateToken;
        var rootPrefix = token + "/";

        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var full = entry.FullName.Replace('\\', '/');
            if (full.EndsWith("/", StringComparison.Ordinal))
                continue; // directory entry

            var relative = full.StartsWith(rootPrefix, StringComparison.Ordinal)
                ? full.Substring(rootPrefix.Length)
                : full;
            if (relative.Length == 0)
                continue;

            if (IsUnderUnselectedHead(relative, token, selectedSuffixes, allHeadSuffixes))
                continue;

            var outRelative = relative.Replace(token, name);
            var outPath = Path.Combine(root, outRelative.Replace('/', Path.DirectorySeparatorChar));

            byte[] bytes;
            using (var entryStream = entry.Open())
            using (var memory = new MemoryStream())
            {
                entryStream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            // Latin1 round-trips every byte 1:1 (including any BOM and any
            // multi-byte UTF-8 sequence), and every token we replace is ASCII,
            // so the transformed file is byte-identical except where changed.
            var text = Encoding.Latin1.GetString(bytes);
            text = text.Replace(token, name);
            if (switchFont)
            {
                text = text
                    .Replace(openSans.PackageId, font.PackageId)
                    .Replace(openSans.FontFamilyValue, font.FontFamilyValue)
                    .Replace(openSans.ResourceKey, font.ResourceKey)
                    .Replace(openSans.DisplayName, font.DisplayName);
                // The template carries the DEFAULT font's version, which says
                // nothing about the chosen font: swap the version too.
                if (outRelative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    text = PackageReferenceRewriter.UpdateVersion(text, font.PackageId, font.FallbackVersion, out _);
            }
            if (IsSharedProjectFile(outRelative))
                text = ReplaceSharedProjectGuid(text, newGuid);
            if (IsSolutionFile(outRelative))
                text = PruneUnselectedHeadProjects(text, name, selectedSuffixes, allHeadSuffixes);

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, Encoding.Latin1.GetBytes(text));
        }
    }

    // True for an archive entry under an unselected head project, e.g.
    // "src/TemplateApp.LinuxWayland/…" when Wayland is unchecked. The
    // always-generated .Core and .UI segments are never head suffixes.
    static bool IsUnderUnselectedHead(string relative, string token,
        HashSet<string> selectedSuffixes, HashSet<string> allHeadSuffixes)
    {
        var prefix = "src/" + token + ".";
        if (!relative.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var rest = relative.Substring(prefix.Length);
        var slash = rest.IndexOf('/');
        var segment = slash < 0 ? rest : rest.Substring(0, slash);
        return allHeadSuffixes.Contains(segment) && !selectedSuffixes.Contains(segment);
    }

    static bool IsSharedProjectFile(string relative) =>
        relative.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase);

    static bool IsSolutionFile(string relative) =>
        relative.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    // Replaces the shared project's baked GUID (in <SharedGUID> in .projitems
    // and <ProjectGuid> in .shproj) with a freshly generated one, so every
    // generated application owns a unique — and internally matching — GUID.
    static string ReplaceSharedProjectGuid(string text, string newGuid)
    {
        text = Regex.Replace(text, @"(<SharedGUID>)[^<]*(</SharedGUID>)", "${1}" + newGuid + "${2}");
        text = Regex.Replace(text, @"(<ProjectGuid>)[^<]*(</ProjectGuid>)", "${1}" + newGuid + "${2}");
        return text;
    }

    // Drops the <Project> lines for unselected heads from the .slnx (the
    // application-name substitution has already run, so paths read
    // "src/<Name>.<Suffix>/…"). Line endings are preserved.
    static string PruneUnselectedHeadProjects(string text, string name,
        HashSet<string> selectedSuffixes, HashSet<string> allHeadSuffixes)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var dropped = allHeadSuffixes.Any(suffix =>
                !selectedSuffixes.Contains(suffix)
                && line.Contains($"src/{name}.{suffix}/", StringComparison.Ordinal));
            if (!dropped)
                kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    // Generates the optional extra libraries and their test projects, then
    // wires them into .Core (a ProjectReference each) and the .slnx (the
    // Libraries and Tests solution folders).
    static void GenerateLibraries(ApplicationTemplateOptions options, string root, string name,
        IReadOnlyList<string> libraries, string hostingVersion)
    {
        if (libraries.Count == 0)
            return;

        foreach (var library in libraries)
        {
            var libraryDirectory = Path.Combine(root, "src", "libs", library);
            Write(libraryDirectory, $"{library}.csproj", LibraryCsproj());
            Write(libraryDirectory, "InternalsVisibleTo.cs", InternalsVisibleToCs(library));

            var testsDirectory = Path.Combine(root, "tests", "libs", $"{library}.Tests");
            Write(testsDirectory, $"{library}.Tests.csproj", TestsCsproj(library, hostingVersion));
            Write(testsDirectory, "BasicTests.cs", BasicTestsCs(library));
        }

        InjectLibraryReferencesIntoCore(root, name, libraries);
        InjectLibraryFoldersIntoSolution(root, name, libraries);
    }

    static void InjectLibraryReferencesIntoCore(string root, string name, IReadOnlyList<string> libraries)
    {
        var corePath = Path.Combine(root, "src", $"{name}.Core", $"{name}.Core.csproj");
        var text = File.ReadAllText(corePath);
        var builder = new StringBuilder();
        builder.Append("\n  <ItemGroup>\n");
        foreach (var library in libraries)
            builder.Append($"    <ProjectReference Include=\"..\\libs\\{library}\\{library}.csproj\" />\n");
        builder.Append("  </ItemGroup>\n");
        File.WriteAllText(corePath, InsertBefore(text, "</Project>", builder.ToString()));
    }

    static void InjectLibraryFoldersIntoSolution(string root, string name, IReadOnlyList<string> libraries)
    {
        var slnxPath = Path.Combine(root, $"{name}.slnx");
        var text = File.ReadAllText(slnxPath);
        var builder = new StringBuilder();
        builder.Append("  <Folder Name=\"/Libraries/\">\n");
        foreach (var library in libraries)
            builder.Append($"    <Project Path=\"src/libs/{library}/{library}.csproj\" />\n");
        builder.Append("  </Folder>\n");
        builder.Append("  <Folder Name=\"/Tests/\">\n");
        foreach (var library in libraries)
            builder.Append($"    <Project Path=\"tests/libs/{library}.Tests/{library}.Tests.csproj\" />\n");
        builder.Append("  </Folder>\n");
        File.WriteAllText(slnxPath, InsertBefore(text, "</Solution>", builder.ToString()));
    }

    static string InsertBefore(string text, string marker, string insertion)
    {
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? text + insertion : text.Insert(index, insertion);
    }

    static void Write(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    // The version a generated test project references a package at: the
    // template's own Microsoft.Extensions.Hosting version for the
    // Microsoft.Extensions.* pair, and the hard-coded snapshot for the rest.
    // Never null — an unversioned PackageReference resolves to the LOWEST
    // published version, not the latest.
    static string TestPackageVersion(string packageId, string hostingVersion)
    {
        if (string.Equals(packageId, HostingPackageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(packageId, "Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase))
            return hostingVersion;
        if (testPackageVersions.TryGetValue(packageId, out var version))
            return version;
        throw new InvalidOperationException(
            $"No version is defined for the generated test projects' {packageId} package reference.");
    }

    static string LibraryCsproj() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>

        """;

    static string InternalsVisibleToCs(string libraryName) => $$"""
        using System.Runtime.CompilerServices;

        [assembly: InternalsVisibleTo("{{libraryName}}.Tests")]

        """;

    static string TestsCsproj(string libraryName, string hostingVersion)
    {
        var packageLines = new StringBuilder();
        foreach (var id in TestPackageIds)
        {
            var version = TestPackageVersion(id, hostingVersion);
            if (id == "xunit.runner.visualstudio")
            {
                packageLines.Append($"    <PackageReference Include=\"{id}\" Version=\"{version}\">\n");
                packageLines.Append("      <PrivateAssets>all</PrivateAssets>\n");
                packageLines.Append("      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>\n");
                packageLines.Append("    </PackageReference>\n");
            }
            else
            {
                packageLines.Append($"    <PackageReference Include=\"{id}\" Version=\"{version}\" />\n");
            }
        }

        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <!-- xUnit.net v3 test projects are self-executing binaries and
                     must build as Exe; run via Microsoft.Testing.Platform,
                     matching the CodeBrix family test convention. -->
                <OutputType>Exe</OutputType>
                <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
                <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="..\..\..\src\libs\{{libraryName}}\{{libraryName}}.csproj" />
              </ItemGroup>

              <ItemGroup>
            {{packageLines.ToString().TrimEnd('\n')}}
              </ItemGroup>

            </Project>

            """;
    }

    static string BasicTestsCs(string libraryName) => $$"""
        using SilverAssertions;
        using Xunit;

        namespace {{libraryName}}.Tests;

        public class BasicTests
        {
            [Fact]
            public void can_run_tests()
            {
                //Arrange
                var isRunning = true;

                //Assert
                isRunning.Should().Be(true);
            }
        }

        """;
}
