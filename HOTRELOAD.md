# Hot-reload dev workflow (no game restart)

The mod is split into two assemblies:

- **Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — the stable module Bannerlord loads.
  Owns the game lifecycle + the reload engine. Changing it still needs a restart (rare).
- **Payload** (`Payload/` → `BLTDeploymentCrashGuard.Payload.dll`) — all guards/fixes/tracers.
  Hot-reloadable. This is where ~all iteration happens.

In build-and-drop mode a generation loads via `Assembly.LoadFrom` on a shadow copy written per
reload **attempt**, not per generation (`Harness/HotReload.cs:294,307-314`). LoadFrom-context
binding is required — byte-loading binds 0Harmony to the wrong copy via app-base probing — but a
byte load (`Assembly.Load(bytes)`, `HotReload.cs:331-339`) remains the fallback when the shadow
load fails or dedups, and it is the *only* path under `"hotReloadRoslyn": true` **on a harness built
with `-p:Roslyn=true`** (see *Mode (B) is not a superset of mode (A)* below).

Each payload build compiles under a unique assembly NAME (`BLTDeploymentCrashGuard.Payload.b<stamp>`,
published under the fixed file name) because the LoadFrom context dedups simple-named assemblies by
name only — a unique version alone is collapsed (field-proven 2026-09-01). Every generation gets
fresh statics and a per-generation Harmony owner id (`bltogether.crashguard.gen{N}`); the new
generation is applied first, then the previous generation is `UnpatchAll`'d — a failed reload keeps
the previous generation, so the game is never left unpatched.

Three consequences of that design that are easy to trip over:

- **`InternalsVisibleTo` can never cover the payload.** It matches by exact assembly name, and the
  payload's name changes every build (`Payload/BLTDeploymentCrashGuard.Payload.csproj:23`), so the
  shared harness surface (`Log`, `Diag`, `GuardConfig`, `SelfHealing`, `ISharedState`) is **public**
  on purpose. Do not "tidy" it back to internal. A vestigial
  `[assembly: InternalsVisibleTo("BLTDeploymentCrashGuard.Payload")]` is still in the tree at
  `Harness/AssemblyInfo.cs:9`, under a lead comment (`:3-5`) that its own next lines correct — it is
  inert, and its presence is not evidence the internal path works.
- **An `AssemblyResolve` hook cannot fix a bad load context**, because the hook only runs when
  probing *fails*. Under byte-load, default-context probing *succeeds* against the game's own
  0Harmony — so the load context, not a resolver pin, is the fix.
- **A dedup that slips through is detected, not ignored**: the engine compares the loaded assembly's
  `Location` with the shadow path it asked for and, on a mismatch, logs
  `[HOTRELOAD] LoadFrom deduped to already-loaded …` and falls back to a byte load rather than
  silently re-applying the previous generation's code.

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

**Mode (B) is not a superset of mode (A).** The config key alone does not switch paths: `_useRoslyn`
is `hotReload && hotReloadRoslyn && PayloadCompiler.CompiledIn` (`Harness/HotReload.cs:71`), and
`CompiledIn` is a build-time constant, true only under `-p:Roslyn=true`
(`Harness/PayloadCompiler.cs:27-36`, `Harness/BLTDeploymentCrashGuard.csproj:17-18`) — on a stock
harness the key is inert and mode (A) still applies. Once `_useRoslyn` is true the engine skips the
shadow-copy `LoadFrom` branch entirely (it is guarded by `!_useRoslyn`, `Harness/HotReload.cs:294`)
and byte-loads the compiled bytes — and when the Roslyn compile fails, the prebuilt-DLL *fallback is
byte-loaded too*.
Byte-loading is the path that was field-proven on 2026-08-30 to bind the game's own
`0Harmony 2.4.2.0` from the app base and produce `Method 'Apply' in PayloadEntry does not have an
implementation`; no resolver pin prevents it, because probing succeeds. If mode (B) misbehaves, set
`"hotReloadRoslyn": false` and **restart** — do not rely on the in-session fallback being
equivalent to (A).

## What a reload resets, and what survives

Before each generation applies, the engine clears the health list (`Diag.ResetHealth`) and the
self-test registry (`SelfHealing.ResetTests`), so each `[HOTRELOAD] genN applied` line carries its
own fresh `MOD HEALTH:` summary instead of accumulating duplicates.

**Survives** (harness-side — Bannerlord loads the harness exactly once):

- Guard fire counts. `GUARD ACTIVITY:` keeps counting across generations, which is also the proof
  that harness-owned state survived the swap. They are deliberately not reset.
- `Diag.SessionId`, the log path and the role tag (`Log` statics).
- The `ISharedState` bag, created once by the reload engine and passed into every generation's
  `Apply` — the home for state a payload owns but must not lose.

**Reset** (every payload static is fresh per generation — that freshness is what makes reload
clean):

- Every `_applied` latch — the guards simply re-apply under the new owner id.
- Rate/limit state: the encounter-loop breaker's trip flag and its recent-call ring buffer; the
  background-tick guard's block window, worst-ms and throttled-call counters. **Do not reload in the
  middle of reproducing a rate-based bug — you erase the state you are measuring.**
- One-shot log latches: the illness/old-age guard's `[NOSICK] blocking the daily old-age/illness
  death roll` line prints again (`Payload/IllnessDeathGuard.cs:91`); shared time control will try to
  grant again (`_grantedLogged`), the `[CLICK-SPEED]` / `[TIME-FLOW]` once-only lines reappear and
  the join-escape arm window restarts; the siege take-over's once-per-mission screen note can appear
  a second time in one battle, and its refused-hand-off / stopped-shuffle counters restart at zero.
  Those counters are per **battle** anyway — `SiegeCommandGuard.OnMissionInit` zeroes them
  (`Payload/SiegeCommandGuard.cs:157-162`, called from `Payload/PayloadEntry.cs:140`) and the line
  itself reads "this battle: …" (`:520`); a reload just zeroes them mid-battle as well.
  `CoopCommandSplit` re-resolves both players' parties and re-announces the I–IV / V–VIII
  split, and the BT release hooks are re-scanned with their one retry. `BattleMode`'s once-per-target
  unresolved-lift-target warnings reprint too (`WarnedUnresolved`, `Payload/BattleMode.cs:91,347,364`)
  — a repeated `[BATTLE-MODE] lift target … not found` after a reload is the latch resetting, not a
  new resolution failure.
- In-flight deferred work. A reload while `ClanPartyCreationAdvisor` is waiting for a new clan party
  to settle silently drops the pending troop-screen open — the pending-timeout path never runs,
  because the state it would have timed out is gone. Reproduce deferred behaviour *after* a reload,
  not across one.
- `PeerDetection`'s cached `CoopSession` type lookup, **including a negative result**, is per
  generation. The `OnBeforeInitialModuleScreen` retries re-apply the BT-dependent guards but do not
  clear that latch, so if BT's assembly turned up after the first probe, a payload reload is the
  fastest way to make peer detection see it without restarting the game.

## Config across a reload

The harness `GuardConfig` caches the whole file for the session behind a `_loaded` latch, so a knob
read through it is a **launch-time snapshot** and cannot be changed by a reload. Two exceptions
matter in practice:

- `tracing` is re-read **fresh from disk on every apply** (`PayloadEntry.FreshTracingFlag`), exactly
  so that editing `guardconfig.json` + dropping a rebuilt payload turns the `[TRACE]` / `[TIME]`
  tracers on mid-session without losing a live repro. It gates nothing but the tracers: the
  encounter-loop breaker hooks `PlayerEncounter.Finish` itself, always-on
  (`Payload/EncounterLoopGuard.cs:70-73,171-174`), so its Finish stamp is live with `tracing=false`;
  the tracer's own `Finish` hook is log-only (`Payload/TracePatches.cs:188-191`).
- Knobs the payload reads into its own statics (e.g. `timeAlwaysFlows`, `shareTimeControl`) are
  re-read simply because those statics are recreated — a reload picks up the edited value **on the
  build-and-drop (LoadFrom) path**. Both of those readers derive the config path from the *payload*
  assembly's `Location` (`Payload/TimeFlowPatch.cs:85-86`, `Payload/ShareTimeControl.cs:194-195`),
  which is empty for a byte-loaded generation, so under mode (B) or a byte-load fallback the read
  falls into its empty catch and returns the hard-coded default rather than the edited value.

Any *new* knob that must flip mid-session has to read from `GuardConfig.Path` itself, the way
`FreshTracingFlag()` does; say so in its `_<key>` doc string.

## Diagnostics across a reload

- The session-wide first-chance exception observer is armed once per **AppDomain**, not per
  generation: `CharacterCreationTrace` checks the `BLTCG_FirstChanceArmed` AppDomain slot before
  subscribing. Statics reset on reload, so an assembly-level flag would let every reload stack
  another handler and log each exception N times. The 400-event cap, by contrast, lives in a
  per-generation static — a reload resets it.
- `TraceThrottle` lives in the payload precisely because the harness DLL is locked while the game
  runs; its counters are per generation, so a reload starts with clean `[repeat]` runs.
- `CoopBattleTrace` latches on `_applied` and both co-op tracers bind BT at `Apply` — a reload is
  how you pick BT up if it loaded late.
- Tracer load lines are re-printed per generation; check them again after a reload before trusting
  a trace (`docs/DIAGNOSTICS.md` § tracer load lines).

## What a reload cannot do (fresh launch required)

- **Harness changes.** Its DLL is locked while the game runs — `tools/release.sh` reports that copy
  as `LOCKED (game running?)` rather than failing (see *Build both for deployment*).
- **`MovementOrderTypeInitGuard`** — a load-time fix cannot be re-taken on a type the CLR has
  already prepared. It runs from `ApplyEarly`, first in `Apply` and before `PatchAll`
  (`Payload/PayloadEntry.cs:42,45`), and its own header states the consequence: "Load-time fix:
  takes effect on a fresh launch, not on a hot-reload" (`Payload/MovementOrderTypeInitGuard.cs:42`).
  A reload does re-run `ApplyEarly`, but the forced `RunClassConstructor`
  (`Payload/MovementOrderTypeInitGuard.cs:76`) cannot re-initialize a type whose initializer has
  already run — good or poisoned, that outcome is fixed for the process.
- **`ClientBootstrapFix`** — it only installs a prefix on BT's verify method
  (`Payload/ClientBootstrapFix.cs:23`); BT runs that verification **once** per process and latches
  `_harmonyPatchBootstrapAttempted=true`, which permanently blocks retry
  (`Payload/ClientBootstrapFix.cs:16-17`), so a reload after the abort changes nothing.
- **`ClanModeSoloFix`** — a transpiler cannot un-inline callers that were already jitted, so a
  mid-session reload may leave BT's clan-mode getter reading the original value. The class header
  says the same: it "applies at module load, before any campaign code compiles"
  (`Payload/ClanModeSoloFix.cs:26-28`).

Each of these is idempotent and latched, and `PayloadEntry` re-applies `ClientBootstrapFix` and
`ClanModeSoloFix` at the module screen for a late-loading BT assembly
(`Payload/PayloadEntry.cs:119-120`) — a *re-apply* is not the same as a load-time fix taking effect,
so a reload still does not deliver a change to any of them.

## Build both for deployment

**`tools/release.sh` is the deploy step.** It builds both assemblies from one run and copies all
three shipped files — `BLTDeploymentCrashGuard.dll` (harness) and
`BLTDeploymentCrashGuard.Payload.dll` (payload) into
`Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/`, and `SubModule.xml` into the module
root — then into `dist/`, writes `dist/manifest.txt`, and verifies the SHA256 of every file matches
across build output, `dist/` and the game module. `SubModule.xml` still points at the harness; the
harness loads the payload itself.

```
tools/release.sh              # build both, deploy, manifest, verify
tools/release.sh --no-build   # deploy + manifest + verify from the existing build output
```

Do not assemble a deployment by hand — the point of the script is that the harness and payload in
`dist/` provably came from the same build. The full checklist around it is `docs/RELEASE.md`
§ *2. Run `tools/release.sh`*; this document does not repeat it.

**With the game running the script cannot finish, by design.** A file the game holds open is
reported `LOCKED (game running?): … — left as is` and skipped rather than failing the copy; the hash
check then sees a stale copy in the game module, prints `NOT release-ready`, adds *"The game is
running: harness/SubModule copies were skipped. Close the game and re-run with `--no-build`"*, and
exits non-zero. Treat that as the correct answer, not something to work around: a release run needs
the game closed, and a fresh launch is required anyway (§ *What a reload cannot do (fresh launch
required)*).

For **payload-only iteration** with the game running, do not use the script — copy the payload DLL
by hand as in *A) Build-and-drop* above. That works because the engine never holds a lock on the
file you drop — it `LoadFrom`s a per-attempt shadow copy, and the byte-load fallback reads the
bytes and loads those — so the canonical payload DLL stays writable during a session (see the
intro).

## Trade-offs and known gaps

- ~1–3 MB leaked per reload (old assembly can't unload in .NET FW); restart every few dozen reloads.
- Every reload **attempt** (not every successful generation) writes a new shadow DLL next to the
  payload, and `LoadFrom` locks it for the process lifetime. The cleanup pass runs only on the first
  load of a launch, so a long dev session leaves one file per attempt in
  `bin/Win64_Shipping_Client/` until the next launch sweeps them.
- If the new generation applies but unpatching the **old** owner id throws, the engine logs
  `[HOTRELOAD] unpatch of bltogether.crashguard.genN failed: …` and carries on — both generations'
  patches are then live at once. Treat that line as "restart before you trust anything you observe".
- Harness changes need a restart.
- Known Phase-B gap: `BattleMode`'s foreign-patch stash does not yet survive a reload — reloading
  while in `battleMode=solo` (vanilla, BT battle patches lifted) can leave them lifted. The stash is
  a payload static (`Payload/BattleMode.cs:75`), and every payload static is fresh per generation;
  state that must *survive* a reload belongs in the harness `ISharedState` bag instead. Note the
  `ISharedState` doc comment (`Harness/Contracts.cs:24-28`) already lists "BattleMode's
  foreign-patch stash" among what the bag holds; the code above is the authority, and the stash is
  not in the bag today. Until it moves: iterate with `"battleMode": "coop"` (nothing is lifted, so
  nothing can be lost), or restart after a reload done in vanilla mode. Reloading in
  `battleMode=coop` is unaffected.
- Known gap of the same family: `PregnancySyncGuard`'s host birth listener is subscribed in
  `OnGameStart` (campaign events are per-`Campaign`), and a reload calls only `Apply` — the harness
  invokes `OnGameStart` solely from `SubModule.OnGameStart`. Reloading mid-campaign therefore leaves
  the **previous** generation's `OnGivenBirthEvent` listener attached (Harmony's `UnpatchAll` does
  not remove campaign event listeners) while the new generation never subscribes for that campaign.
  Load a campaign after the reload — or restart — before trusting birth sync in a dev session.
