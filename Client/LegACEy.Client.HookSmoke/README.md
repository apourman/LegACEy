# Windows native hook regression check

Run on Windows with .NET Framework 4.8:

```powershell
dotnet run --project Client/LegACEy.Client.HookSmoke
```

Exercises the production hook against an executable x86 thiscall function with the retail prologue. Checks actual callback execution, original-function forwarding, readiness, draw/report failure isolation, disable and unload. This does not establish in-game ordering or coexistence.
