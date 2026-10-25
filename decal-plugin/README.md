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
| `LegACEy.Plugin.Market` | The in-game Market window, opened from the market station, built into `Plugins/Market/` |
| `LegACEy.Plugin.Inventory` | The inventory window, built into `Plugins/Inventory/` |
| `LegACEy.Updater` | The net48 `LegACEy.Updater.exe` beside the stub, which applies a staged release after the game exits |
| `LegACEy.WorldPatchBuilder` | Developer tool: builds a world patch and its manifest from edited dats; not shipped |
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

An install made by setup.exe can update itself; a build from a checkout never
does, because only setup.exe puts `install.json` beside the main DLL. Nothing
installs until the player asks. At login and every 30 minutes the client asks
GitHub for the newest published `decal-plugin-vX.Y.Z` release (at most once in
5 minutes for all clients on the install, timed by the `update-check` file, which
also passes the release it found to the other clients). A newer one is offered
once per version by a toast at the bottom centre and a chat line, and again on
every press of Check for updates.
The toast's Log out and update (Update when all are closed when several clients
run) asks first and works only in peace stance; it then downloads the release
into `update\<version>\`, checks it against its `SHA256SUMS.txt`, unpacks it,
starts the updater, and logs out and closes the game. The updater applies it
once every client has exited; start the game again from the launcher. The
LegACEy menu shows the installed version and a Check button, which checks now
past the shared gap (at most once a minute) and shows Checking, then the
outcome, as a toast and in chat; it is greyed out on a checkout build or with
`no-auto-update`. The checksum guards against a corrupt download, not a
compromised release: the sums file comes from the same release. The updater
logs to `updater.log`. Creating an empty file
named `no-auto-update` beside the main DLL turns update checks off.

Creating an empty file named `fail-post-ui-draw` beside the main DLL makes the
next post-UI draw fail in game. The file is deleted, the failure is logged to
`legacey-avalonia.log`, and the retail EndScene hook is removed, which exercises
that removal path. With several clients sharing the directory, whichever client
checks first takes the file; the `[pid N]` prefix shows which one.

Every 30 s in game, `legacey-avalonia.log` gets a set of `UI performance` lines; times are median/95th percentile/worst in
milliseconds. `sample` covers the whole UI frame and the post-UI draw, with memory and GC counts. `gc` gives what was allocated and how
long the GC paused the game since the last line, memory load, and the last full and background pauses. `phases` times each part of
the frame (game state, each retail takeover, panels, windows) with the KB it allocated, and what the game thread allocated between frames.
`avalonia` is Avalonia's one step per frame for every window (its jobs, layout, render tick and each window's capture) and `upload` the
texture uploads after it. `world` covers water, weather and combat text, and how many of retail's draws pass through the world draw hook.
One `surface` line per window gives its capture (`tick`), upload and draw times, how many captures changed pixels, and its attribution
over the same 30 s: `layoutPasses` (layout passes its window ran in the shared step), `paintedPixels` (pixels it painted) and `share`
(its part of every window's painted pixels, a stand-in for its part of the shared step, which Avalonia cannot time per window).

The LegACEy Inventory replaces retail's inventory panel: it parks retail's panel and opens and closes its own window with it (by key,
toolbar or item). Its vertical layout follows retail's panel: retail's width, "Inventory of" the character's name, the paperdoll over the
main pack's six columns, and the burden and packs in a column on the right, each pack's fill in an upright bar; only its height changes. Ctrl and Shift clicks pick several items in the open pack;
dragged onto a pack's tile or into its slots, the set moves there one item after another, as fast as the server answers each move
(retail refuses a second move while one is pending). The appraisal window likewise replaces retail's examination window: when retail's examination panel shows, it is parked
and closed, and the appraised object opens in LegACEy's window. Its header's Inspect button opens the Inspect window: the object's 3D
paperdoll alone, which drags to turn. The client reads every object's look from the game's own messages, so Inspect and the inventory's
doll work on any server; only objects outside the world (Vault items) ask a LegACEy server (`paperdoll.inspect`). The expand button in the
inventory's doll area opens the same window on your own character, from its first view each time. The inventory's doll stands still,
facing front without what it holds, and fills the panel above the Slots row. A click, drag, right-click or double-click on it acts on the
worn item drawn under the pointer, as on that item's slot, and a drop on it wields the item; a weapon or shield dropped there first
puts the one it replaces back in the main pack. While an item is dragged, the body parts
it covers are outlined: gold where it can be worn, red where it can't, teal for a worn item being lifted. Which worn item draws which
part is worked out on the client from portal.dat's clothing tables, so this too works on any server. The appraisal window lists an item's lines in the order retail's examination
text does (tinkering, set and ratings, combat, spells, properties, use, requirements, the rest, then spellcraft, mana and each
spell's description), under Dereth headings. Creating an empty file named `retail-inventory-native` beside the main DLL
gives retail's inventory panel back, and `retail-appraisal-native` gives retail's examination panel back; deleting a file
takes its panel over again. Settings → Windows switches both (Inventory and Appraisal); switching Appraisal to Retail with an item open hands that item to retail's
window. The file is only checked, never consumed. A drag that leaves the LegACEy window is handed to retail's own
drag when retail's inventory lists show the item, so the world, NPCs and other retail windows take it as they would from retail.

The Dereth toolbar replaces retail's toolbar, drawn at retail's toolbar position, from portal space at login on. It is laid out as retail's is:
retail's own stance and backpack art at the ends, the window buttons over Use, the selection plaque and Examine, and the two shortcut rows
below, inside the same frame and retail's width: nine touching 32 px slots. A click on a shortcut selects and uses its item, and the slot stays lit while its item is selected. A long target name wraps to two lines. Press and drag the
toolbar's frame or top edge (not a button or slot) to move it anywhere on screen, unless retail's UI is locked; the place is saved per
character in `window-positions.txt`, and retail's own toolbar stays where retail left it. Settings → Windows shows or hides row 2. Creating an empty file named `retail-toolbar-native` beside the main DLL gives retail's toolbar back within a
second; deleting it takes the toolbar over again.

The Dereth chat window replaces retail's main and floating chat windows. It starts with six tabs (All, Speech, Fellowship, Allegiance,
Channels, Tells); each tab shows its own kinds of line, talks to its own target and counts unread lines while out of view. Right-click a
tab to rename, edit, reorder or remove it, and use + to add one. Drag a tab out of the strip into a window of its own, and drag it back to
merge it; tabs are kept per character in `chat-tabs.txt` and window places and sizes in `window-positions.txt`. Enter opens the input and
Escape leaves it. Left alone for 15 seconds, the window fades to bare lines; hovering or pressing Enter brings it back. Settings sets the
delay per character (Never, Instant as in retail, or 10 to 60 seconds), and the whole window's active and inactive opacity, frame and
backdrop alike, as retail's General Chat Options do; with an inactive opacity above 0 the lines stay shown while idle. Names are drawn as
in retail, in their line's colour except in a spell's words, where they are green; Name colour can pick Green, White or Gold for every
name instead.
Retail's chat keeps running out of sight, so Decal plugins see chat as before. Creating an empty file named `retail-chat-native` beside
the main DLL gives retail's chat back within a second; deleting it takes the chat over again, with the lines that came meanwhile in its log. In the chat box, Up and Down bring back what you sent, Ctrl+V pastes, and Ctrl+C and Ctrl+X copy. A drag
in the log selects across lines and copies as you let go; Ctrl+C copies it again without the game seeing the key, until you click
outside the chat. Ctrl and the wheel scale the chat window like
any other; Settings → Windows → Chat → Text size sets the size of its text, per character.

Retail's Lock UI locks every LegACEy window too: while it is on, none moves or resizes, and none shows resize grips or corners.

The Dereth vitals window replaces retail's vitals, at retail's place. Health, stamina and mana show current / max in retail's colours; a
drop leaves a pale trail, and health pulses under a quarter. The gear shown on hover switches between the stacked window and a slim
side-by-side strip; each layout keeps its own size, and the choice is kept per character. Settings → Windows → Vitals → Retail-style
bars drops the vital glyphs and value plaques and shows each value in the middle of its bar, as retail does, in either layout. Drag the corner grip or the bottom edge to
draw the window larger or smaller (up to twice its size), and a side edge to stretch its bars longer. Creating an empty file named `retail-vitals-native` beside the main DLL gives retail's vitals back within
a second; deleting it takes them over again.

The Dereth radar replaces retail's radar, at retail's place: the land around the player from the DATs, dots in retail's radar colours, a
heading tape above and the coordinates below. Hover a dot to name it with its distance and direction, click it to select it, and use the
wheel to zoom. Indoors the radar draws the floor plan of the place from the DATs instead: the player's floor solid, the others faint, and
a building's town ground under it. A ladder beside the bezel has a pip a level, top first; hovering a pip peeks at that level in teal, and the
player's floor is left out until the pointer leaves. A dot on another level is drawn
as a ▲ or ▼ arrow. The bezel's lock button locks and unlocks retail's UI; its gear opens the options (Show map, North up,
Range rings, Zoom), kept per character, with the indoor zoom kept apart from the outdoor one. With the UI unlocked, hovering the radar
shows gold corners: drag one, or an edge, to draw it larger or smaller, from its 1× layout to three times it; it opens at twice.
Ctrl+click a spot on the radar to ping it: a pulsing diamond shows there for 15 seconds, in a colour picked from the sender's name, and
a ping past the radar's range is an arrow on the rim. The same spot gets a pillar of light of that colour in the world, with a pulsing
ring on the ground, for as long as the ping lasts; it shows through walls and terrain, and indoors the walls around it light up
with a sweeping scan. Indoors a ping lands on the level drawn under the pointer: the bright one, or a dimmed one where only it has floor. On a LegACEy server your fellowship sees it too (fellows in the same instance on
their radar, every fellow as a fellowship chat line with the coordinates); without a fellowship, or on another server, only you do. Creating
an empty file named `retail-radar-native` beside the main DLL gives retail's radar back within a second; deleting it takes the radar over
again.

The Dereth attack bar replaces retail's attack panel in melee and missile combat, centred over retail's place the first time. Click the
groove to set the power (accuracy with a bow; hover it for the percentage), a plaque to pick High, Medium or Low, and a gem to switch
Repeat Attacks, Auto Target or Keep in View; retail's keys still work and the bar follows them. The charge fills the groove as retail
builds it. With the UI unlocked, hovering the bar shows gold corners: drag a side edge to make the bar longer or the bottom edge to
give it more height (up to 960 by 150 DIPs); the text keeps its size, and Ctrl+wheel scales the whole bar. In peace and magic the bar closes and retail's panels are left as retail draws them. Creating an empty file named
`retail-attack-native` beside the main DLL (or the Settings window's Attack bar row) gives retail's panel back within a second.

Retail's yellow game messages at the top of the screen ("You can't move that!", "In Portal Space - Please Wait...") show as Dereth bands
instead, from character select on: one line on a dark band, up to three at once with the newest on top, each for 4 seconds; a repeat moves
back to the top. Chat is untouched. Settings → Windows → Game messages turns them off entirely, sets their text size (60 to 200 percent,
with a preview), and moves them: Move shows a sample band to drag anywhere, Reset puts them back at the top centre. These are kept for
every character in `plugin-settings.txt`, since the messages show before a character is chosen. Creating an empty file named
`retail-game-messages-native` beside the main DLL (or the card's Retail switch) gives retail's yellow text back.

The Settings window's Windows tab has a Dereth / Retail switch on each window's card, and an All windows switch above them that
writes or deletes every `retail-*-native` file at once. It lights neither choice while the windows differ.

The LegACEy menu's UI size row scales every LegACEy window and the toolbar with its shortcut rows, from 60 to 150 percent (85 to start),
for every character. Ctrl and the wheel over a window or the toolbar scales it on its own, in 5 percent steps from 50 to 200 percent,
per character, and shows its size over it while it changes. Item icons scale with their window like the rest of it. A change rebuilds the LegACEy UI at once, as a relog does: plugin windows that were open close and the chat history
clears.

The LegACEy menu's Windows section switches each of the inventory, appraisal, toolbar, chat, vitals, radar and attack bar between LegACEy's window (Custom, the
default) and retail's (Native). It creates or deletes that window's switch file above, so the choice holds for every character on the
install. `/legacey` in chat opens or closes the menu, so it can be reached with retail's toolbar too.

## Worlds

A LegACEy server can run a world of its own (new land, towns, dungeons) and
ship it as a world patch: every dat record that differs from retail, the cell
dat's edition record included. The world version is that edition.

The plugin never writes a world into the dat files. It lays the patch over the
retail dats while the game is connected to that world's server: one hook on
retail's `DiskController::LoadDataEx`, which every dat read, existence check
and edition read goes through, answers from the patch when it has the record
and lets every other read through to the retail file. So one game folder plays
every server, LegACEy worlds and plain ACE alike, and nothing ever needs
repairing. The patch's index is held in memory and its records are read from
the file when the client asks for them. Every few minutes and at logout,
`legacey-avalonia.log` gets a line with the hook's calls, records served,
look-ups answered and time spent inside it.

Each world the folder has visited is cached under
`<game>\legacey-worlds\<world key>\` (`world.json` and
`world-<edition>.patch`), keyed by its update-list address;
`legacey-worlds\servers.json` maps `host:port` to a world. At startup the
plugin reads the server from the game's `-h` and `-p` arguments (nothing else
from that line, which also carries the account and password) and switches that
server's cached world on before the client opens its dats, after checking its
SHA-256. The client then reports the world's edition when it connects.

The server announces its world at character select. With
`LegaceyWorldUpdateUrl` set in its `Config.js`, it sends a LegACEy channel note
right after the character list: the world's name (`LegaceyWorldName`), the
cell dat edition it runs, and the update list's address.

The plugin holds entering the world (hooks on retail's
`gmCharacterManagementUI::EnterGame`, behind the Enter button, and
`CPlayerSystem::LogOnCharacter`, which character creation and auto-login
filters also reach) from its start until it knows what the server needs: the
note, or the end of the DDD exchange (`DDD_EndDDD`) without one. Then:

- **No note:** a plain server. The overlay goes off (and that server is
  forgotten, if a world was switched on for it at startup); Enter opens.
- **The world on is at the server's edition:** Enter opens without asking the
  update host.
- **The world is cached at that edition** (reached through another host, or
  not on yet): it's switched on, and Enter opens once the client has sent its
  dat report again (below).
- **Otherwise** (first visit, or a new release): the plugin reads the update
  list (`{ "version": N, "patch": "<url>", "size": <bytes>, "sha256": "<hex>" }`),
  downloads the patch into the cache with a toast showing progress, checks its
  size and SHA-256 (a bad download is retried once), switches it on and sends
  the dat report again. No restart. A new release whose `client_portal.dat`
  records the old one didn't have says those show after a relaunch: the
  client may already hold the old ones.

Sending the report again calls retail's `CLCache::OnServerInterrogation`,
which builds the report from each dat's edition record at send time, so the
overlay's edition goes out. ACE re-decides the session's dat verdict on a
repeat report, without starting DDD or ending the exchange again. If the
client doesn't match the entry, the toast asks the player to relaunch: the
world is cached and switches on at startup.

Addresses must be https; plain http works only for this machine and private
networks, for local testing. A download that fails shows a toast and opens
Enter, and the server decides (below). Each step is logged to
`legacey-avalonia.log`.

With DAT patching off, ACE no longer kicks clients whose dats are older than
the server's at connect: they reach character select, where the plugin can
switch the world on, and are refused at Enter with a message to log in with
the LegACEy plugin. Keep the `refuse_older_dats` server setting on (the
default) for world servers: then nobody without the world enters it. Off, they
get in with a chat warning. Clients with newer dats are still kicked at
connect.

Build a patch and its update list from WSL or Windows, after raising the
edited cell dat's edition above retail's:

```bash
dotnet run --project decal-plugin/LegACEy.WorldPatchBuilder -- build <retail dats> <edited dats> <out> <url folder>
dotnet run --project decal-plugin/LegACEy.WorldPatchBuilder -- check <edited dats> <out>/world-<edition>.patch
```

`build` writes `world-<edition>.patch` and `world-manifest.json` (whose patch
address is `<url folder>/world-<edition>.patch`); `check` reads every record
back through the index the overlay serves it with, confirms each matches the
edited dats, and that the patch carries the cell dat's edition record. Upload
both to a GitHub release and set the server's `LegaceyWorldUpdateUrl` to
`https://github.com/<owner>/<repo>/releases/latest/download/world-manifest.json`,
which follows each new release. For a local test, serve `<out>` with
`python3 -m http.server` and use that address.

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
