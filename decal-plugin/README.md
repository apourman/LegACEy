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
| `LegACEy.Client.Demo` | Framework windows and controls, and the plugin API (`ILegACEyPlugin`, `ILegACEyClient`) |
| `LegACEy.Plugin.Paperdoll` | The 3D paperdoll plugin, built into `Plugins/Paperdoll/` |
| `LegACEy.Plugin.Vault` | The account Vault plugin, built into `Plugins/Vault/` |
| `LegACEy.Plugin.Inventory` | The inventory window, built into `Plugins/Inventory/` |
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
output. Each LegACEy plugin is copied beside it as
`Plugins/<Name>/<Name>.dll`, for example `Plugins/Paperdoll/Paperdoll.dll` and `Plugins/Vault/Vault.dll`;
the client loads those at startup. Keep the `Plugins` folder beside the main DLL. Keep those dependencies available beside the plugin; the main DLL is
not a standalone executable. If Decal is registered against a previous checkout
path, update that registration after moving the directory.

Creating an empty file named `fail-post-ui-draw` beside the main DLL makes the
next post-UI draw fail in game. The file is deleted, the failure is logged to
`legacey-avalonia.log`, and the retail EndScene hook is removed, which exercises
that removal path. With several clients sharing the directory, whichever client
checks first takes the file; the `[pid N]` prefix shows which one.

The LegACEy Inventory replaces retail's inventory panel: it parks retail's panel and opens and closes its own window with it (by key,
toolbar or item). Creating an empty file named `retail-inventory-native` beside the main DLL gives retail's panel back; deleting it
takes the panel over again. The file is only checked, never consumed. A drag that leaves the LegACEy window is handed to retail's own
drag when retail's inventory lists show the item, so the world, NPCs and other retail windows take it as they would from retail.

## Tests

Run the automated tests from the repository root:

```sh
dotnet test decal-plugin/LegACEy.Client.Tests/LegACEy.Client.Tests.csproj --configuration Release
```

See the [Windows hook check](LegACEy.Client.HookSmoke/README.md)
for the native hook regression exercise. Automated checks do not replace
in-game validation.

## Releases

Release-please manages the server and plugin as separate packages, with separate
release PRs, versions, tags, and changelogs. Its shared configuration is in
[`release-please-config.json`](../release-please-config.json).

1. Merge plugin work into `master`. Release-please creates or updates the plugin
   release PR with its next version and [CHANGELOG.md](CHANGELOG.md).
2. Merge that plugin release PR when ready to publish. It commits the new
   [VERSION](VERSION) and changelog, then release-please creates a draft release
   tagged `decal-plugin-v<version>`.
3. The [plugin publishing workflow](../.github/workflows/release-decal-plugin.yml)
   builds and tests that exact release commit, attaches the DLL and runtime ZIP,
   then publishes the draft.

Work on other branches creates no official release. Only changes under
`decal-plugin/` contribute to the plugin release PR. The root/server package
excludes `Client/`, `decal-plugin/`, and `.github/`. Changes confined to those directories
cannot generate a server release PR. Release-please's generated plugin release
PR uses a `chore` title, which does not generate server release notes even though
it also updates the shared root manifest. The server package still covers other
repository paths: a qualifying `fix` or `feat` change to a root file, such as the
root README or release configuration, can update the server release PR. Use
`docs`, `ci`, or `chore` titles for documentation or release configuration work.
Plugin workflow changes under `.github/` do not independently bump the plugin;
release-please tracks that package only under `decal-plugin/`. A change affecting both
products can update both release PRs; each release PR is merged independently.

Use conventional commit messages for plugin changes, such as `fix: correct UI
input`, `feat: add a window`, `docs: update plugin instructions`, or `test: cover
scrolling`. The plugin includes documentation, tests, build, CI, refactoring,
maintenance, style, and revert commits in its changelog rather than hiding them.
Unstructured commit messages are not release-please release entries; use a
conventional title when squashing a PR. Versions start at `0.1.0` and advance
by one patch version per plugin release PR, even when several changes accumulate.

Official release jobs run only in `apourman/LegACEy` on `master` and are skipped
in forks. The [component release workflow](../.github/workflows/release-components.yml)
runs release-please once and routes its package-specific outputs to the plugin
publisher and the existing ACE deployment workflow. Only a server release
enters the `ace-production` queue; plugin work cannot occupy that queue.
Plugin publishing uses only the `decal-plugin` outputs; server publishing uses
only the root package outputs.
The plugin's publishing job leaves release-please's release notes intact and
keeps the server as GitHub's repository-wide latest release.

The Windows build extracts Decal 2.9.8.3 and Managed DirectX 1.1 references from
official downloads, checks their pinned SHA-256 hashes, and builds the x86
plugin. No game installation or DAT files are needed for the build. Automated
plugin tests, asset publishing checks, and the Windows native hook check must
pass before publishing.

Each release contains:

- `LegACEy.Client.DecalPlugin.dll`, for an existing installation with matching
  companion libraries.
- `LegACEy-decal-plugin-v<version>-windows-x86.zip`, containing the main DLL,
  companion managed DLLs, x86 rendering libraries, the `Plugins` folder with the
  bundled LegACEy plugins, and license notices.
- `SHA256SUMS.txt`, covering the DLL and ZIP.

For a fresh installation, extract the ZIP and register its
`LegACEy.Client.DecalPlugin.dll` in Decal. Keep the companion files and the
`Plugins` folder in place.
Players still need Decal, .NET Framework 4.8, and Managed DirectX installed;
those external references are not bundled. Updating companion libraries
requires the ZIP rather than replacing just the main DLL.

A failed build or upload leaves the release as a draft. Use **Re-run failed jobs**
on the original workflow run to retry the plugin build/publish jobs with the
original release-please outputs. The plugin has no manual dispatch recovery;
its saved build artifact is retained for seven days. Retry failed publishing
while that artifact is available. An expired artifact needs a separate recovery
procedure; **Re-run all jobs** can lose the original release-please outputs and is not a
reliable publishing retry. Re-running a completed publishing job leaves
its published assets unchanged. There is no custom version allocator or
post-release changelog PR: release-please updates the changelog before publishing.

### Verify release isolation locally

The offline regression check uses release-please 17.3.0, matching the pinned
GitHub action. It simulates plugin-only, server-only, mixed, and merged release
PR histories without making GitHub requests. It also checks the initial plugin
version, patch increments, separate release PRs/tags, and each configured commit
type. Install its dependency outside the checkout and run from the repository root:

```sh
validation_dir=$(mktemp -d)
npm install --prefix "$validation_dir" --no-audit --no-fund release-please@17.3.0
NODE_PATH="$validation_dir/node_modules" node decal-plugin/build/test_release_please.cjs
```

The server's build provenance and publishing verification checks run directly
against its workflow scripts:

```sh
python3 -m unittest discover -s .github/tests -p 'test_*.py'
python3 -m unittest discover -s decal-plugin/build -p 'test_*.py'
```

The server checks require Linux with Bash and jq, and also run in the server
build job. They cover delayed releases, manual retries, commit/tag mismatches,
corrupt archives, missing provenance, and failed build recovery attempts.
