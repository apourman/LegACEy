# LegACEy Decal Plugin

LegACEy's Windows Decal plugin adds Avalonia UI to Asheron's Call. It hosts
in-game windows, routes mouse and keyboard input, displays game art, and supplies
themes and controls for LegACEy features.

This directory contains the plugin and its supporting projects. The ACE server
and database remain in the repository's upstream-compatible `Source/` and
`Database/` directories.

## Projects

| Project | Purpose |
| --- | --- |
| `LegACEy.Client.DecalPlugin` | Decal entry point, game integration, and native rendering hooks |
| `LegACEy.Client.PanelHost` | Avalonia surface hosting and rendering |
| `LegACEy.Client.InputRouter` | Mouse and keyboard routing |
| `LegACEy.Client.GameArt` | Game DAT art loading |
| `LegACEy.Client.Themes` | Shared UI themes and controls |
| `LegACEy.Client.Demo` | In-game demo windows and controls |
| `LegACEy.Client.Preview` | Desktop UI preview without running the game |
| `LegACEy.Client.Tests` | Automated tests without an installed Decal runtime |
| `LegACEy.Client.HookSmoke` | Windows native hook regression check |

Project and assembly names retain the `LegACEy.Client` prefix.

## Build the plugin

Build on Windows with a .NET SDK that supports these projects and the .NET
Framework 4.8 targeting pack. The plugin targets .NET Framework 4.8 and x86,
matching the game process. Install Decal and Managed DirectX first.

From the repository root:

```powershell
dotnet build decal-plugin/LegACEy.Client.DecalPlugin/LegACEy.Client.DecalPlugin.csproj --configuration Release
```

The project locates Decal and Managed DirectX through `DecalInstallDir`,
`DecalInteropDir`, and `ManagedDirectXDir`. Override these MSBuild properties
with `-p:PropertyName=PATH` if your installation differs from the project defaults.

The main output is
`LegACEy.Client.DecalPlugin/bin/Release/net48/LegACEy.Client.DecalPlugin.dll`
relative to this directory. It loads through Decal and requires its companion
managed libraries and the `x86/` native rendering libraries from the build
output. Keep those dependencies available beside the plugin; the main DLL is
not a standalone executable. If Decal is registered against a previous checkout
path, update that registration after moving the directory.

## Tests and preview

Run the automated tests from the repository root:

```sh
dotnet test decal-plugin/LegACEy.Client.Tests/LegACEy.Client.Tests.csproj --configuration Release
```

See the [desktop preview instructions](LegACEy.Client.Preview/README.md) to
inspect the UI outside the game, and the [Windows hook check](LegACEy.Client.HookSmoke/README.md)
for the native hook regression exercise. Automated checks do not replace
in-game validation.

## Release boundary

The agreed release policy is one independent plugin release for each push or
merge into `master` that changes any file in `decal-plugin/`, including tests
and documentation. Work on other branches produces no official plugin release.
Changes outside this directory do not trigger a plugin release, and plugin-only
changes do not release or deploy the server.

Official publishing is restricted to `apourman/LegACEy`. Release and deployment
jobs must check the repository identity so they are skipped in forks, even if
the fork owner enables GitHub Actions or manually starts a workflow.

Plugin releases will use `decal-plugin-v<version>` tags and publish the built
plugin DLL. This policy describes the planned automation; the repository's
existing release workflow currently builds and deploys the ACE server only.
