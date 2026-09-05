# Diagnostics playbook

How to investigate a crash, freeze, or wrong behaviour in this mod without guessing. The rule
this whole file exists to enforce: **prove the root cause from IL and logs before changing code.**
The location where a symptom manifests is almost never the root-cause location — find both.

Two investigation surfaces: **static** (read the installed assemblies) and **runtime** (turn on
tracing and read `CrashGuard.log`).

---

## 0. Crash triage checklist (start here)

Work down the list; do not skip to a fix.

1. **Collect the evidence.** `collect-diagnostics.cmd` bundles the whole set into one zip and
   uploads it — nothing on this list needs attaching by hand:

   | In the bundle | Source | Where |
   |---|---|---|
   | `CrashGuard.log` **and every rotated segment `.1 … .6`** | module folder | `collect-diagnostics.cmd:54-55` |
   | `guardconfig.json`, `hero-identity.json`, `SubModule.xml` | module folder | `:56-58` |
   | `bt-sync-host/client/solo.txt` | Desktop | `:59` |
   | 3 newest `rgl_log_*`, 3 newest `rgl_log_errors_*`, 2 newest `watchdog_log_*`, newest `launcher_log_*` | `%ProgramData%\Mount and Blade II Bannerlord\logs` | `:61-64` |
   | newest game crash folder, **text files only** (the dumps are too large to upload) | `%ProgramData%\Mount and Blade II Bannerlord\crashes` | `:67-71` |
   | newest `*crash*.html` | `%USERPROFILE%\Documents` | `:75-77` |

   `guardconfig.json` and `SubModule.xml` are part of the evidence, not padding: they say which
   config produced the log and which version wrote it. What the bundle still does **not** carry:
   the memory dump inside the crash folder, and any segment already rotated out past `.6`.
2. **Identify the build that produced the log.** The banner line carries the mod version, harness
   build time and session id; `[HOTRELOAD] genN applied` says which payload generation was live at
   the moment of the crash (health and tracer lines are re-printed per generation).
3. **Read the mod's own verdict before the exception.** `MOD HEALTH:`, `[SELFTEST]`,
   `[BATTLE-MODE]` — but see § *What `MOD HEALTH:` does not cover* below: absence is not health.
4. **Turn tracing on and reproduce** (§2). Then check the tracer load lines: a tracer that hooked
   0 methods produces silence that is easy to misread as "the bug did not happen".
5. **Find the first-chance exception, not the last log line** (§2): inner-exception chain,
   `CONTEXT:`, and the `LIVE …` frames that name the trigger.
6. **Ask whether the throw is a re-throw.** A failed type initializer is cached; if a constructor
   probe never fired yet the crash still happens, the type was poisoned earlier — look at load time.
7. **Locate it in IL** (§1): `NameSearch` → `Inspect` → `IlDump` → `Callers`, until the null-deref,
   ordering or type-init fact is proven in the installed assembly.
8. **Name both locations** — the manifested frame and the root cause — before editing anything.
9. **Fix at the root, then prove it from the log again**: the guard's own tag fires, `MOD HEALTH:`
   lists it, and the crash no longer reproduces on a clean launch.

### Branch: freeze / hang (nothing throws)

A hang produces no exception, so **every piece of exception tooling in this repo is silent by
construction** — the session-wide first-chance capture only fires on a throw, the finalizer guards
only run on an escaping exception, and `GUARD ACTIVITY:` stays at "none fired this session". That
silence is the expected output of a hang, not evidence of health. Do not read it as "the mod was
fine".

1. **Read the `[DIAG]` heartbeat.** It is emitted on a timer regardless of whether anything throws,
   so it is the one signal a hang still produces. Compare consecutive lines for build-up in
   `WS=` / `priv=` / `managed=`, in `handles=` and in `threads=`; the `Mission=` / `GameState=` /
   `Campaign=` fields say what the engine believed it was doing. **The last heartbeat is the time of
   death** — everything after it is post-mortem.
2. **Read the last `[TRACE]` and `[BATTLE-MODE]` lines before the heartbeat stops.** They are the
   last decision the mod made, and they name the chokepoint it was standing on.
3. **Attach a debugger to the live process.** A hang is the one symptom you can still inspect
   directly — the game has not exited.
   - Visual Studio → **Debug > Attach to Process** → `Bannerlord.exe`, attach to **Managed** code,
     then **Break All** (pause), **Debug > Windows > Threads**, and read the main thread's managed
     call stack. Sample it several times a few seconds apart: a stack that does not move between
     samples is a deadlock or a blocking wait; a stack that churns inside the same few frames is a
     spin.
   - No IDE: `dotnet-dump collect -p <pid>`, then `dotnet-dump analyze <dump>` and `clrstack`
     (`setthread <n>` to walk the other threads).
4. **Do not kill the game to "get the log".** The log is already on disk and flushed; killing the
   process throws away the live stack, which is the only evidence a hang produces.

### Branch: log-only triage (someone else's log, no reproduction)

What a log alone **can** settle:

- **Which build produced it** — the banner line (mod version, harness build time, session id) and
  `payload build <time> applying on <harmony id>` (which payload generation was live).
- **Whether the mod resolved its targets on that machine** — `MOD HEALTH:` plus the `[SELFTEST]`
  block. A `FAIL` names the component; a resolution failure names the member that moved.
- **Which guards actually fired** — `GUARD ACTIVITY:`, with a count per guard id. A non-zero count
  is proof a guarded path was hit on their machine.
- **What was thrown, and by whom** — the first-chance block (§2): exception type, the full
  `<- INNER:` chain, the `CONTEXT:` engine state and the `LIVE …` trigger stack.
- **What the mod decided** — the last `[BATTLE-MODE]`, `[TIME]`, `[ENCOUNTER-GUARD]` lines.

What it **cannot** settle: a **root cause on a build you do not have**. Member names, IL offsets and
patch counts are per-build facts; a count that looks wrong may be a different game or BT version
rather than a bug. Pin the build before theorising — `VerCheck.exe <dll>` prints assembly identity
and nothing else (it reads no game path and installs no resolver,
`tools/il-probes/VerCheck/VerCheck.cs:1-5`), so it runs against any DLL, including one a reporter
sends. Then ask for `collect-diagnostics.cmd` (step 1) rather than guessing from the fragment you
were pasted.

---

## 1. Static analysis — read the installed game

Use the IL probes in `../tools/il-probes/` (build once, see that folder's README). Typical flow:

1. `NameSearch.exe <dll> <term>` — locate the type/method when you only know a concept.
2. `Inspect.exe <dll> <FullTypeName>` — list its members and signatures.
3. `IlDump.exe <dll> "<Type>::<Method>"` — disassemble to IL; this proves control flow, the exact
   field/property a line touches, and where a null-deref can occur. Supports `.cctor` and `.ctor`.
4. `Callers.exe <dll> <member>` — find who calls a member (ordering, trigger paths).

What to look for:

- **Null-deref sites**: a `callvirt`/`ldfld` on a value produced by a getter that can return null.
- **Type-init fragility**: a `beforefieldinit` type (check `Type.Attributes`) whose `.cctor`
  depends on runtime state (e.g. `Mission.Current`) — the runtime can run it at an unsafe time.
  See `ENGINE-NOTES.md` § MovementOrder.
- **Ordering**: whether field X is set before method Y runs — dump the caller (`Callers.exe`) and
  read the call sequence.

Confirming a type is a struct / beforefieldinit (no probe needed, quick dotnet):

```csharp
var t = asm.GetType("TaleWorlds.MountAndBlade.MovementOrder");
t.IsValueType;                                            // struct?
(t.Attributes & TypeAttributes.BeforeFieldInit) != 0;    // lazy cctor timing?
```

---

## 2. Runtime tracing — read CrashGuard.log

Turn tracing on in `guardconfig.json` (module root):

```json
{ "tracing": true, "selfTest": true }
```

With hot-reload on, flipping `tracing` and dropping a rebuilt payload turns tracers on mid-session
(the flag is read fresh from disk on each apply). Log lives at
`Modules/BLTDeploymentCrashGuard/CrashGuard.log`.

### Log tags (grep targets)

Startup/health: `MOD HEALTH`, `[SELFTEST]`, `[HOTRELOAD]`, `[BATTLE-MODE]`.
Fixes each own a tag — `[SIEGE-CMD]`, `[COOP-CMD]`, `[IDENTITY]`, `[TIME-GUARD]`, `[GATE]`, etc.
Diagnostics added in the 2026-09-04 investigation:

| Tag | What it gives you |
|---|---|
| `[CHARGEN]` | Character-creation lifecycle + a **session-wide first-chance exception capture** with the full inner-exception chain and the throwing frames. |
| `[MO-PROBE]` | (dev) logs the **first 12** `MovementOrder` constructions + `Mission.Current` state, and any throw out of the ctor at the instant it is thrown (`Payload/MovementOrderInitProbe.cs:27,56,73-92`). After the 12th it is silent except on a throw — an origin probe for the type-init crash, not a census. |
| `[MO-INIT]` | The `MovementOrder` type-init guard's result at load: "initialized safely" or "already poisoned". |
| `[DIAG]` | Memory + engine-state heartbeat (WS/private/managed, GC counts, handles, threads; Mission/GameState/Campaign) every ~15 s and at every mission transition. Use it to see a leak/balloon build up before a symptom — and it is the only signal a freeze still produces (§0 *Branch: freeze / hang*). |
| `[DEPLOY-GUARD]` | The two deployment finalizers (README fix #1) and their health check. `deployment crash guards active — SetupTeams=guarded FinishDeployment=guarded` at load (`Payload/DeploymentCrashGuards.cs:43`), `SUPPRESSED crash in DeploymentMissionController.SetupTeams: …` when one fires (`:107,128`), and one line per recovery step that itself failed (`:144-159`). Every line in that file now carries the tag; before 2026-09-04 they were untagged and ungreppable. |
| `[TIME] … change SUPPRESSED/ALTERED by [X]` | Names **which** of our three prefixes on `Campaign.set_TimeControlMode` vetoed a write — `[TIME-GUARD]`, `[CLICK-SPEED]`, or `another patch (not one of ours)` when the vetoer was not ours (`Payload/TimeTrace.cs:118-124`). The vetoing prefix notes itself (`TimeEnforcementGuard.cs:217`, `MapClickSpeedKeeper.cs:93`) and the `[repeat]` dedup key includes the vetoer (`TimeTrace.cs:127-128`), so a collapsed burst still says who won. |
| `[repeat] … ×N` | A high-frequency line coalesced by `TraceThrottle` (see below). |

### Tracer load lines — a tracer's health report

Tracers do not call `Diag.Report`; their load line **is** their health report, printed once per
payload generation from `Apply`. Read it before trusting anything a tracer did or did not log:

| Line | Meaning |
|---|---|
| `[CHARGEN] character-creation tracer active on N method(s); …` | hooked N methods; the same line states whether the session-wide first-chance capture armed. |
| `[CONTROL] control tracer active on N method(s)` | as above for input/control (`ControlTrace.cs:45`). |
| `[COOP-BATTLE] battle-formation tracer active on N method(s)` | BT battle-command tracing is live. |
| `[TIME] time-control tracer active on N method(s)` | the `Campaign.set_TimeControlMode` / lock tracer. |
| `[TIME-GUARD] shared-pause tracer active on N method(s)` | the shared-pause observation hooks. |
| `[TRACE] tracer active on N method overload(s)` | the generic tracer in `TracePatches.cs`. |
| `[ROLE] role-transition tracer active (LoadSaveGameData hooks=N)` | co-op role across save loads (`Payload/RoleTrace.cs:61`). |
| `[MO-PROBE] MovementOrder ctor origin probe active (logs first N constructions + any throw)` | the dev origin probe from the tag table above (`Payload/MovementOrderInitProbe.cs:44`). |
| `[<TAG>] type not found: X` | the type could not be resolved by name — a game/BT rename, nothing was hooked. |
| `[<TAG>] no patchable method T.M` | the type resolved, the method did not. |

**N = 0 (or a `type not found` line) means the trace is empty by construction.** With by-name
reflection everywhere, a silent hook miss is indistinguishable from "the bug did not happen", so a
tracer that prints no count is a tracer you cannot reason from — fix the count first.

Two tracers can print **no load line at all**, so their absence is ambiguous rather than a count of
zero:

- `RoleTrace.Apply` returns before logging anything when `PeerDetection.FindCoopType("CoopSession")`
  is null (`Payload/RoleTrace.cs:39-42`) — a missing `[ROLE] … active` line means *either* BT is not
  loaded *or* BT renamed `CoopSession`, and the log cannot tell you which.
- `CoopBattleTrace` prints its `active on N method(s)` line only inside `if (n > 0)`
  (`Payload/CoopBattleTrace.cs:43-46`); with nothing hooked you get only per-type
  `[COOP-BATTLE] type not found: X` lines (`:63`), never an `N = 0` count. Do not wait for one.

### What `MOD HEALTH:` does not cover

`MOD HEALTH:` is built from the components that called `Diag.Report`. It is printed from
`PayloadEntry.Apply` (`Payload/PayloadEntry.cs:102`, with `[SELFTEST]` following when
`"selfTest": true`) **and again inside the `[HOTRELOAD] genN applied` line**
(`Harness/HotReload.cs:381`) — so a reloaded generation produces two copies and a fresh launch one;
do not count `MOD HEALTH:` lines as generations. A component that never reports is **absent**, not
healthy — and several shipped ones never report:

| Component | What it reports | Where it does show up |
|---|---|---|
| `Payload/DeploymentCrashGuards.cs` (fix #1: `SetupTeams`, `FinishDeployment` finalizers) | nothing — attribute classes applied by `harmony.PatchAll(typeof(PayloadEntry).Assembly)`, with no `Apply`, no `Diag.Report`, no `SelfHealing.RegisterTest` and untagged log lines | `GUARD ACTIVITY:` — `setup-teams-guard`, `finish-deployment-guard` (`SelfHealing.RecordFire`), plus the `SUPPRESSED crash in …` line |
| `BattleMode` / `PeerDetection`, `PayloadEntry` | no health, no self-test; nothing pins the `BattleTargets` member list, and `EnumerateTargets` skips an unresolvable type with a bare `continue` | `[BATTLE-MODE]` lines and the patch counts they carry |
| `PlayerIdentityGuard`, `BootstrapWatch` | nothing at all (no report, no self-test, no `RecordFire`) | their own tags only, e.g. `[IDENTITY]` |
| `ClientHeroCreationGuard` | `RecordFire("hero-creation-guard")` only | `GUARD ACTIVITY:` |
| `StealthHideoutAdvisor` | returns silently when `HideoutAmbushMissionController` is missing (older game build) | nothing — it is simply absent |

Practical consequences when reading a log:

- A fix missing from `MOD HEALTH:` may still be loaded. Check `GUARD ACTIVITY:` and the fix's own
  tag before concluding it did not apply. `GUARD ACTIVITY:` is itself throttled — at most one line
  per 120 s, and only reprinted when the summary text changes (`Payload/PayloadEntry.cs:189-202`) —
  so *its* absence is not evidence either; grep the tag and `SUPPRESSED crash in` too.
- A BT or game rename under `BattleTargets` produces **fewer patched methods, not a degraded
  component** — compare the `[BATTLE-MODE]` counts against a known-good log rather than trusting
  the health line.
- `GUARD ACTIVITY:` counts accumulate across hot-reload generations while `MOD HEALTH:` is reset per
  generation (`HOTRELOAD.md`), so the two lines answer different questions.

### Reading a first-chance exception line

`CharacterCreationTrace` arms an `AppDomain.FirstChanceException` observer for the whole session.
Every exception in game code is logged **even if the game swallows it**, with:

- the full **inner-exception chain** (`<- INNER:` lines) — a `TypeInitializationException`'s real
  cause is always its inner exception;
- `CONTEXT:` — the engine state at the throw (is `Mission.Current` null, what state, etc.);
- a memory line;
- `LIVE …` frames — the **actual executing call stack** at the throw, which shows *who triggered*
  it. The exception's own stack is truncated to the throw point and hides the trigger; the live
  stack does not.

Key subtlety: a **failed type initializer is cached**. .NET runs a `.cctor` once; if it throws,
every later access re-throws the *same* exception without re-running the ctor. So a logged
type-init throw may be a **re-throw** whose `CONTEXT` (mission live) differs from the **origin**
(mission null, earlier). If a probe on the constructor never fires but the crash still happens,
the type was poisoned before the probe/handler existed — look earlier (load time), not at the
manifested frame.

---

## 3. The log itself: rotation + throttling

- **Rotation** (harness `Log.cs`): a segment rolls past 8 MB into a rolling window
  `CrashGuard.log.1 … .6` (~48 MB of history), checked every 256 writes. A burst no longer
  discards the evidence being chased.
- **Throttling** (`TraceThrottle`): identical repeated tracer lines are coalesced — first
  occurrence logs in full, repeats collapse to `[repeat] key ×N in Ys (identical, collapsed)` at
  most every 5 s (`Payload/TraceThrottle.cs:31,82`). This is why a per-tick fight (e.g. BT re-requesting a time mode our guard blocks) no longer
  floods the log. When adding a high-frequency tracer, route it through `TraceThrottle.Emit(key, msg)`.

---

## 4. Crash reports on disk

- TaleWorlds logs: `C:/ProgramData/Mount and Blade II Bannerlord/logs/rgl_log_*.txt` (and
  `rgl_log_errors_*`, `watchdog_*`). `watchdog_*` carries the GPU/build tags; an
  `Unhandled Exception Code 0xE0434352` in `rgl_log_*` is a managed exception that killed the process.
- HTML crash reports: `%USERPROFILE%/Documents/crashreport*.html` — the Documents **root**, which is
  the only place `collect-diagnostics.cmd:41-42` looks (newest `*.html` whose name contains "crash").
  Reports have been observed there, not in the `Documents/Mount and Blade II Bannerlord/` subfolder
  that `README.md:722-724` names; check both before concluding no report exists. ButterLib does
  **not** catch every fatal — the mod's own first-chance capture is the backstop.

---

## 5. Discipline (learned the hard way, 2026-09-04)

- **Do not pattern-match a web result onto this repo.** A known vanilla symptom name is a lead,
  not a diagnosis. Prove it in *these* logs/IL.
- **The manifested frame is not the root cause.** `Formation.ResetAux` was where the crash
  surfaced; the cause was a `beforefieldinit` struct initialized while `Mission.Current` was null.
- **A quiet fix attempt built on a wrong theory is worse than none.** Revert speculative changes
  the moment the evidence contradicts them; keep the diagnostics.
- **Instrument to catch the class, not the instance.** When two failures rhyme (an
  AccessViolation in a finalizer, a null-at-transition), suspect one shared class and add telemetry
  that would reveal it (here: session-wide first-chance + memory heartbeat).
