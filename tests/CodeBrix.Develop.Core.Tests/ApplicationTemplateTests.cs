using System;
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

    ApplicationTemplateOptions MakeOptions(string name = "MyNewApp", ApplicationFont font = ApplicationFont.OpenSans) =>
        new ApplicationTemplateOptions
        {
            Name = name,
            Location = tempDirectory,
            Heads = new[] { PlatformHead.MacOS, PlatformHead.LinuxX11, PlatformHead.WinWpfSkia },
            Font = font,
            LibrarySuffixes = new[] { "Graphics", "DatabaseAccess" },
        };

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
    public void Generated_test_projects_reference_every_package_with_a_version()
    {
        //Act
        ApplicationTemplate.Generate(MakeOptions());
        var root = Path.Combine(tempDirectory, "MyNewApp");

        //Assert — an unversioned PackageReference resolves to the LOWEST
        // published version, so every reference must carry one.
        var testsCsproj = File.ReadAllText(Path.Combine(root,
            "tests", "libs", "MyNewApp.Graphics.Tests", "MyNewApp.Graphics.Tests.csproj"));
        var references = PackageReferenceReader.Read(testsCsproj);
        references.Select(reference => reference.Id).Should().BeEquivalentTo(ApplicationTemplate.TestPackageIds);
        references.All(reference => !string.IsNullOrWhiteSpace(reference.Version)).Should().BeTrue();

        // The Microsoft.Extensions.* pair takes the template's own Hosting version...
        var coreCsproj = File.ReadAllText(Path.Combine(root, "src", "MyNewApp.Core", "MyNewApp.Core.csproj"));
        var hostingVersion = PackageReferenceReader.ReadVersion(coreCsproj, "Microsoft.Extensions.Hosting");
        hostingVersion.Should().NotBeNull();
        PackageReferenceReader.ReadVersion(testsCsproj, "Microsoft.Extensions.Hosting").Should().Be(hostingVersion);
        PackageReferenceReader.ReadVersion(testsCsproj, "Microsoft.Extensions.DependencyInjection").Should().Be(hostingVersion);

        //...and the packages the template knows nothing about are pinned here.
        PackageReferenceReader.ReadVersion(testsCsproj, "Microsoft.NET.Test.Sdk").Should().Be("18.8.1");
        PackageReferenceReader.ReadVersion(testsCsproj, "SilverAssertions.ApacheLicenseForever").Should().Be("1.0.164.180");
        PackageReferenceReader.ReadVersion(testsCsproj, "xunit.v3").Should().Be("3.2.2");
        // The runner keeps its expanded form (PrivateAssets/IncludeAssets children).
        PackageReferenceReader.ReadVersion(testsCsproj, "xunit.runner.visualstudio").Should().Be("3.1.5");
        testsCsproj.Should().Contain("<PrivateAssets>all</PrivateAssets>");
    }

    [Fact]
    public void Roboto_font_reference_carries_the_roboto_package_version()
    {
        //Act
        ApplicationTemplate.Generate(MakeOptions(font: ApplicationFont.Roboto));

        //Assert — the font swap replaces the package id in the template's
        // text, so the version has to be swapped too: the template only ever
        // carries the DEFAULT font's version, which does not exist for Roboto.
        var coreCsproj = File.ReadAllText(Path.Combine(tempDirectory, "MyNewApp",
            "src", "MyNewApp.Core", "MyNewApp.Core.csproj"));
        PackageReferenceReader.ReadVersion(coreCsproj, "CodeBrix.Platform.Fonts.Roboto.OflLicenseForever")
            .Should().Be("1.0.181.661");
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
    public void Every_generated_project_file_pins_every_package_reference()
    {
        //Act
        ApplicationTemplate.Generate(MakeOptions(font: ApplicationFont.Roboto));
        var root = Path.Combine(tempDirectory, "MyNewApp");

        //Assert — nothing anywhere in the solution is left unversioned, so an
        // application is buildable even when nuget.org cannot be reached.
        foreach (var path in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            foreach (var (id, version) in PackageReferenceReader.Read(File.ReadAllText(path)))
                version.Should().NotBeNull($"{id} in {Path.GetFileName(path)} must carry a version");
        }
    }
}

public class NuGetVersionTests
{
    [Fact]
    public void Previews_are_recognized_and_build_metadata_is_not_a_preview()
    {
        NuGetVersion.IsPreview("1.0.10").Should().BeFalse();
        NuGetVersion.IsPreview("1.0.10-preview.1").Should().BeTrue();
        NuGetVersion.IsPreview("1.0.199.897+abc123").Should().BeFalse();
        NuGetVersion.IsPreview("2.0.0-rc.1+abc123").Should().BeTrue();
    }

    [Fact]
    public void Latest_release_never_selects_a_preview()
    {
        //Assert — numeric segments compare as numbers, not as text...
        NuGetVersion.SelectLatestRelease(new[] { "1.0.9", "1.0.10", "1.0.10-preview.1" }).Should().Be("1.0.10");
        NuGetVersion.SelectLatestRelease(new[] { "3.119.4", "4.148.0", "4.150.1" }).Should().Be("4.150.1");

        //...and a package with nothing but previews published means "leave it
        // alone", never "take the preview".
        NuGetVersion.SelectLatestRelease(new[] { "2.0.0-rc.1", "2.0.0-rc.2" }).Should().BeNull();
        NuGetVersion.SelectLatestRelease(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void Pinned_versions_are_distinguished_from_ranges()
    {
        NuGetVersion.IsPinned("4.150.1").Should().BeTrue();
        NuGetVersion.IsPinned("14.2.1.1").Should().BeTrue();
        NuGetVersion.IsPinned("[4.150.1]").Should().BeFalse();
        NuGetVersion.IsPinned("[4.150.1,5.0.0)").Should().BeFalse();
        NuGetVersion.IsPinned("4.*").Should().BeFalse();
        NuGetVersion.IsPinned("").Should().BeFalse();
    }

    [Fact]
    public void Lower_returns_the_older_version_and_tolerates_nulls()
    {
        NuGetVersion.Lower("4.150.1", "4.148.0").Should().Be("4.148.0");
        NuGetVersion.Lower("1.0.201.336", null).Should().Be("1.0.201.336");
        NuGetVersion.Lower(null, "1.0.201.336").Should().Be("1.0.201.336");
        NuGetVersion.Lower(null, null).Should().BeNull();
    }
}
