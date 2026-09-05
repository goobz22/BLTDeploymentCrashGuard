# Diagnostics playbook

How to investigate a crash, freeze, or wrong behaviour in this mod without guessing. The rule
this whole file exists to enforce: **prove the root cause from IL and logs before changing code.**
The location where a symptom manifests is almost never the root-cause location — find both.

Two investigation surfaces: **static** (read the installed assemblies) and **runtime** (turn on
tracing and read `CrashGuard.log`).

---

## 0. Crash triage checklist (start here)

Work down the list; do not skip to a fix.

1. **Collect the evidence.** `Modules/BLTDeploymentCrashGuard/CrashGuard.log` plus its rotated
   segments (`.1 … .6`), TaleWorlds' `rgl_log_*` / `rgl_log_errors_*` / `watchdog_*`, and any
   ButterLib report (§4). `collect-diagnostics.cmd` gathers them in one pass.
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
| `[MO-PROBE]` | (dev) logs each `MovementOrder` construction + Mission.Current state — an origin probe for the type-init crash. |
| `[MO-INIT]` | The `MovementOrder` type-init guard's result at load: "initialized safely" or "already poisoned". |
| `[DIAG]` | Memory + engine-state heartbeat (WS/private/managed, GC counts, handles, threads; Mission/GameState/Campaign) every ~15 s and at every mission transition. Use it to see a leak/balloon build up before a symptom. |
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
| `[<TAG>] type not found: X` | the type could not be resolved by name — a game/BT rename, nothing was hooked. |
| `[<TAG>] no patchable method T.M` | the type resolved, the method did not. |

**N = 0 (or a `type not found` line) means the trace is empty by construction.** With by-name
reflection everywhere, a silent hook miss is indistinguishable from "the bug did not happen", so a
tracer that prints no count is a tracer you cannot reason from — fix the count first.

### What `MOD HEALTH:` does not cover

`MOD HEALTH:` is built from the components that called `Diag.Report`, and is printed once per
generation from `PayloadEntry.Apply` (with `[SELFTEST]` following when `"selfTest": true`). A
component that never reports is **absent**, not healthy — and several shipped ones never report:

| Component | What it reports | Where it does show up |
|---|---|---|
| `Payload/DeploymentCrashGuards.cs` (fix #1: `SetupTeams`, `FinishDeployment` finalizers) | nothing — attribute classes applied by `harmony.PatchAll(typeof(PayloadEntry).Assembly)`, with no `Apply`, no `Diag.Report`, no `SelfHealing.RegisterTest` and untagged log lines | `GUARD ACTIVITY:` — `setup-teams-guard`, `finish-deployment-guard` (`SelfHealing.RecordFire`), plus the `SUPPRESSED crash in …` line |
| `BattleMode` / `PeerDetection`, `PayloadEntry` | no health, no self-test; nothing pins the `BattleTargets` member list, and `EnumerateTargets` skips an unresolvable type with a bare `continue` | `[BATTLE-MODE]` lines and the patch counts they carry |
| `PlayerIdentityGuard`, `BootstrapWatch` | nothing at all (no report, no self-test, no `RecordFire`) | their own tags only, e.g. `[IDENTITY]` |
| `ClientHeroCreationGuard` | `RecordFire("hero-creation-guard")` only | `GUARD ACTIVITY:` |
| `StealthHideoutAdvisor` | returns silently when `HideoutAmbushMissionController` is missing (older game build) | nothing — it is simply absent |

Practical consequences when reading a log:

- A fix missing from `MOD HEALTH:` may still be loaded. Check `GUARD ACTIVITY:` and the fix's own
  tag before concluding it did not apply.
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
  occurrence logs in full, repeats collapse to `[repeat] key ×N in Ys (collapsed)` at most every
  5 s. This is why a per-tick fight (e.g. BT re-requesting a time mode our guard blocks) no longer
  floods the log. When adding a high-frequency tracer, route it through `TraceThrottle.Emit(key, msg)`.

---

## 4. Crash reports on disk

- TaleWorlds logs: `C:/ProgramData/Mount and Blade II Bannerlord/logs/rgl_log_*.txt` (and
  `rgl_log_errors_*`, `watchdog_*`). `watchdog_*` carries the GPU/build tags; an
  `Unhandled Exception Code 0xE0434352` in `rgl_log_*` is a managed exception that killed the process.
- ButterLib crash reports (when it catches one): under the user's Documents Bannerlord folder.
  Note ButterLib does **not** catch every fatal — the mod's own first-chance capture is the backstop.

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
