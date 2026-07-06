# CodeBrix.Develop

An integrated development environment for building CodeBrix.Platform
applications, running on Linux x64.

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
- **CodeBrix.Develop.Debug.LinuxX64** — the NetCoreDbg debugger engine (planned)

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
