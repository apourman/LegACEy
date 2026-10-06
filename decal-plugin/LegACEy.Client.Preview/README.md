# LegACEy UI preview

Run the desktop preview with an optional `client_portal.dat` path:

```powershell
dotnet run --project decal-plugin/LegACEy.Client.Preview -- "C:\Turbine\Asheron's Call\client_portal.dat"
```

The **Load game art** field also accepts the path after the app opens. The gallery uses the AC art source when a dat is loaded; the theme picker and gallery button switch the open controls between the AC and Simple themes. The input test panel is the same demo control used by the in-game plugin.

The first window is **Account Vault**, a static shell with twelve sample items,
retail-inspired charcoal, gold trim and navy accents. Inventory cells, their
selection overlay and item icons are decoded from the supplied DAT and drawn at
32px without scaling. The cells use retail contents-list sprite `06004D20` and
selection overlay `06004D21`; missing art falls back to plain borders. The first
item is shown in the detail pane; search, sorting, appraisal and transfer faces are
display-only placeholders. The vault keeps its styling when switching gallery
themes. The window skin is drawn by Avalonia and ships in the demo assembly; retail
cell and item artwork is read from the player's DAT rather than bundled. Nothing
is connected to an account, inventory or server.
In game, the **Vault preview** indicator (chest icon, K4 overlay or fallback label)
opens the same shell.

The K4 overlay identifies the indicator; it is not a keyboard shortcut. Close
the game before rebuilding or replacing loaded plugin assemblies.

The current shell displays **Preview v4** beside the close button and in its
footer, followed by the demo assembly's source commit (or `local` when source
metadata is unavailable). The indicator displays **K4**. These markers identify
the vault preview revision separately from the plugin's official release version.
If the markers are absent, the loaded assemblies predate this revision. Close the
game, pull and rebuild, and update the plugin's companion DLLs as well as its main
DLL if Decal loads a separate installation folder.
