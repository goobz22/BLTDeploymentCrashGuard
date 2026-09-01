# Hot-reload dev workflow (no game restart)

The mod is split into two assemblies:

- **Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — the stable module Bannerlord loads.
  Owns the game lifecycle + the reload engine. Changing it still needs a restart (rare).
- **Payload** (`Payload/` → `BLTDeploymentCrashGuard.Payload.dll`) — all guards/fixes/tracers.
  Hot-reloadable. This is where ~all iteration happens.

Every generation loads via `Assembly.LoadFrom` on a per-generation shadow copy (LoadFrom-context binding is required — byte-loading binds 0Harmony to the wrong copy via app-base probing); each payload build compiles under a unique assembly NAME (`BLTDeploymentCrashGuard.Payload.b<stamp>`, published under the fixed file name) because the LoadFrom context dedups simple-named assemblies by name only — a unique version alone is collapsed (field-proven 2026-09-01). Fresh statics and a per-generation Harmony
owner id (`bltogether.crashguard.gen{N}`); the new generation is applied first, then the previous
generation is `UnpatchAll`'d — a failed reload keeps the previous generation, so the game is never
left unpatched.

## Enabling hot-reload (dev only — never ship this on)

1. In `guardconfig.json`: `"hotReload": true`.
2. Create an empty marker file `.hotreload-dev` in the module root
   (`Modules/BLTDeploymentCrashGuard/.hotreload-dev`). Both conditions are required — this makes
   runtime code loading impossible on a normal player install.

Two reload sources:

### A) Build-and-drop (default, bulletproof, zero extra deps)

Leave `"hotReloadRoslyn": false`. The engine watches the deployed
`bin/Win64_Shipping_Client/BLTDeploymentCrashGuard.Payload.dll`. Iterate:

```
cd Payload && dotnet build -c Release
copy /Y bin\Release\BLTDeploymentCrashGuard.Payload.dll "<Game>\Modules\BLTDeploymentCrashGuard\bin\Win64_Shipping_Client\"
```

The engine reloads within ~400ms. `CrashGuard.log` shows `[HOTRELOAD] gen2 applied (reload), unpatched …gen1`.

### B) Edit-.cs auto-reload (Roslyn, slicker, fragile on net472)

Build the harness with Roslyn compiled in, set `"hotReloadRoslyn": true`, and point
`"payloadSourceDir"` at the repo `Payload/` folder:

```
cd Harness && dotnet build -c Release -p:Roslyn=true
```

Now editing any `Payload/*.cs` triggers a runtime Roslyn recompile + reload — no `dotnet build`.
CAVEAT: Roslyn on .NET Framework 4.8 inside Bannerlord can bind-conflict with ButterLib's older
`System.Collections.Immutable` / `System.Reflection.Metadata`. If the runtime compile fails, the
engine logs it and falls back to the prebuilt DLL, so you can always switch to (A).

## Build both for deployment

```
cd Harness && dotnet build -c Release
cd ..\Payload && dotnet build -c Release
```

Deploy BOTH DLLs to `Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/`:
`BLTDeploymentCrashGuard.dll` (harness) and `BLTDeploymentCrashGuard.Payload.dll` (payload).
SubModule.xml still points at the harness; the harness loads the payload itself.

## Trade-offs

- ~1–3 MB leaked per reload (old assembly can't unload in .NET FW); restart every few dozen reloads.
- Harness changes need a restart.
- Known Phase-B gap: `BattleMode`'s foreign-patch stash does not yet survive a reload — reloading
  while in `battleMode=solo` (vanilla, BT battle patches lifted) can leave them lifted. Reloading in
  `battleMode=coop` is unaffected (nothing is lifted). Restart if battle mode misbehaves after a
  reload.
