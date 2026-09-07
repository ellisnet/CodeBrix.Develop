# CodeBrix.Develop

An integrated development environment for building CodeBrix.Platform
applications, running on Linux (x64 and arm64).

CodeBrix.Develop is inspired by — and architected like — the
[MonoDevelop](https://github.com/mono/monodevelop) IDE, rebuilt for
.NET 10, modern C#, and GTK 4:

| Layer | Project | Role |
|---|---|---|
| Core | `src/CodeBrix.Develop.Core` | No-UI runtime: file paths, solution/project model, build engine (`dotnet` CLI), MSBuild-format error parsing, Roslyn type system service |
| IDE | `src/CodeBrix.Develop.Ide` | GTK 4 workbench: pads, documents, source editor (GtkSourceView 5), commands, menus |
| App | `src/CodeBrix.Develop` | The executable entry point |

Built on:

- **CodeBrix.Develop.UI** — GTK 4 + GtkSourceView 5 bindings for .NET 10
- **Microsoft.CodeAnalysis (Roslyn)** — C# language services (workspaces, completion)
- **CodeBrix.Develop.Debug.LinuxX64** / **CodeBrix.Develop.Debug.LinuxArm64** — the NetCoreDbg debugger engine; the app references whichever matches the host architecture
- **CodeBrix.Develop.Debug.AndroidArm64** / **CodeBrix.Develop.Debug.AndroidX64** — the same debugger built to run on an Android device, for debugging .NET 11 (CoreCLR) Android apps; the app references both and pushes the one matching the device's ABI

Code adapted from MonoDevelop retains its MIT/X11 license headers and carries
a `//was previously:` note on the namespace line. See THIRD-PARTY-NOTICES.txt.

## Building

```
dotnet build CodeBrix.Develop.slnx
dotnet run --project src/CodeBrix.Develop
```

Requires the .NET 10 SDK and the GTK 4 / GtkSourceView 5 native libraries
(Debian: `libgtk-4-1`, `libgtksourceview-5-0`).

## License

MIT — Copyright (c) 2026 Jeremy Ellis and contributors
