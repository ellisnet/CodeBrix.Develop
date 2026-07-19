using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.Develop.Core.Projects;
using CodeBrix.Develop.Core.Templates;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Develop.Core.Tests;

public class ApplicationTemplateTests : IDisposable
{
    readonly string tempDirectory;

    public ApplicationTemplateTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
    }

    static Dictionary<string, string> FixedVersions(params string[] ids) =>
        ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, _ => "1.2.3", StringComparer.OrdinalIgnoreCase);

    ApplicationTemplateOptions MakeOptions(string name = "MyNewApp", ApplicationFont font = ApplicationFont.OpenSans)
    {
        var options = new ApplicationTemplateOptions
        {
            Name = name,
            Location = tempDirectory,
            Heads = new[] { PlatformHead.MacOS, PlatformHead.LinuxX11, PlatformHead.WinWpfSkia },
            Font = font,
            LibrarySuffixes = new[] { "Graphics", "DatabaseAccess" },
        };
        // Only the generated test projects take up-front-resolved versions now; the
        // application projects come versioned from the archive.
        options.PackageVersions = FixedVersions(ApplicationTemplate.GetRequiredPackageIds(options).ToArray());
        return options;
    }

    [Fact]
    public void Generate_creates_the_full_layout()
    {
        //Act
        var slnxPath = ApplicationTemplate.Generate(MakeOptions());

        //Assert
        var root = Path.Combine(tempDirectory, "MyNewApp");
        ((string) slnxPath).Should().Be(Path.Combine(root, "MyNewApp.slnx"));
        File.Exists(Path.Combine(root, "src", "MyNewApp.Core", "MyNewApp.Core.csproj")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.Core", "ViewModels", "MainViewModel.cs")).Should().BeTrue();
        // The archive brings the DI helper that the App wires up at startup.
        File.Exists(Path.Combine(root, "src", "MyNewApp.Core", "Helpers", "HostHelper.cs")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.UI", "MyNewApp.UI.shproj")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.UI", "MyNewApp.UI.projitems")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.UI", "App.xaml")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.UI", "Views", "MainPage.xaml")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.MacOS", "MyNewApp.MacOS.csproj")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "MyNewApp.LinuxX11", "Program.cs")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "libs", "MyNewApp.Graphics", "MyNewApp.Graphics.csproj")).Should().BeTrue();
        File.Exists(Path.Combine(root, "src", "libs", "MyNewApp.Graphics", "InternalsVisibleTo.cs")).Should().BeTrue();
        File.Exists(Path.Combine(root, "tests", "libs", "MyNewApp.Graphics.Tests", "BasicTests.cs")).Should().BeTrue();
        File.Exists(Path.Combine(root, "tests", "libs", "MyNewApp.DatabaseAccess.Tests", "MyNewApp.DatabaseAccess.Tests.csproj")).Should().BeTrue();
        // Heads not selected are not generated.
        Directory.Exists(Path.Combine(root, "src", "MyNewApp.LinuxWayland")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "src", "MyNewApp.Win32Skia")).Should().BeFalse();
    }

    [Fact]
    public void Generated_slnx_loads_and_contains_the_expected_projects()
    {
        //Act
        var slnxPath = ApplicationTemplate.Generate(MakeOptions());
        var solution = Solution.Load(slnxPath);

        //Assert — UI + Core + 3 heads + 2 libs + 2 test projects. The test
        // projects are executables too (xUnit v3 self-executing binaries).
        solution.Projects.Count.Should().Be(9);
        solution.Projects.Count(p => p.IsSharedProject).Should().Be(1);
        solution.Projects.Count(p => p.IsExecutable).Should().Be(5);
        solution.Projects.Count(p => p.IsTestProject).Should().Be(2);
        solution.Projects.Where(p => p.IsTestProject).All(p => p.UsesMicrosoftTestingPlatformRunner).Should().BeTrue();
        // The archive lists the heads in a fixed order (X11 first), so it is the
        // first executable non-test project.
        solution.StartupProject.Name.Should().Be("MyNewApp.LinuxX11");

        // Platform projects at the root; libraries and tests grouped in
        // TitleCase solution folders.
        solution.Projects.First(p => p.Name == "MyNewApp.Core").SolutionFolder.Should().Be("");
        solution.Projects.First(p => p.Name == "MyNewApp.Graphics").SolutionFolder.Should().Be("Libraries");
        solution.Projects.First(p => p.Name == "MyNewApp.Graphics.Tests").SolutionFolder.Should().Be("Tests");
        solution.SolutionFolderNames.Should().Equal(new[] { "Libraries", "Tests" });
    }

    [Fact]
    public void Generated_files_carry_the_expected_conventions()
    {
        //Act
        var slnxPath = ApplicationTemplate.Generate(MakeOptions());
        var root = Path.Combine(tempDirectory, "MyNewApp");

        //Assert
        var coreCsproj = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.Core", "MyNewApp.Core.csproj"));
        // The platform package comes versioned from the archive (baked value; not asserted).
        coreCsproj.Should().Contain("Include=\"CodeBrix.Platform.ApacheLicenseForever\" Version=\"");
        coreCsproj.Should().Contain("<ProjectReference Include=\"..\\libs\\MyNewApp.Graphics\\MyNewApp.Graphics.csproj\" />");
        // The archive no longer carries the internal-compilation constants.
        coreCsproj.Should().NotContain("HAS_CODEBRIX");

        var wpfCsproj = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.WinWpfSkia", "MyNewApp.WinWpfSkia.csproj"));
        wpfCsproj.Should().Contain("<TargetFramework>net10.0-windows</TargetFramework>");
        wpfCsproj.Should().NotContain("<UseWPF>");

        var wpfProgram = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.WinWpfSkia", "Program.cs"));
        wpfProgram.Should().Contain("RenderSurfaceType.Software");

        var x11Program = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.LinuxX11", "Program.cs"));
        x11Program.Should().Contain(".UseLinuxX11()");
        x11Program.Should().Contain(".UseDirectSkiaCanvasMode()");

        // The MainViewModel is a SimpleViewModel and App.xaml.cs wires up DI.
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.Core", "ViewModels", "MainViewModel.cs"))
            .Should().Contain(": SimpleViewModel");
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "App.xaml.cs"))
            .Should().Contain("SimpleServiceResolver.CreateInstance");

        var internalsVisibleTo = File.ReadAllText(Path.Combine(root, "src", "libs", "MyNewApp.Graphics", "InternalsVisibleTo.cs"));
        internalsVisibleTo.Should().Contain("[assembly: InternalsVisibleTo(\"MyNewApp.Graphics.Tests\")]");

        var basicTests = File.ReadAllText(Path.Combine(root, "tests", "libs", "MyNewApp.Graphics.Tests", "BasicTests.cs"));
        basicTests.Should().Contain("public void can_run_tests()");
        basicTests.Should().Contain("isRunning.Should().Be(true);");

        var slnx = File.ReadAllText(slnxPath);
        slnx.Should().Contain("<Folder Name=\"/Libraries/\">");
        slnx.Should().Contain("<Folder Name=\"/Tests/\">");

        // The .UI pair shares one fresh GUID — never the archive's baked one.
        var projitems = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "MyNewApp.UI.projitems"));
        var shproj = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "MyNewApp.UI.shproj"));
        var guid = System.Text.RegularExpressions.Regex.Match(projitems, "<SharedGUID>([^<]+)</SharedGUID>").Groups[1].Value;
        guid.Should().NotBe("");
        guid.Should().NotBe("cb2ad0d5-d6d9-4347-8547-c53b80ed5e7d");
        shproj.Should().Contain($"<ProjectGuid>{guid}</ProjectGuid>");
    }

    [Fact]
    public void Default_font_is_open_sans()
    {
        //Act
        ApplicationTemplate.Generate(MakeOptions());
        var root = Path.Combine(tempDirectory, "MyNewApp");

        //Assert
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.Core", "MyNewApp.Core.csproj"))
            .Should().Contain("CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever");
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "App.xaml"))
            .Should().Contain("x:Key=\"OpenSansFont\"");
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "Views", "MainPage.xaml"))
            .Should().Contain("{StaticResource OpenSansFont}");
    }

    [Fact]
    public void Roboto_font_replaces_every_open_sans_reference()
    {
        //Act
        ApplicationTemplate.Generate(MakeOptions(font: ApplicationFont.Roboto));
        var root = Path.Combine(tempDirectory, "MyNewApp");

        //Assert — the Roboto package and resources are referenced...
        var coreCsproj = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.Core", "MyNewApp.Core.csproj"));
        coreCsproj.Should().Contain("Include=\"CodeBrix.Platform.Fonts.Roboto.OflLicenseForever\" Version=\"");
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "App.xaml"));
        appXaml.Should().Contain("x:Key=\"RobotoFont\"");
        appXaml.Should().Contain("ms-appx:///CodeBrix.Platform.Fonts.Roboto/Fonts/Roboto.ttf#Roboto");
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "Views", "MainPage.xaml"))
            .Should().Contain("{StaticResource RobotoFont}");
        File.ReadAllText(Path.Combine(root, "src", "MyNewApp.UI", "App.xaml.cs"))
            .Should().Contain("ms-appx:///CodeBrix.Platform.Fonts.Roboto/Fonts/Roboto.ttf#Roboto");

        //...and NO generated file mentions OpenSans anywhere.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.ReadAllText(file).Should().NotContain("OpenSans");
    }

    [Fact]
    public void Generate_rejects_an_existing_folder()
    {
        //Arrange
        Directory.CreateDirectory(Path.Combine(tempDirectory, "MyNewApp"));

        //Act
        var act = () => ApplicationTemplate.Generate(MakeOptions());

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Generate_requires_at_least_one_head()
    {
        //Arrange
        var options = MakeOptions();
        options.Heads = Array.Empty<PlatformHead>();

        //Act
        var act = () => ApplicationTemplate.Generate(options);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unresolved_test_package_versions_are_emitted_unversioned()
    {
        //Arrange — no resolved versions available.
        var options = MakeOptions();
        options.PackageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        //Act
        ApplicationTemplate.Generate(options);

        //Assert — the generated test project emits its references unversioned
        // (the first restore then resolves them).
        var testsCsproj = File.ReadAllText(Path.Combine(tempDirectory, "MyNewApp",
            "tests", "libs", "MyNewApp.Graphics.Tests", "MyNewApp.Graphics.Tests.csproj"));
        testsCsproj.Should().Contain("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" />");
    }

    [Fact]
    public void Name_validation_enforces_file_safe_identifier_names()
    {
        ApplicationTemplate.GetNameError("MyNewApp").Should().BeNull();
        ApplicationTemplate.GetNameError("My.New.App2").Should().BeNull();
        ApplicationTemplate.GetNameError("").Should().NotBeNull();
        ApplicationTemplate.GetNameError("1App").Should().NotBeNull();
        ApplicationTemplate.GetNameError("My App").Should().NotBeNull();
        ApplicationTemplate.GetNameError("My/App").Should().NotBeNull();
        ApplicationTemplate.GetNameError("My..App").Should().NotBeNull();
        ApplicationTemplate.GetNameError("App.").Should().NotBeNull();
        ApplicationTemplate.GetNameError("Windows").Should().NotBeNull();
        ApplicationTemplate.GetNameError("My.System.Tools").Should().NotBeNull();
    }

    [Fact]
    public void Required_package_ids_are_the_test_packages_only_when_libraries_are_requested()
    {
        //Arrange
        var options = MakeOptions();

        //Act
        var ids = ApplicationTemplate.GetRequiredPackageIds(options);

        //Assert — with libraries, the ids are exactly the test-project packages
        // (the application projects come versioned from the archive, not here).
        ids.Should().Contain("SilverAssertions.ApacheLicenseForever");
        ids.Should().Contain("xunit.v3");
        ids.Should().NotContain("CodeBrix.Platform.Runtime.Skia.MacOS.ApacheLicenseForever");
        ids.Should().NotContain("CodeBrix.Platform.Fonts.OpenSans.ApacheLicenseForever");

        // Without libraries there are no test projects, so nothing to resolve.
        options.LibrarySuffixes = Array.Empty<string>();
        ApplicationTemplate.GetRequiredPackageIds(options).Count.Should().Be(0);
    }
}

public class PackageVersionResolverTests : IDisposable
{
    readonly string tempDirectory;

    public PackageVersionResolverTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "codebrix-develop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void PickNewest_prefers_the_highest_stable_version()
    {
        PackageVersionResolver.PickNewest(new[] { "1.0.9", "1.0.10", "1.0.10-preview.1" }).Should().Be("1.0.10");
        PackageVersionResolver.PickNewest(new[] { "2.0.0-rc.1", "2.0.0-rc.2" }).Should().Be("2.0.0-rc.2");
        PackageVersionResolver.PickNewest(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void Local_cache_fallback_returns_the_newest_cached_version()
    {
        //Arrange — a fake ~/.nuget/packages with two cached versions.
        Directory.CreateDirectory(Path.Combine(tempDirectory, "some.package", "1.0.2"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "some.package", "1.0.10"));
        var resolver = new PackageVersionResolver(tempDirectory);

        //Assert
        resolver.GetNewestLocalCacheVersion("Some.Package").Should().Be("1.0.10");
        resolver.GetNewestLocalCacheVersion("absent.package").Should().BeNull();
    }
}
