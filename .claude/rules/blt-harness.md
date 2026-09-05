---
paths: ["Harness/**", "Payload/**/*.csproj", "Directory.Build.props", "SubModule.xml", "dist/**", "install.cmd", "share-log.cmd", "collect-diagnostics.cmd"]
---

# Harness, versioning and deployment — invariants

The harness (`Harness/` → `BLTDeploymentCrashGuard.dll`) is the only assembly Bannerlord loads
(`SubModule.xml:17-19`, `Harness/SubModule.cs:7-8`). It owns lifecycle, logging, config,
health/self-test and the reload engine; it must not depend on payload types — the payload pushes
data in instead (e.g. `Log.SetRoleTag`, `Harness/Log.cs:8-10,32-39`).

## One version source

`Directory.Build.props` `<Version>` is the single source of truth. MSBuild stamps both assemblies
from it, and the `StampSubModuleVersion` target `XmlPoke`s `SubModule.xml`'s `/Module/Version/@value`
to `v$(Version)` on every harness build (`Directory.Build.props:12-18`). The same target then
**copies the freshly stamped `SubModule.xml` into `dist/`** (`:19-24`) — before that, nothing ever
wrote `dist/SubModule.xml`, so a release could ship a stale one. `Diag.Version` reads the version back
off the assembly identity at runtime (`Harness/Diag.cs:14-31`). Never hardcode a version anywhere
else, including either `SubModule.xml` — a build overwrites both. Bump `<Version>` for a release.

## Log contract (`Harness/Log.cs`)

- **Never take the game down.** Every write is inside try/catch (`Log.cs:62-75`).
- **Rotation:** a segment rolls past `MaxLogBytes` = 8 MB into a rolling window of
  `MaxSegments` = 6 (`CrashGuard.log.1` … `.6`, ~48 MB), size re-checked every
  `RotateCheckEveryWrites` = 256 writes — not once per launch, which once let the file reach 283 MB
  (`Log.cs:13-15,78-120`). A window, not a single overwrite: a burst must not discard the evidence
  being chased.
- Every line is `timestamp [roleTag] message`; the role tag (`S`/`H`/`C`) is set by the payload
  (`Log.cs:32-39`, `Payload/PayloadEntry.cs:163-186`).
- The complementary half of the flood defence is `TraceThrottle` in the **payload**
  (`Payload/TraceThrottle.cs:14-17`) — deliberately not in the harness, because the harness DLL is
  locked while the game runs and a throttle fix must be able to hot-reload.
- `Log.Screen` is the on-screen channel; only for what a player must see (`Log.cs:122-131`).

## Diag and SelfHealing

- `MOD HEALTH:` is built only from components that called `Diag.Report`, and it prints a count, not a
  roster — names appear only for degraded entries (`Harness/Diag.cs:87-103`). When something is
  unresolved the line now appends *"(read each detail: a BannerlordTogether OR game update may have
  renamed a member; a detail saying 'inert', 'not loaded' or 'older game build' is on purpose)"*
  (`Diag.cs:93`). That suffix is guidance for reading a detail, not three literal strings to grep:
  `Diag.Report` discards `detail` on the healthy branch (`:71-85`), so no shipped stand-down text can
  ever appear in a degraded entry.
- `critical: true` escalates to an on-screen warning (`Diag.cs:71-99`) — the earned-only rule and the
  complete call-site list live in `.claude/rules/blt-payload-guards.md` § *`critical: true` is earned*.
- `Diag.ResetHealth()` and `SelfHealing.ResetTests()` run before each generation applies, so reloads
  do not duplicate entries; **fire counts persist** across generations
  (`Harness/SelfHealing.cs:44-57,97-105`).

## GuardConfig contract (`Harness/GuardConfig.cs`)

- The file is `<module root>/guardconfig.json`, resolved two levels above the DLL directory
  (`GuardConfig.cs:17-24`). It is **read with regex, not a JSON parser** — no JSON dependency — and
  cached for the whole session (`GuardConfig.cs:26-80`). A value that must be re-readable mid-session
  has to bypass the cache with its own fresh disk read; that is why the tracing flag has
  `FreshTracingFlag` in the payload (`Payload/PayloadEntry.cs:213-234`).
- `Bool(key, fallback)` and `String(key, fallback)` both fall back silently on any failure, so a
  malformed file degrades to defaults rather than crashing.
- **`DefaultJson` must list every key with its `_key` explanation string** and is written on first run
  so every knob is discoverable (`GuardConfig.cs:82-115`). Adding a config key means adding both lines
  there *and* a row in the README `## Config` table. Comment keys are plain JSON string members
  (`"_siegeCommandAll": "…"`), which is why the regex reader tolerates them.
- **It is now the only writer of that file.** `BattleMode` used to write a two-key stub of its own;
  that writer is gone, so a short `guardconfig.json` in a bug report is an *old* file, not a minimal
  config — every absent key silently takes its `DefaultJson` value.
- An explanation string must describe what the code does. `_noSickness` now says the guard "coexists
  with the third-party NoSickness mod (this guard only ever cures and never increments ill days, so
  that mod's own check sees a healthy hero and passes through)" (`GuardConfig.cs:94`); the earlier
  "stands down automatically" was not true of any code path. Correcting a string only affects fresh
  installs — the template is written only when the file is absent.

## Hot-reload engine contract (`Harness/HotReload.cs`)

- **Hard gate:** watching activates only when `hotReload=true` **and** a `.hotreload-dev` marker file
  exists in the module root — runtime code loading must be impossible on a player install
  (`HotReload.cs:26-28,69-70`).
- Every generation loads via `Assembly.LoadFrom` on a per-attempt shadow copy placed in the same
  directory as the canonical DLL (`HotReload.cs:276-314`). LoadFrom-context binding is required so the
  payload binds the harness's already-loaded `0Harmony 2.3.6.0`; a byte load probes the app base,
  binds the game's own `0Harmony 2.4.2.0` and splits assembly identity — `AssemblyResolve` never
  fires, because probing succeeds (`HotReload.cs:281-287`). The sibling split, where the binder
  loads a *second copy of the harness*, is what the `ResolveFromLoadedAssemblies` redirect prevents
  (`HotReload.cs:56-63`).
- Each payload build compiles under a **unique assembly name**
  (`BLTDeploymentCrashGuard.Payload.b<stamp>`, published to the fixed file name by the
  `PublishFixedPayloadName` target) because the LoadFrom context dedups simple-named assemblies by
  name only — a unique version alone gets collapsed
  (`Payload/BLTDeploymentCrashGuard.Payload.csproj:9-24,94-97`).
- Apply order: new generation applies **first**; only on success does the engine swap `_gen` and
  `UnpatchAll` the previous owner id `bltogether.crashguard.gen{N}`. A failed apply keeps the previous
  generation, so the game is never left unpatched (`HotReload.cs:13-16,358-393`). `PayloadEntry.Apply`
  rethrows for exactly this reason (`Payload/PayloadEntry.cs:110-114`).
- Known trade-offs and the `BattleMode` stash gap: `HOTRELOAD.md` § *Trade-offs and known gaps*.

## Release = `tools/release.sh`

The full checklist is `docs/RELEASE.md`; this is the contract it enforces. **One build** produces the
three shipped files, each of which goes to **two destinations**:

| File | Game module | Repo |
|---|---|---|
| `BLTDeploymentCrashGuard.dll` (harness) | `<Game>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/` | `dist/` |
| `BLTDeploymentCrashGuard.Payload.dll` | same `bin/Win64_Shipping_Client/` | `dist/` |
| `SubModule.xml` | module root | `dist/` |

```bash
tools/release.sh              # build both, deploy, manifest, verify
tools/release.sh --no-build   # deploy + verify from the existing build output
BANNERLORD_DIR="…" tools/release.sh
```

It reads `<Version>` from `Directory.Build.props`, refuses to continue unless the repo `SubModule.xml`
is stamped `v<Version>` (only a harness build re-stamps it, so a version bump needs a full run, not
`--no-build`), copies into the module — a file the running game holds open is reported `LOCKED (game
running?)` and left alone — then into `dist/`, writes `dist/manifest.txt` (`version=<Version>` then one
`<sha256>  <file>` line per file), and verifies every SHA256 matches across build output, `dist/` and
the module. "release-ready" means only that; anything else prints `NOT release-ready` and exits
non-zero (`tools/release.sh:8-13,28-78`). Do not push on a non-zero exit.

`install.cmd` downloads from `<repo>/dist/` on branch `main` (`install.cmd:9,58-60`), so **pushing
`dist/` is releasing**. Never push mid-investigation (`CLAUDE.md` § *Working discipline*). The
half-updated-`dist/` hole is closed at both ends: after downloading the three files `install.cmd`
fetches `dist/manifest.txt` and re-verifies each SHA256 with `certutil`, refusing a mismatched set
("The release may be mid-update on GitHub. Run this again in a minute") and skipping the check with a
notice if there is no manifest or no `certutil` (`install.cmd:67-90,108-112`). That is the
harness↔payload pairing check the old md5-by-hand step never had.

`install.cmd`, `share-log.cmd` and `collect-diagnostics.cmd` are served live from the repo root of
`main` — release artifacts, not tooling — and each carries its own copy of the Steam-library search
list (`install.cmd:17-29`, `share-log.cmd:13-25`, `collect-diagnostics.cmd:22-34`). All three now
carry the same 11 entries, and `tools/lint-scripts.sh` fails if they diverge again or if `install.cmd`
does not both download and verify every file listed in `dist/manifest.txt`. Run it after touching any
of the three and before every release. Edit all three together.

**While the game is running the harness DLL is locked.** Deploy the payload only — it hot-reloads via
shadow copy and logs `[HOTRELOAD] gen2 applied` (`HOTRELOAD.md` § *A) Build-and-drop (default,
bulletproof, zero extra deps)*). Harness changes and load-time fixes need a **fresh launch**, not a
reload; the four cases are `HOTRELOAD.md` § *What a reload cannot do (fresh launch required)*.
`install.cmd` renames a locked DLL to `.prev` rather than failing (`install.cmd:51-56`).
