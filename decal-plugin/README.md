# LegACEy Decal Plugin

A Decal plugin that replaces Asheron's Call's UI with LegACEy's own Avalonia
windows (inventory, appraisal, character, social, map, quests, options, toolbar,
chat, vitals, radar, attack bar, game messages) and adds LegACEy features such as the
Vault, the Market and custom worlds. It works on any ACE server; some features need a LegACEy server.

## Install

Close the game and run `LegACEy-decal-plugin-v<version>-setup.exe` from the
[latest release](https://github.com/apourman/LegACEy/releases). It needs Decal
and .NET Framework 4.8; the .NET 10 runtime is bundled. The plugin checks for
updates itself and offers them in game.

## Options

Settings opens from the toolbar's LegACEy button or `/legacey` in chat. Its
Windows tab switches each window between Dereth and Retail, and sets size and
opacity. Each switch is an empty `retail-<window>-native` file beside the main
DLL, so it holds for every character on the install.

Other files beside the main DLL:

| File | Effect |
| --- | --- |
| `no-auto-update` | Turns update checks off |
| `fail-post-ui-draw` | Fails the next post-UI draw once (tests the hook removal path) |
| `legacey-avalonia.log` | The client log: crashes, performance, world overlay |
| `journals/` | Each character's quest journal, first copied from retail's in `Documents\Asheron's Call` |

## Projects

| Project | Purpose |
| --- | --- |
| `LegACEy.Client.DecalPlugin` | net48 stub Decal loads; starts the .NET 10 client from `runtime/` |
| `LegACEy.Client` | The .NET 10 client: hooks and retail takeovers |
| `LegACEy.Client.PanelHost` | Avalonia surface hosting and rendering |
| `LegACEy.Client.InputRouter` | Mouse and keyboard routing |
| `LegACEy.Client.GameArt` | DAT art loading |
| `LegACEy.Client.Themes` | Shared themes and controls |
| `LegACEy.Client.Demo` | Framework windows and the plugin API |
| `LegACEy.Plugin.*` | Vault, Market and Inventory, built into `Plugins/<Name>/` |
| `LegACEy.Updater` | Applies a downloaded release after the game exits |
| `LegACEy.WorldPatchBuilder` | Builds world patches (developer tool, not shipped) |
| `LegACEy.Client.Tests` | Automated tests |
| `LegACEy.Client.HookSmoke` | [Native hook check](LegACEy.Client.HookSmoke/README.md) on Windows |

## Build

On Windows with Decal, the .NET 10 SDK and the .NET Framework 4.8 targeting
pack, from the repository root:

```powershell
dotnet build decal-plugin/LegACEy.Client.DecalPlugin/LegACEy.Client.DecalPlugin.csproj --configuration Release
dotnet publish decal-plugin/LegACEy.Client/LegACEy.Client.csproj --configuration Release --runtime win-x86 --self-contained --output decal-plugin/LegACEy.Client.DecalPlugin/bin/Release/net48/runtime
```

Or, with the game closed, `decal-plugin/build/Update-LocalPlugin.ps1` pulls,
builds and publishes in one step. Register
`bin/Release/net48/LegACEy.Client.DecalPlugin.dll` in Decal. Override
`DecalInstallDir` or `DecalInteropDir` with `-p:` if Decal lives elsewhere.

## Worlds

A LegACEy server can ship its own world as a patch over the retail dats. The
plugin never writes the dats: it serves patched records from memory while
connected to that server, downloads new patches at character select, and holds
Enter until the right world is on. Build a patch with:

```bash
dotnet run --project decal-plugin/LegACEy.WorldPatchBuilder -- build <retail dats> <edited dats> <out> <url folder>
dotnet run --project decal-plugin/LegACEy.WorldPatchBuilder -- check <edited dats> <out>/world-<edition>.patch
```

Upload both outputs to a release and point the server's `LegaceyWorldUpdateUrl`
at `world-manifest.json`.

## Tests

```sh
dotnet test decal-plugin/LegACEy.Client.Tests/LegACEy.Client.Tests.csproj --configuration Release
```

Automated checks do not replace testing in game.

## Releases

Release-please versions the plugin separately from the server (tags
`decal-plugin-v<version>`); only changes under `decal-plugin/` count. Merging
its release PR publishes a draft that the
[publishing workflow](../.github/workflows/release-decal-plugin.yml) builds with
[`build/Build-Release.ps1`](build/Build-Release.ps1), attaching the DLL, the
ZIP, `setup.exe` and `SHA256SUMS.txt`. The promote PR (`next` into `master`)
runs the same script, tests included. If publishing fails, use **Re-run failed
jobs** on the original run.
