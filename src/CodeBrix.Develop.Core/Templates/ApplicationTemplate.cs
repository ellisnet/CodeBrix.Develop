//
// ApplicationTemplate.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (generates the canonical CodeBrix.Platform application layout
//      documented in the CodeBrix.Platform AGENT-README)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Resolved package versions (id → version); a missing or null entry
    /// emits that PackageReference without a Version attribute.
    /// </summary>
    public IReadOnlyDictionary<string, string> PackageVersions { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Generates a complete new CodeBrix.Platform application: the .Core class
/// library, the .UI shared project (App.xaml + Views), one thin head
/// project per selected platform, optional extra libraries under src/libs
/// with their test projects under tests/libs, and the cross-platform .slnx
/// tying it all together. Follows the canonical layout from the
/// CodeBrix.Platform AGENT-README.
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

    /// <summary>
    /// The framework packages referenced by the generated .Core project
    /// (the chosen font's package is added alongside these).
    /// </summary>
    public static readonly IReadOnlyList<string> CorePackageIds = new[]
    {
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging.Console",
        "CodeBrix.Platform.ApacheLicenseForever",
    };

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
    /// The package ids the generated projects will reference, so their
    /// latest versions can be resolved before <see cref="Generate"/>.
    /// </summary>
    public static IReadOnlyList<string> GetRequiredPackageIds(ApplicationTemplateOptions options)
    {
        var ids = new List<string>(CorePackageIds)
        {
            ApplicationFontInfo.Get(options.Font).PackageId,
        };
        foreach (var head in options.Heads)
            ids.Add(PlatformHeadInfo.Get(head).PackageId);
        if (options.LibrarySuffixes.Count > 0)
            ids.AddRange(TestPackageIds);
        return ids;
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

        var heads = options.Heads.Select(PlatformHeadInfo.Get).ToList();
        var libraries = options.LibrarySuffixes.Select(suffix => $"{name}.{suffix}").ToList();
        var font = ApplicationFontInfo.Get(options.Font);

        // src/<Name>.Core
        var coreDirectory = Path.Combine(root, "src", $"{name}.Core");
        Write(coreDirectory, $"{name}.Core.csproj", CoreCsproj(name, options, font));
        Write(Path.Combine(coreDirectory, "ViewModels"), "MainViewModel.cs", MainViewModelCs(name));

        // src/<Name>.UI (shared project)
        var uiDirectory = Path.Combine(root, "src", $"{name}.UI");
        var sharedGuid = Guid.NewGuid().ToString();
        Write(uiDirectory, $"{name}.UI.projitems", UiProjitems(name, sharedGuid));
        Write(uiDirectory, $"{name}.UI.shproj", UiShproj(name, sharedGuid));
        Write(uiDirectory, "App.xaml", AppXaml(name, font));
        Write(uiDirectory, "App.xaml.cs", AppXamlCs(name, font));
        Write(Path.Combine(uiDirectory, "Views"), "MainPage.xaml", MainPageXaml(name, font));
        Write(Path.Combine(uiDirectory, "Views"), "MainPage.xaml.cs", MainPageXamlCs(name));

        // src/<Name>.<Head> per selected platform
        foreach (var head in heads)
        {
            var headDirectory = Path.Combine(root, "src", $"{name}.{head.ProjectSuffix}");
            Write(headDirectory, $"{name}.{head.ProjectSuffix}.csproj", HeadCsproj(name, head, options));
            Write(headDirectory, "Program.cs", HeadProgramCs(name, head));
        }

        // src/libs/<Name>.<Suffix> + tests/libs/<Name>.<Suffix>.Tests
        foreach (var library in libraries)
        {
            var libraryDirectory = Path.Combine(root, "src", "libs", library);
            Write(libraryDirectory, $"{library}.csproj", LibraryCsproj());
            Write(libraryDirectory, "InternalsVisibleTo.cs", InternalsVisibleToCs(library));

            var testsDirectory = Path.Combine(root, "tests", "libs", $"{library}.Tests");
            Write(testsDirectory, $"{library}.Tests.csproj", TestsCsproj(library, options));
            Write(testsDirectory, "BasicTests.cs", BasicTestsCs(library));
        }

        var slnxPath = Path.Combine(root, $"{name}.slnx");
        File.WriteAllText(slnxPath, Slnx(name, heads, libraries));
        LoggingService.LogInfo($"New CodeBrix.Platform application generated: {slnxPath}");
        return new FilePath(slnxPath);
    }

    static void Write(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    // <PackageReference Include="..." Version="..." /> — or unversioned when
    // no version could be resolved (the first restore then picks one).
    static string PackageReference(string packageId, ApplicationTemplateOptions options, string indent)
    {
        options.PackageVersions.TryGetValue(packageId, out var version);
        return version == null
            ? $"{indent}<PackageReference Include=\"{packageId}\" />"
            : $"{indent}<PackageReference Include=\"{packageId}\" Version=\"{version}\" />";
    }

    static string CoreCsproj(string name, ApplicationTemplateOptions options, ApplicationFontInfo font)
    {
        var packages = string.Join('\n', CorePackageIds.Concat(new[] { font.PackageId })
            .Select(id => PackageReference(id, options, "    ")));
        var builder = new StringBuilder();
        builder.Append($$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>

                <!-- Match the namespace used by the app code -->
                <RootNamespace>{{name}}</RootNamespace>

                <!-- CodeBrix.Platform needs these for internal conditional compilation -->
                <DefineConstants>$(DefineConstants);HAS_CODEBRIX;HAS_CODEBRIX_WINUI</DefineConstants>
              </PropertyGroup>

              <ItemGroup>
            {{packages}}
              </ItemGroup>

            """);
        if (options.LibrarySuffixes.Count > 0)
        {
            builder.Append("\n  <ItemGroup>\n");
            foreach (var suffix in options.LibrarySuffixes)
                builder.Append($"    <ProjectReference Include=\"..\\libs\\{name}.{suffix}\\{name}.{suffix}.csproj\" />\n");
            builder.Append("  </ItemGroup>\n");
        }
        builder.Append("</Project>\n");
        return builder.ToString();
    }

    static string MainViewModelCs(string name) => $$"""
        namespace {{name}}.ViewModels;

        public class MainViewModel
        {
            public string Greeting => "Hello from {{name}}!";
        }

        """;

    static string UiProjitems(string name, string sharedGuid) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <MSBuildAllProjects Condition="'$(MSBuildVersion)' == '' Or '$(MSBuildVersion)' &lt; '16.0'">$(MSBuildAllProjects);$(MSBuildThisFileFullPath)</MSBuildAllProjects>
            <HasSharedItems>true</HasSharedItems>
            <SharedGUID>{{sharedGuid}}</SharedGUID>
          </PropertyGroup>
          <PropertyGroup Label="Configuration">
            <Import_RootNamespace>{{name}}.UI</Import_RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <Page Include="$(MSBuildThisFileDirectory)App.xaml">
              <SubType>Designer</SubType>
              <Generator>MSBuild:Compile</Generator>
            </Page>
            <Page Include="$(MSBuildThisFileDirectory)Views\MainPage.xaml">
              <SubType>Designer</SubType>
              <Generator>MSBuild:Compile</Generator>
            </Page>
          </ItemGroup>
          <ItemGroup>
            <Compile Include="$(MSBuildThisFileDirectory)App.xaml.cs">
              <DependentUpon>App.xaml</DependentUpon>
            </Compile>
            <Compile Include="$(MSBuildThisFileDirectory)Views\MainPage.xaml.cs">
              <DependentUpon>MainPage.xaml</DependentUpon>
            </Compile>
          </ItemGroup>
        </Project>

        """;

    static string UiShproj(string name, string sharedGuid) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup Label="Globals">
            <ProjectGuid>{{sharedGuid}}</ProjectGuid>
            <MinimumVisualStudioVersion>14.0</MinimumVisualStudioVersion>
          </PropertyGroup>
          <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
          <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.Common.Default.props" />
          <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.Common.props" />
          <PropertyGroup />
          <Import Project="{{name}}.UI.projitems" Label="Shared" />
          <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.CSharp.targets" />
        </Project>

        """;

    static string AppXaml(string name, ApplicationFontInfo font) => $$"""
        <Application x:Class="{{name}}.App"
               xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

          <Application.Resources>
            <ResourceDictionary>
              <ResourceDictionary.MergedDictionaries>
                <!-- Load WinUI resources -->
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
              </ResourceDictionary.MergedDictionaries>
              <!-- {{font.DisplayName}} font - reference the .ttf file directly (the Fonts.xaml
                   merge does not work on Skia targets) -->
              <FontFamily x:Key="{{font.ResourceKey}}">{{font.FontFamilyValue}}</FontFamily>
            </ResourceDictionary>
          </Application.Resources>

        </Application>

        """;

    static string AppXamlCs(string name, ApplicationFontInfo font) => $$"""
        using Microsoft.Extensions.Logging;
        using Microsoft.UI.Xaml;
        using Microsoft.UI.Xaml.Controls;
        using Microsoft.UI.Xaml.Navigation;
        using System;

        namespace {{name}};

        public partial class App : Application
        {
            public App()
            {
                //Set {{font.DisplayName}} as the default font for all text in the application
                global::CodeBrix.Platform.UI.FeatureConfiguration.Font.DefaultTextFontFamily =
                    "{{font.FontFamilyValue}}";

                InitializeComponent();
            }

            protected Window MainWindow { get; private set; }

            protected override void OnLaunched(LaunchActivatedEventArgs args)
            {
                MainWindow = new Window
                {
                    Title = "{{name}}"
                };

                if (MainWindow.Content is not Frame rootFrame)
                {
                    rootFrame = new Frame();
                    MainWindow.Content = rootFrame;
                    rootFrame.NavigationFailed += OnNavigationFailed;
                }

                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(Views.MainPage), args.Arguments);
                }

                MainWindow.Activate();
            }

            void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
            {
                throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
            }

            // Called from each head's Program.Main BEFORE building the host.
            public static void InitializeLogging()
            {
        #if DEBUG
                var factory = LoggerFactory.Create(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                    builder.AddFilter("CodeBrix.Platform", LogLevel.Warning);
                    builder.AddFilter("Windows", LogLevel.Warning);
                    builder.AddFilter("Microsoft", LogLevel.Warning);
                });

                global::CodeBrix.Platform.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

        #if HAS_CODEBRIX
                global::CodeBrix.Platform.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
        #endif
        #endif
            }
        }

        """;

    static string MainPageXaml(string name, ApplicationFontInfo font) => $$"""
        <Page
            x:Class="{{name}}.Views.MainPage"
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            xmlns:vm="using:{{name}}.ViewModels"
            FontFamily="{StaticResource {{font.ResourceKey}}}"
            Background="{ThemeResource ApplicationPageBackgroundThemeBrush}">

            <Page.DataContext>
                <vm:MainViewModel />
            </Page.DataContext>

            <Grid>
                <TextBlock Text="{Binding Greeting}"
                           FontSize="24"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center" />
            </Grid>
        </Page>

        """;

    static string MainPageXamlCs(string name) => $$"""
        using Microsoft.UI.Xaml.Controls;

        namespace {{name}}.Views;

        public sealed partial class MainPage : Page
        {
            public MainPage()
            {
                this.InitializeComponent();
            }
        }

        """;

    static string HeadCsproj(string name, PlatformHeadInfo head, ApplicationTemplateOptions options)
    {
        var packageLine = PackageReference(head.PackageId, options, "    ");
        // EnableWindowsTargeting lets the net10.0-windows WPF head compile
        // inside the cross-platform solution on Linux and macOS build hosts.
        var windowsTargeting = head.IsWpf ? "\n    <EnableWindowsTargeting>true</EnableWindowsTargeting>" : "";
        // No <RootNamespace> on heads: sharing .Core's root namespace would
        // make the XAML source generator emit a GlobalStaticResources type
        // that collides with .Core's copy (CS0436 on every head).
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{{head.TargetFramework}}</TargetFramework>
                <OutputType>Exe</OutputType>{{windowsTargeting}}

                <!-- CodeBrix.Platform needs these for internal conditional compilation -->
                <DefineConstants>$(DefineConstants);HAS_CODEBRIX;HAS_CODEBRIX_WINUI</DefineConstants>
              </PropertyGroup>

              <!-- Tell MSBuild to treat .xaml files as CodeBrix.Platform XAML pages -->
              <ItemGroup>
                <Page Include="**\*.xaml" Exclude="bin\**\*.xaml;obj\**\*.xaml" />
                <None Remove="**\*.xaml" />
              </ItemGroup>

              <!-- Shared UI files (App.xaml + Views) -->
              <Import Project="..\{{name}}.UI\{{name}}.UI.projitems" Label="Shared" />
              <ItemGroup>
                <ProjectReference Include="..\{{name}}.Core\{{name}}.Core.csproj" />
              </ItemGroup>

              <!-- EXACTLY ONE platform head package; all other packages come from {{name}}.Core -->
              <ItemGroup>
            {{packageLine}}
              </ItemGroup>
            </Project>

            """;
    }

    static string HeadProgramCs(string name, PlatformHeadInfo head)
    {
        if (head.IsWpf)
        {
            // The WPF host's default OpenGL renderer conflicts with WPF's own
            // DirectX composition ("airspace") — force software rendering.
            return $$"""
                using CodeBrix.Platform.UI.Hosting;
                using CodeBrix.Platform.UI.Runtime.Skia.Wpf;
                using System;

                namespace {{name}};

                internal class Program
                {
                    [STAThread]
                    public static void Main(string[] args)
                    {
                        App.InitializeLogging();

                        var host = CodeBrixPlatformHostBuilder.Create()
                            .App(() => new App())
                            .{{head.BootstrapCall}}()
                            .Build();

                        if (host is WpfHost wpfHost)
                        {
                            wpfHost.RenderSurfaceType = RenderSurfaceType.Software;
                        }

                        host.Run();
                    }
                }

                """;
        }

        return $$"""
            using CodeBrix.Platform.UI.Hosting;
            using System;

            namespace {{name}};

            internal class Program
            {
                [STAThread]
                public static void Main(string[] args)
                {
                    App.InitializeLogging();

                    var host = CodeBrixPlatformHostBuilder.Create()
                        .App(() => new App())
                        .{{head.BootstrapCall}}()
                        .Build();

                    host.Run();
                }
            }

            """;
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

    static string TestsCsproj(string libraryName, ApplicationTemplateOptions options)
    {
        var packageLines = new StringBuilder();
        foreach (var id in TestPackageIds)
        {
            if (id == "xunit.runner.visualstudio")
            {
                options.PackageVersions.TryGetValue(id, out var runnerVersion);
                var versionAttribute = runnerVersion == null ? "" : $" Version=\"{runnerVersion}\"";
                packageLines.Append($"    <PackageReference Include=\"{id}\"{versionAttribute}>\n");
                packageLines.Append("      <PrivateAssets>all</PrivateAssets>\n");
                packageLines.Append("      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>\n");
                packageLines.Append("    </PackageReference>\n");
            }
            else
            {
                packageLines.Append(PackageReference(id, options, "    "));
                packageLines.Append('\n');
            }
        }

        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
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

    static string Slnx(string name, IReadOnlyList<PlatformHeadInfo> heads, IReadOnlyList<string> libraries)
    {
        var builder = new StringBuilder();
        builder.Append("<Solution>\n");
        builder.Append($"  <!-- {name}.slnx\n");
        builder.Append("       Generated by CodeBrix Develop: a CodeBrix.Platform application with\n");
        builder.Append("       everything that builds with the plain .NET SDK on Linux, macOS and Windows. -->\n");
        builder.Append($"  <Project Path=\"src/{name}.UI/{name}.UI.shproj\" />\n");
        builder.Append($"  <Project Path=\"src/{name}.Core/{name}.Core.csproj\" />\n");
        foreach (var head in heads)
            builder.Append($"  <Project Path=\"src/{name}.{head.ProjectSuffix}/{name}.{head.ProjectSuffix}.csproj\" />\n");
        if (libraries.Count > 0)
        {
            builder.Append("  <Folder Name=\"/Libraries/\">\n");
            foreach (var library in libraries)
                builder.Append($"    <Project Path=\"src/libs/{library}/{library}.csproj\" />\n");
            builder.Append("  </Folder>\n");
            builder.Append("  <Folder Name=\"/Tests/\">\n");
            foreach (var library in libraries)
                builder.Append($"    <Project Path=\"tests/libs/{library}.Tests/{library}.Tests.csproj\" />\n");
            builder.Append("  </Folder>\n");
        }
        builder.Append("</Solution>\n");
        return builder.ToString();
    }
}
