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

Three consequences of that design that are easy to trip over:

- **`InternalsVisibleTo` can never cover the payload.** It matches by exact assembly name, and the
  payload's name changes every build, so the shared harness surface (`Log`, `Diag`, `GuardConfig`,
  `SelfHealing`, `ISharedState`) is **public** on purpose. Do not "tidy" it back to internal.
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

**Mode (B) is not a superset of mode (A).** With `"hotReloadRoslyn": true` the engine skips the
shadow-copy `LoadFrom` branch entirely (the branch is guarded by `!_useRoslyn`) and byte-loads the
compiled bytes — and when the Roslyn compile fails, the prebuilt-DLL *fallback is byte-loaded too*.
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
- One-shot log latches: the illness/old-age guard's "blocking the daily death roll" line prints
  again; the siege take-over's once-per-mission screen note can appear a second time in one battle,
  and its refused-hand-off / stopped-shuffle counters restart at zero (they are per generation, not
  per battle). `CoopCommandSplit` re-resolves both players' parties and re-announces the I–IV / V–VIII
  split, and the BT release hooks are re-scanned with their one retry.
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
  so that editing `guardconfig.json` + dropping a rebuilt payload turns the tracers on mid-session
  without losing a live repro. That is also what makes the encounter-loop breaker's Finish stamp and
  the `[TIME]` tracer go live mid-session.
- Knobs the payload reads into its own statics (e.g. `timeAlwaysFlows`, `shareTimeControl`) are
  re-read simply because those statics are recreated — a reload picks up the edited value.

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

- **Harness changes.** Its DLL is locked while the game runs.
- **`MovementOrderTypeInitGuard`** — a load-time fix cannot be re-taken on a type the CLR has
  already prepared.
- **`ClientBootstrapFix`** — it only installs its prefix; BannerlordTogether verifies its action
  cache **once** per process, so a reload after the abort changes nothing.
- **`ClanModeSoloFix`** — a transpiler cannot un-inline callers that were already jitted, so a
  mid-session reload may leave BT's clan-mode getter reading the original value.

## Build both for deployment

```
cd Harness && dotnet build -c Release
cd ..\Payload && dotnet build -c Release
```

Deploy BOTH DLLs to `Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/`:
`BLTDeploymentCrashGuard.dll` (harness) and `BLTDeploymentCrashGuard.Payload.dll` (payload).
SubModule.xml still points at the harness; the harness loads the payload itself.

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
  a payload static, and every payload static is fresh per generation; state that must *survive* a
  reload belongs in the harness `ISharedState` bag instead. Until it moves: iterate with
  `"battleMode": "coop"` (nothing is lifted, so nothing can be lost), or restart after a reload done
  in vanilla mode. Reloading in `battleMode=coop` is unaffected.
- Known gap of the same family: `PregnancySyncGuard`'s host birth listener is subscribed in
  `OnGameStart` (campaign events are per-`Campaign`), and a reload calls only `Apply` — the harness
  invokes `OnGameStart` solely from `SubModule.OnGameStart`. Reloading mid-campaign therefore leaves
  the **previous** generation's `OnGivenBirthEvent` listener attached (Harmony's `UnpatchAll` does
  not remove campaign event listeners) while the new generation never subscribes for that campaign.
  Load a campaign after the reload — or restart — before trusting birth sync in a dev session.
