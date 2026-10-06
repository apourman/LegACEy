# LegACEy UI preview

Run the desktop preview with an optional `client_portal.dat` path:

```powershell
dotnet run --project decal-plugin/LegACEy.Client.Preview -- "C:\Turbine\Asheron's Call\client_portal.dat"
```

The **Load game art** field also accepts the path after the app opens. The gallery uses the AC art source when a dat is loaded; the theme picker and gallery button switch the open controls between the AC and Simple themes. The input test panel is the same demo control used by the in-game plugin.

The first window is **Account Vault**, a static shell with twelve sample items,
retail-inspired charcoal, gray sockets, gold trim and navy accents, with icons
decoded from the supplied DAT. The first item
is shown in the detail pane; search, sorting, appraisal and transfer faces are
display-only placeholders. The vault keeps its styling when switching gallery
themes. The skin is drawn by Avalonia and ships in the demo assembly, without
external skin images. Nothing is connected to an account, inventory or server.
In game, the **Vault preview** indicator (chest icon, K overlay or fallback label)
opens the same shell.

The K overlay identifies the indicator; it is not a keyboard shortcut. Close
the game before rebuilding or replacing loaded plugin assemblies.
