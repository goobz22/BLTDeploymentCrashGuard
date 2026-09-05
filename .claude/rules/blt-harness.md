---
paths: ["Harness/**", "Directory.Build.props", "SubModule.xml", "dist/**", "install.cmd"]
---

# Harness, versioning and deployment — invariants

The harness (`Harness/` → `BLTDeploymentCrashGuard.dll`) is the only assembly Bannerlord loads
(`SubModule.xml:17-19`, `Harness/SubModule.cs:7-8`). It owns lifecycle, logging, config,
health/self-test and the reload engine; it must not depend on payload types — the payload pushes
data in instead (e.g. `Log.SetRoleTag`, `Harness/Log.cs:8-10,32-39`).

## One version source

`Directory.Build.props` `<Version>` is the single source of truth. MSBuild stamps both assemblies
from it, and the `StampSubModuleVersion` target `XmlPoke`s `SubModule.xml`'s `/Module/Version/@value`
to `v$(Version)` on every harness build (`Directory.Build.props:12-19`). `Diag.Version` reads it back
off the assembly identity at runtime (`Harness/Diag.cs:14-31`). Never hardcode a version anywhere
else, including `SubModule.xml` — a build overwrites it. Bump `<Version>` for a release.

## Log contract (`Harness/Log.cs`)

- **Never take the game down.** Every write is inside try/catch (`Log.cs:62-75`).
- **Rotation:** a segment rolls past `MaxLogBytes` = 8 MB into a rolling window of
  `MaxSegments` = 6 (`CrashGuard.log.1` … `.6`, ~48 MB), size re-checked every
  `RotateCheckEveryWrites` = 256 writes — not once per launch, which once let the file reach 283 MB
  (`Log.cs:13-15,78-120`). A window, not a single overwrite: a burst must not discard the evidence
  being chased.
- Every line is `timestamp [roleTag] message`; the role tag (`S`/`H`/`C`) is set by the payload
  (`Log.cs:32-39`, `Payload/PayloadEntry.cs:161-183`).
- The complementary half of the flood defence is `TraceThrottle` in the **payload**
  (`Payload/TraceThrottle.cs:14-17`) — deliberately not in the harness, because the harness DLL is
  locked while the game runs and a throttle fix must be able to hot-reload.
- `Log.Screen` is the on-screen channel; only for what a player must see (`Log.cs:122-131`).

## GuardConfig contract (`Harness/GuardConfig.cs`)

- The file is `<module root>/guardconfig.json`, resolved two levels above the DLL directory
  (`GuardConfig.cs:17-24`). It is **read with regex, not a JSON parser** — no JSON dependency — and
  cached for the whole session (`GuardConfig.cs:26-80`). A value that must be re-readable mid-session
  has to bypass the cache with its own fresh disk read; that is why the tracing flag has
  `FreshTracingFlag` in the payload (`Payload/PayloadEntry.cs:211-232`).
- `Bool(key, fallback)` and `String(key, fallback)` both fall back silently on any failure, so a
  malformed file degrades to defaults rather than crashing.
- **`DefaultJson` must list every key with its `_key` explanation string** and is written on first
  run so every knob is discoverable (`GuardConfig.cs:82-115`). Adding a config key means adding both
  lines there *and* a row in the README `## Config` table. Comment keys are plain JSON string members
  (`"_siegeCommandAll": "…"`), which is why the regex reader tolerates them.

## Hot-reload engine contract (`Harness/HotReload.cs`)

- **Hard gate:** watching activates only when `hotReload=true` **and** a `.hotreload-dev` marker file
  exists in the module root — runtime code loading must be impossible on a player install
  (`HotReload.cs:26-28,69-70`).
- Every generation loads via `Assembly.LoadFrom` on a per-attempt shadow copy placed in the same
  directory as the canonical DLL (`HotReload.cs:276-314`). LoadFrom-context binding is required so the
  payload binds the harness's already-loaded `0Harmony`; a byte load probes the app base and splits
  assembly identity (`HotReload.cs:56-62`).
- Each payload build compiles under a **unique assembly name**
  (`BLTDeploymentCrashGuard.Payload.b<stamp>`, published to the fixed file name by the
  `PublishFixedPayloadName` target) because the LoadFrom context dedups simple-named assemblies by
  name only — a unique version alone gets collapsed
  (`Payload/BLTDeploymentCrashGuard.Payload.csproj:9-24,94-97`).
- Apply order: new generation applies **first**; only on success does the engine swap `_gen` and
  `UnpatchAll` the previous owner id `bltogether.crashguard.gen{N}`. A failed apply keeps the previous
  generation, so the game is never left unpatched (`HotReload.cs:13-16,358-393`). `PayloadEntry.Apply`
  rethrows for exactly this reason (`Payload/PayloadEntry.cs:108-112`).
- `Diag.ResetHealth()` and `SelfHealing.ResetTests()` run before each generation applies so reloads do
  not duplicate entries; fire counts persist (`HotReload.cs:363-364`, `Harness/SelfHealing.cs:94-105`).
- Known trade-offs and the `BattleMode` stash gap: `HOTRELOAD.md:61-68`.

## Deploy = the release

`install.cmd` downloads from `<repo>/dist/` on branch `main` (`install.cmd:9,58-60`), so **pushing to
GitHub is releasing**. Never push mid-investigation (`CLAUDE.md:83-85`).

Build both, then place the **three** files in **both** destinations:

```
cd Harness  && dotnet build -c Release
cd ../Payload && dotnet build -c Release
```

| File | Game module | Repo |
|---|---|---|
| `BLTDeploymentCrashGuard.dll` (harness) | `<Game>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/` | `dist/` |
| `BLTDeploymentCrashGuard.Payload.dll` | same `bin/Win64_Shipping_Client/` | `dist/` |
| `SubModule.xml` | module root | `dist/` |

Then `md5sum` all three across build output, game module and `dist/` — they must match
(`CLAUDE.md:39-46`). Game bin:
`C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client`.

**While the game is running the harness DLL is locked.** Deploy the payload only — it hot-reloads via
shadow copy and logs `[HOTRELOAD] gen2 applied` (`HOTRELOAD.md:24-34`). Harness changes and load-time
fixes such as `MovementOrderTypeInitGuard` need a **fresh launch**, not a reload
(`CLAUDE.md:48-50`). `install.cmd` renames a locked DLL to `.prev` rather than failing
(`install.cmd:51-56`).
