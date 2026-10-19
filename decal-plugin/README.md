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
| `LegACEy.Client.DecalPlugin` | The net48 stub Decal registers: Decal access, and starts the .NET 10 client from `runtime/` |
| `LegACEy.Client` | The client on .NET 10 (runtime, hooks, retail takeover) and its bridge to the Decal entry point |
| `LegACEy.Client.PanelHost` | Avalonia surface hosting and rendering |
| `LegACEy.Client.InputRouter` | Mouse and keyboard routing |
| `LegACEy.Client.GameArt` | Game DAT art loading |
| `LegACEy.Client.Themes` | Shared UI themes and controls |
| `LegACEy.Client.Demo` | Framework windows and controls, and the plugin API (`ILegACEyPlugin`, `ILegACEyClient`) |
| `LegACEy.Plugin.Vault` | The account Vault plugin, built into `Plugins/Vault/` |
| `LegACEy.Plugin.Inventory` | The inventory window, built into `Plugins/Inventory/` |
| `LegACEy.Updater` | The net48 `LegACEy.Updater.exe` beside the stub, which applies a staged release after the game exits |
| `LegACEy.Client.Tests` | Automated tests without an installed Decal runtime |
| `LegACEy.Client.HookSmoke` | Windows native hook regression check |

Project and assembly names retain the `LegACEy.Client` prefix.

## Build the plugin

Build on Windows with the .NET 10 SDK and the .NET Framework 4.8 targeting pack.
The stub targets .NET Framework 4.8 and x86, matching the game process, and
starts the client on .NET 10 from a `runtime/` folder beside it. Install Decal
first.

From the repository root, build the stub, then publish the client into its
`runtime/` folder:

```powershell
dotnet build decal-plugin/LegACEy.Client.DecalPlugin/LegACEy.Client.DecalPlugin.csproj --configuration Release
dotnet publish decal-plugin/LegACEy.Client/LegACEy.Client.csproj --configuration Release --runtime win-x86 --self-contained --output decal-plugin/LegACEy.Client.DecalPlugin/bin/Release/net48/runtime
```

Rerun the publish after changing the client or its libraries; the stub build
alone does not update `runtime/`. With the game closed,
`decal-plugin/build/Update-LocalPlugin.ps1` pulls (fast-forward only), builds
and publishes in one step. The publish is self-contained and trimmed: it
holds the .NET runtime, the client, Avalonia, and the x86 Skia and HarfBuzz
native libraries. The project locates Decal through `DecalInstallDir` and
`DecalInteropDir`. Override these MSBuild properties with `-p:PropertyName=PATH`
if your installation differs from the project defaults.

Register `bin/Release/net48/LegACEy.Client.DecalPlugin.dll` in Decal; it is not
a standalone executable. Each LegACEy plugin is copied beside it as
`Plugins/<Name>/<Name>.dll`, and the client loads those at startup. Keep
`LegACEy.Client.GameArt.dll`, `Plugins/` and `runtime/` beside the main DLL. If
Decal is registered against a previous checkout path, update that registration
after moving the directory.

The stub build also copies `LegACEy.Updater.exe` beside the main DLL. Run as
`LegACEy.Updater.exe <install> <install>\update\<version>\files` from a copy
outside the install root (such as `<install>\update\<version>\`), it waits
until no `acclient.exe` is left, then applies that stage, or the newest ready
stage under `<install>\update\` if one staged later is newer. A stage is ready
once `<install>\update\<version>\ready` exists: every folder the release ships
(`runtime\`, `Plugins\`) replaces the installed one, every root file overwrites
the installed one, and other files in the install stay. It logs to
`<install>\updater.log`. When done it deletes `ready`, `files\`, the download
and its working `rollback\` folder; the version folder, which holds the running
updater, stays for the client to remove. A failure puts the install back and
keeps the stage for the next try. Exit codes: 0
applied, 1 failed and rolled back, 2 bad arguments or no ready stage, 3 another
updater is already running.

An install made by setup.exe updates itself; a build from a checkout never does,
because only setup.exe puts `install.json` beside the main DLL. At login and
every 6 hours the client asks GitHub for the newest published
`decal-plugin-vX.Y.Z` release (at most once an hour for all clients on the
install, timed by the `update-check` file). A newer one is downloaded into
`update\<version>\`, checked against its `SHA256SUMS.txt` and unpacked; chat
says it installs when the game closes, and the updater applies it then, so the
next launch runs it. The LegACEy menu shows the installed version and a
Check button, which checks now past the hourly gap (at most once a minute) and
says the outcome in chat; it is greyed out on a checkout build or with
`no-auto-update`. The checksum guards against a corrupt download, not a
compromised release: the sums file comes from the same release. The updater
logs to `updater.log`. Creating an empty file
named `no-auto-update` beside the main DLL turns auto-update off.

Creating an empty file named `fail-post-ui-draw` beside the main DLL makes the
next post-UI draw fail in game. The file is deleted, the failure is logged to
`legacey-avalonia.log`, and the retail EndScene hook is removed, which exercises
that removal path. With several clients sharing the directory, whichever client
checks first takes the file; the `[pid N]` prefix shows which one.

The LegACEy Inventory replaces retail's inventory panel: it parks retail's panel and opens and closes its own window with it (by key,
toolbar or item). The appraisal window likewise replaces retail's examination window: when retail's examination panel shows, it is parked
and closed, and the appraised object opens in LegACEy's window. Its header's Inspect button opens the Inspect window: the object's 3D
paperdoll alone, which drags to turn (it needs a server with `paperdoll.inspect`). The expand button in the inventory's doll area opens
the same window on your own character. Creating an empty file named `retail-inventory-native` beside the main DLL
gives retail's inventory and examination panels back; deleting it
takes the panel over again. The file is only checked, never consumed. A drag that leaves the LegACEy window is handed to retail's own
drag when retail's inventory lists show the item, so the world, NPCs and other retail windows take it as they would from retail.

The Dereth toolbar replaces retail's toolbar, drawn at retail's toolbar position, from portal space at login on. Press and drag the toolbar's frame or
top edge (not a button) to move it anywhere on screen, unless retail's UI is locked; the place is saved per character in
`window-positions.txt`, and retail's own toolbar stays where retail left it. The two shortcut bars start beside the toolbar and move on their own once the character is in game; row 2's tag
shows or hides row 2. Creating an empty file named `retail-toolbar-native` beside the main DLL gives retail's toolbar back within a
second; deleting it takes the toolbar over again.

The Dereth chat window replaces retail's main and floating chat windows. It starts with six tabs (All, Speech, Fellowship, Allegiance,
Channels, Tells); each tab shows its own kinds of line, talks to its own target and counts unread lines while out of view. Right-click a
tab to rename, edit, reorder or remove it, and use + to add one. Drag a tab out of the strip into a window of its own, and drag it back to
merge it; tabs are kept per character in `chat-tabs.txt` and window places and sizes in `window-positions.txt`. Enter opens the input and
Escape leaves it. Left alone for 15 seconds, the window fades to bare lines; hovering or pressing Enter brings it back.
Retail's chat keeps running out of sight, so Decal plugins see chat as before. Creating an empty file named `retail-chat-native` beside
the main DLL gives retail's chat back within a second; deleting it takes the chat over again. In the chat box, Up and Down bring back what you sent, Ctrl+V pastes, and Ctrl+C and Ctrl+X copy. A drag
in the log selects across lines and copies as you let go; Ctrl+C copies it again without the game seeing the key, until you click
outside the chat. The chat's text size is set by its Aa
button (click larger, right-click smaller) or Ctrl and the wheel, per character.

Retail's Lock UI locks every LegACEy window too: while it is on, none moves or resizes, and none shows resize grips or corners.

The Dereth vitals window replaces retail's vitals, at retail's place. Health, stamina and mana show current / max in retail's colours; a
drop leaves a pale trail, and health pulses under a quarter. The gear shown on hover switches between the stacked window and a slim
side-by-side strip; each layout keeps its own size, and the choice is kept per character. Drag the corner grip or the bottom edge to
draw the window larger or smaller (up to twice its size), and a side edge to stretch its bars longer. Creating an empty file named `retail-vitals-native` beside the main DLL gives retail's vitals back within
a second; deleting it takes them over again.

The Dereth radar replaces retail's radar, at retail's place: the land around the player from the DATs, dots in retail's radar colours, a
heading tape above and the coordinates below. Hover a dot to name it with its distance and direction, click it to select it, and use the
wheel to zoom. The bezel's lock button locks and unlocks retail's UI; its gear opens the options (Show ground, North up, Range rings,
Zoom), kept per character. With the UI unlocked, hovering the radar shows gold corners: drag one, or an edge, to draw it larger or smaller,
from its 1× layout to three times it; it
opens at twice. Creating an empty file named `retail-radar-native` beside the main DLL gives retail's radar back within a
second; deleting it takes the radar over again.

The LegACEy menu's UI size row scales every LegACEy window, the toolbar and the shortcut bars, from 60 to 150 percent (85 to start),
for every character. A change rebuilds the LegACEy UI at once, as a relog does: plugin windows that were open close and the chat history
clears.

The LegACEy menu's Windows section switches each of the inventory, toolbar, chat, vitals and radar between LegACEy's window (Custom, the
default) and retail's (Native). It creates or deletes that window's switch file above, so the choice holds for every character on the
install. `/legacey` in chat opens or closes the menu, so it can be reached with retail's toolbar too.

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
   builds and tests that exact release commit, attaches the DLL, runtime ZIP and
   setup.exe, then publishes the draft.

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

The Windows build extracts the Decal 2.9.8.3 references from the official
download, checks its pinned SHA-256 hash, builds the x86 stub, and publishes the
trimmed client into `runtime/`. The package step fails if `runtime/` lacks
`coreclr.dll`, `clrjit.dll`, `System.Private.CoreLib.dll`, `netstandard.dll`, a
LegACEy client library, or the Skia or HarfBuzz native library, or if its
`coreclr.dll` is not x86. The runner's Inno Setup 6 then builds `setup.exe` from
[`LegACEy-decal-plugin.iss`](build/LegACEy-decal-plugin.iss), installing everything in the ZIP.
No game installation or DAT files are needed for the build. Automated
plugin tests, asset publishing checks, and the Windows native hook check must
pass before publishing.

Each release contains:

- `LegACEy.Client.DecalPlugin.dll`, for an existing installation with a matching
  `runtime` folder.
- `LegACEy-decal-plugin-v<version>-windows-x86.zip`, the build layout above
  (`runtime` holds the .NET 10 runtime) plus license notices.
- `LegACEy-decal-plugin-v<version>-setup.exe`, the installer.
- `SHA256SUMS.txt`, covering the DLL, ZIP and installer.

To install, close the game and run `setup.exe`. It installs the ZIP's files and an
`install.json` marker to `%LOCALAPPDATA%\LegACEy\DecalPlugin` for the player who
runs it, and registers the plugin in Decal, asking for administrator approval
for that step only. A plugin already registered by hand is moved onto the
installed copy and enabled. Setup stops if the game is running or Decal is not
installed. `runtime\` and `Plugins\` belong to the release and are replaced on
every install and update, so keep no files of your own there. Uninstall it from
Windows' Apps list: that removes the installed files and the Decal registration,
and keeps your settings, switch files and logs.

The ZIP stays for manual installs: extract it and register its
`LegACEy.Client.DecalPlugin.dll` in Decal. To update, extract the ZIP over the
old folder; updating the client needs the ZIP, not just the main DLL. Files that
older releases left at the root (every DLL except `LegACEy.Client.DecalPlugin.dll`
and `LegACEy.Client.GameArt.dll`, and the `x86` folder) are unused and can be
deleted.
Players need Decal and .NET Framework 4.8, which Decal itself needs; neither is
bundled. The .NET 10 runtime ships in `runtime`, so nothing else is installed.

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
