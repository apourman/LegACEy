# LegACEy UI preview

Run the desktop preview with an optional `client_portal.dat` path:

```powershell
dotnet run --project decal-plugin/LegACEy.Client.Preview -- "C:\Turbine\Asheron's Call\client_portal.dat"
```

The **Load game art** field also accepts the path after the app opens. The gallery uses the AC art source when a dat is loaded; the theme picker and gallery button switch the open controls between the AC and Simple themes. The input test panel is the same demo control used by the in-game plugin.
