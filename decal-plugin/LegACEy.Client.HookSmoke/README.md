# Windows native hook regression check

Run on Windows with .NET Framework 4.8:

```powershell
dotnet run --project decal-plugin/LegACEy.Client.HookSmoke
```

Exercises the production hook against an executable x86 thiscall function with the retail prologue. Checks actual callback execution, the device read, original-function forwarding, readiness, frames that keep running through a draw failure, drawing again, disable and unload. This does not establish in-game ordering or coexistence.
