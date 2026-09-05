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
   config produced the log and which version wrote it. Read the config as a *partial* record: a
   file written by an older build can be a two-key stub (`BattleMode` carried its own two-key
   writer until v1.3.2 — `CHANGELOG.md` v1.3.2 § *Battles*; the harness `GuardConfig` template is
   now the only writer), and every key **absent** from the file silently takes its
   `DefaultJson` value (`Harness/GuardConfig.cs:82-115`). A short file is not a minimal config;
   read the missing keys as defaults, not as "off". What the bundle still does **not** carry:
   the memory dump inside the crash folder, and any segment already rotated out past `.6`.
2. **Identify the build that produced the log.** The banner line carries the mod version, harness
   build time and session id; `[HOTRELOAD] genN applied` says which payload generation was live at
   the moment of the crash (health and tracer lines are re-printed per generation).
3. **Read the mod's own verdict before the exception.** `MOD HEALTH:`, `[SELFTEST]`,
   `GUARD ACTIVITY:`, `[BATTLE-MODE]`, `[DEPLOY-GUARD]` — but see § *What `MOD HEALTH:` does not
   cover* below: absence is not health. `[SELFTEST]` in particular is **off in the shipped config**
   (`"selfTest": false`, `Harness/GuardConfig.cs:107`; the block runs only under
   `GuardConfig.Bool("selfTest", false)`, `Payload/PayloadEntry.cs:105-108`), so a log with no
   `[SELFTEST]` block is a default-config log, not a mod that skipped its tests.
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
fine". One of our own layers can also change behaviour with no exception in sight:
`PartyAiCrashGuard`'s layer 1 is a **prefix** that skips a party's tick in a proven-inconsistent
state (`Payload/PartyAiCrashGuard.cs:22-25`), not a finalizer, so it never reaches
`GUARD ACTIVITY:` — it does not call `SelfHealing.RecordFire`, which only the two finalizers do
(`:126,155`). It is **not** silent, though: grep
`[AI-GUARD] skipping AI tick for half-synced party` (`Payload/PartyAiCrashGuard.cs:107,165-183`),
throttled to one line per 5 s and carrying the skip count since the last report.

1. **Read the `[DIAG]` heartbeat — it exists only when `"tracing": true`.** The heartbeat is gated
   on `RuntimeDiagnostics.Enabled` (`Payload/RuntimeDiagnostics.cs:33,37-40`), which is set only
   inside the tracing branch of `PayloadEntry.Apply` (`:83-95`, assignment at `:93`), and the
   shipped config is `"tracing": false` (`Harness/GuardConfig.cs:106`). A log with no `[DIAG]`
   line was produced with the default config — get a tracing-on reproduction rather than treating
   that silence as evidence. With tracing on it is emitted on a timer regardless of whether
   anything throws, so it is the one signal a hang still produces. Compare consecutive lines for
   build-up in `WS=` / `priv=` / `managed=`, in `handles=` and in `threads=`; the `Mission=` /
   `GameState=` / `Campaign=` fields say what the engine believed it was doing. **The last heartbeat
   is the time of death** — everything after it is post-mortem.
2. **Read the last `[TRACE]` and `[BATTLE-MODE]` lines before the heartbeat stops.** They are the
   last decision the mod made, and they name the chokepoint it was standing on. `[TRACE]` is
   tracing-only like the heartbeat; `[BATTLE-MODE]` is always on from v1.3.2.
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
- **Whether the mod resolved its targets on that machine** — `MOD HEALTH:`, and the `[SELFTEST]`
  block *if they had `"selfTest": true`*; the shipped default is `false`
  (`Harness/GuardConfig.cs:107`), so most stranger logs carry `MOD HEALTH:` and nothing else. A
  `FAIL` names the component; a resolution failure names the member that moved.
- **Which guards actually fired** — `GUARD ACTIVITY:`, with a count per guard id. A non-zero count
  is proof a guarded path was hit on their machine. `battle-mode=N` is the most useful entry for a
  "my troops were missing" report: it proves BT's battle patches were actually lifted or restored
  N times this session (`Payload/BattleMode.cs:283,332`), which `MOD HEALTH:` can never show.
- **What was thrown, and by whom** — the first-chance block (§2): exception type, the full
  `<- INNER:` chain, the `CONTEXT:` engine state and the `LIVE …` trigger stack.
- **What the mod decided** — the last `[BATTLE-MODE]`, `[TIME]`, `[ENCOUNTER-GUARD]` lines. Pin the
  version first: on v1.3.1 and earlier — still what `origin` ships — a missing
  `[BATTLE-MODE] … start-battle` line is that build's expected broken state, not a failed
  chokepoint (§2 *Runtime tracing*, the tracing-and-behaviour paragraph).

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

Both keys ship as `false` (`Harness/GuardConfig.cs:106-107`), so a player's log carries neither the
`[DIAG]` heartbeat nor the `[SELFTEST]` block — absence of either says "default config", not
"broken". With hot-reload on, flipping `tracing` and dropping a rebuilt payload turns tracers on
mid-session (the flag is read fresh from disk on each apply). Log lives at
`Modules/BLTDeploymentCrashGuard/CrashGuard.log`.

**A tracing-on reproduction is representative of default play — as of v1.3.2, and not before.**
`TracePatches` is now log-only (`Payload/TracePatches.cs:88`) and every decision point is hooked by
the guard that owns it, so tracing adds lines and changes nothing else. In v1.3.1 and earlier —
still what `origin` ships — it changed behaviour: `BattleMode`'s `StartBattle` / `OpenNew` decisions
and `EncounterLoopGuard`'s `Finish` stamp existed only while the tracer was loaded, so with the
shipped `"tracing": false` they never ran at all (`CHANGELOG.md` v1.3.2 § *Battles*, § *Crash
guards: health and self-tests*). Two consequences: a tracing-on repro of an older build can behave
*better* than the player's tracing-off session did, and on a v1.3.1 log the missing
`[BATTLE-MODE] … start-battle` line is the expected broken state, not a failed chokepoint.

### Log tags (grep targets)

Startup/health: `MOD HEALTH`, `[SELFTEST]`, `GUARD ACTIVITY`, `[HOTRELOAD]`, `[BATTLE-MODE]`,
`[DEPLOY-GUARD]`.
Fixes each own a tag — `[SIEGE-CMD]`, `[COOP-CMD]`, `[IDENTITY]`, `[TIME-GUARD]`, `[GATE]`, etc.
Diagnostics added in the 2026-09-04 investigation:

| Tag | What it gives you |
|---|---|
| `[CHARGEN]` | Character-creation lifecycle + a **session-wide first-chance exception capture** with the full inner-exception chain and the throwing frames. |
| `[MO-PROBE]` | (dev) logs the **first 12** `MovementOrder` constructions + `Mission.Current` state, and any throw out of the ctor at the instant it is thrown (`Payload/MovementOrderInitProbe.cs:27-28,56,73-92`; the cap is the `LogFirst` constant at `:28`). After the 12th it is silent except on a throw — an origin probe for the type-init crash, not a census. |
| `[MO-INIT]` | The `MovementOrder` type-init guard's result at load: "initialized safely" or "already poisoned". |
| `[DIAG]` | Memory + engine-state heartbeat (WS/private/managed, GC counts, handles, threads; Mission/GameState/Campaign) every ~15 s and at every mission transition. **Only emitted when `"tracing": true`** — gated on `RuntimeDiagnostics.Enabled` (`Payload/RuntimeDiagnostics.cs:33,37-40,60-63`), which only the tracing branch sets. Use it to see a leak/balloon build up before a symptom; with tracing on it is the only signal a freeze still produces (§0 *Branch: freeze / hang*). |
| `[DEPLOY-GUARD]` | The two deployment finalizers (README fix #1) and their health check. `deployment crash guards active — SetupTeams=guarded FinishDeployment=guarded` at load (`Payload/DeploymentCrashGuards.cs:43`), `SUPPRESSED crash in DeploymentMissionController.SetupTeams: …` when one fires (`:107,128`), and one line per recovery step that itself failed (`:144-159`). Every line in that file now carries the tag; before 2026-09-04 they were untagged and ungreppable. **A `SUPPRESSED` line is not "the battle was fine"** — the player still went into an empty-sided battle; chase `[BATTLE-MODE]`, not this guard (see the `deployment-guards` row in § *What `MOD HEALTH:` does not cover*). |
| `[TIME] … change SUPPRESSED/ALTERED by [X]` | Names **which** of our three prefixes on `Campaign.set_TimeControlMode` vetoed a write — `[TIME-GUARD]`, `[CLICK-SPEED]`, or `another patch (not one of ours)` when the vetoer was not ours (`Payload/TimeTrace.cs:118-124`). The vetoing prefix notes itself (`Payload/TimeEnforcementGuard.cs:217`, `Payload/MapClickSpeedKeeper.cs:93`) and the `[repeat]` dedup key includes the vetoer (`Payload/TimeTrace.cs:127-128`), so a collapsed burst still says who won. |
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

`MOD HEALTH:` is built from the components that called `Diag.Report`, and it prints a **count**, not
a roster: names appear only for *degraded* entries (`Harness/Diag.cs:87-103`), so an all-resolved
line is `MOD HEALTH: N active, all resolved` and nothing else. A component that never reports is
**absent**, not healthy.

It is printed from `PayloadEntry.Apply` (`Payload/PayloadEntry.cs:104`, with `[SELFTEST]` following
when `"selfTest": true`) **and again inside the `[HOTRELOAD] genN applied` line**
(`Harness/HotReload.cs:380-381`, appended unconditionally) — so **every** generation, a fresh launch
included, produces two copies. The copy count never tells you whether this was a launch or a reload;
read `genN` and the `(initial)` / `(reload)` reason instead.

The 2026-09-04 pass closed the worst of those absences. Six components that previously reported nothing
now report health and register a self-test, so a rename under them now degrades the health line
instead of passing silently:

| Component id | Self-test | Critical? | Notes |
|---|---|---|---|
| `battle-mode` | `battle-mode.contract` | when a chokepoint hook is missing, **or** when `Apply` throws | Two critical paths, not one: `critical: !(startBattle && missionOpen)` (`Payload/BattleMode.cs:130`) and the catch, which is `critical: true` unconditionally (`:136`). Detail carries `chokepoints StartBattle=… OpenNew=…; lift targets N/M method(s)` and names any unresolved one (`:119-130`); an unresolved lift target degrades but is not critical — it costs one lifted method, not the player side. Fires into `GUARD ACTIVITY:` whenever patches are lifted or restored (`:283,332`). |
| `encounter-loop-guard` | `encounter-loop-guard.contract` | no | Reports **healthy** when BT is absent, degraded when BT is present but `BattleSyncBehavior` / `ApplyEncounterRequestNow` is missing (`Payload/EncounterLoopGuard.cs:83,114-121`). Its `inert — BannerlordTogether not loaded` detail is on the healthy path, so it is discarded and never printed — read the stand-down off the *missing* `[ENCOUNTER-GUARD] … loop breaker active` load line (`:116`) instead. Fires into `GUARD ACTIVITY:` when the breaker trips (`:219`). |
| `deployment-guards` | `deployment-guards.contract` | **yes** | Verifies after `PatchAll` that our finalizers really sit on `SetupTeams` and `FinishDeployment` (`Payload/DeploymentCrashGuards.cs:35-43`). Documented limitation, restated in the load line itself (`:43-44`): the finalizers suppress the CTD, they do **not** restore the missing player-side troops — auto battle mode is what prevents an empty player side (`:14-18`). |
| `party-ai-guard` | `party-ai-guard.contract` | no | Only the two finalizers reach `GUARD ACTIVITY:` (`Payload/PartyAiCrashGuard.cs:126,155`); layer 1's skip prefix logs `[AI-GUARD] skipping AI tick …` instead (`:107,165-183`). |
| `hero-creation-guard` | `hero-creation-guard.contract` | no | Previously `RecordFire` only. |
| `movementorder-typeinit` | `movementorder-typeinit.contract` | **yes** | The self-test pins the premise: struct + `beforefieldinit`, the ctor, exactly one transpiled site, and the null-safe helper (`Payload/MovementOrderTypeInitGuard.cs:152-166`). A **load-time** fix — a fresh launch, never hot-reload. |

Self-test names follow `<component>.contract`; the three exceptions are `pregnancy-sync.loopback`,
`stash-sync.loopback` and `client-bootstrap-fix.wiring`, which prove a pipeline rather than a
decision table.

What does not reliably reach `MOD HEALTH:` — never for the first three rows, and **conditionally**
for the rest, where a silent `Apply` return removes the component from the line entirely:

| Component | What it reports | Where it does show up |
|---|---|---|
| `PlayerIdentityGuard`, `BootstrapWatch` | no `Diag.Report`, no self-test — but each now calls `SelfHealing.RecordFire` on every correction / handled abort (`Payload/PlayerIdentityGuard.cs:89`, `Payload/BootstrapWatch.cs:80`) | `GUARD ACTIVITY:` — `player-identity-guard`, `bootstrap-watch` — and their own tags, e.g. `[IDENTITY]` |
| `TimeEnforcementGuard`, `MapClickSpeedKeeper`, `TimeFlowPatch`, `ShareTimeControl` | nothing | their own tags only: `[TIME-GUARD]`, `[CLICK-SPEED]`, `[TIME-FLOW]`, `[SHARE-TIME]` |
| `PeerDetection`, `PayloadEntry` | nothing of their own | `PeerDetection.Snapshot()` embedded in other components' lines |
| `StealthHideoutAdvisor` | **conditional** — reports normally on a current game build (`Payload/StealthHideoutAdvisor.cs:59`, `[STEALTH] … advisor active on N method(s)`); absent only on an older build without `HideoutAmbushMissionController`, where `Apply` returns first (`:37-40`) | normally `MOD HEALTH:` + `stealth-hideout-advisor.contract`; on an older build, nothing |
| `BackgroundTickBudgetGuard` | **conditional** — returns before any `Diag.Report` when `BannerlordTogether.CoopSubModule` is absent (`Payload/BackgroundTickBudgetGuard.cs:57-61`), so on a no-BT launch the component vanishes *including* its `critical: true` path (`:66`) | its tag `[TICK-GUARD]`, and `bg-tick-budget-guard` in `GUARD ACTIVITY:` |
| `JoinSyncPauseEscape` | **conditional** — returns when `CoopSubModule` cannot be resolved (`Payload/JoinSyncPauseEscape.cs:69-73`) | its tag `[JOIN-ESCAPE]` |

The two deployment finalizers keep their **own** fire ids, `setup-teams-guard` and
`finish-deployment-guard` (`Payload/DeploymentCrashGuards.cs:106,127`), under the single
`deployment-guards` health component: in `GUARD ACTIVITY:` you see which of the two fired, in
`MOD HEALTH:` you see one component.

Practical consequences when reading a log:

- A fix missing from `MOD HEALTH:` may still be loaded. Check `GUARD ACTIVITY:` and the fix's own
  tag before concluding it did not apply. `GUARD ACTIVITY:` is itself throttled — at most one line
  per 120 s, and only reprinted when the summary text changes (`Payload/PayloadEntry.cs:191-211`) —
  so *its* absence is not evidence either; grep the tag and `SUPPRESSED crash in` too.
- A BT or game rename under `BattleTargets` no longer passes silently: an unresolved lift target is
  named in the `battle-mode` health detail and logged once as
  `[BATTLE-MODE] lift target type not found: … — its BT patches cannot be lifted (game update?)`
  or `lift target method not found: <Type>.<Method> (game update?)` (`Payload/BattleMode.cs:349,366`).
  Still compare the `[BATTLE-MODE]` patch counts against a known-good log — a count that fell is the
  earliest signal.
- Read the `MOD HEALTH:` suffix rather than reacting to the count: when something is unresolved the
  line appends *"(read each detail: a BannerlordTogether OR game update may have renamed a member; a
  detail saying 'inert', 'not loaded' or 'older game build' is on purpose)"* (`Harness/Diag.cs:93`).
  **That suffix promises something the line cannot deliver, so do not grep a degraded entry for
  those words** — none will ever carry one. `Diag.Report` keeps `detail` only on the failing branch
  and discards it on the `ok` branch (`Harness/Diag.cs:71-85`), and every stand-down in the payload
  is a *healthy* report: `inert — BannerlordTogether not loaded`
  (`Payload/EncounterLoopGuard.cs:83`), `no BT present` (`Payload/ClientBootstrapFix.cs:65`),
  `disabled by config` (`Payload/IllnessDeathGuard.cs:50` and four more). A stand-down therefore
  shows only inside the `active` count; confirm it from the component's own tag line.
- `GUARD ACTIVITY:` counts accumulate across hot-reload generations while `MOD HEALTH:` is reset per
  generation (`HOTRELOAD.md`), so the two lines answer different questions.

### Reading a first-chance exception line

`CharacterCreationTrace` arms an `AppDomain.FirstChanceException` observer for the whole session.
Every exception with a game frame is logged **even if the game swallows it** — up to **400 per
session**, after which the observer returns silently, with no line and not even a `[repeat]` rollup
(`Payload/CharacterCreationTrace.cs:33,172-175`; the counter increments at `:176`, ahead of
`TraceThrottle.Emit` at `:186`, so collapsed repeats spend the budget too). Treat a long session's
later quiet as *budget exhausted*, not as clean. Each logged line carries:

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

### A real log, annotated

From `Modules/BLTDeploymentCrashGuard/CrashGuard.log`, 2026-09-04. Every line below is verbatim;
unrelated lines between them have been elided and the cuts are marked `…`. The excerpts come from
three sessions on the same day, each with its own banner in the live log.

**The bracket after the timestamp is the session role**, set from the payload tick:
`[?]` = not computed yet (everything logged before the first tick, i.e. the whole startup block),
`[C]` client, `[H]` host with a peer connected, `[S]` solo — no remote peer
(`Payload/PayloadEntry.cs:173-184`, `Harness/Log.cs:19,69`). A startup block is always `[?]`; a
line that says `[S]` is authoritative that no peer was connected when it was written.

#### A launch, start to first heartbeat (session `1265ffd7`)

```
2026-09-04 15:11:27.462 [?] ===== BLT Deployment Crash Guard v1.3.2 (harness build 2026-09-04 13:30) session=1265ffd7 =====
2026-09-04 15:11:27.467 [?] [HOTRELOAD] engine start — hotReload=True roslyn=False prebuilt=True sourceDir=(none)
2026-09-04 15:11:27.485 [?] payload build 15:07:49 applying on bltogether.crashguard.gen1
2026-09-04 15:11:27.513 [?] [MO-INIT] MovementOrder constructed with no active mission — returned time 0 instead of crashing (this is the fix firing)
2026-09-04 15:11:27.515 [?] [MO-INIT] MovementOrder initialized safely (patched 1 site(s)) — the beforefieldinit type-init battle crash is prevented for this session
…                                    (38 per-guard and per-tracer load lines, then [BATTLE-MODE] config: battleMode=auto)
2026-09-04 15:11:29.475 [?] [BATTLE-MODE] VANILLA battles active (auto: confidently no session, apply) — removed 0 foreign patch(es)
…                                    (patches applied; battleMode=auto tracing=true)
2026-09-04 15:11:29.476 [?] MOD HEALTH: 20 active, all resolved
2026-09-04 15:11:29.477 [?] [SELFTEST] running 20 guard decision-logic test(s)…
…                                    (11 earlier [SELFTEST] PASS lines)
2026-09-04 15:11:29.504 [?] [SELFTEST] PASS siege-command-guard.contract — members re-resolved (incl. vanilla's siege AI-on default); hand-off decision table verified
…                                    (5 more [SELFTEST] PASS lines)
2026-09-04 15:11:29.520 [?] [SELFTEST] FAIL pregnancy-sync.loopback — threw: Object reference not set to an instance of an object.
…                                    (2 more [SELFTEST] PASS lines)
2026-09-04 15:11:29.531 [?] [SELFTEST] 19 passed, 1 failed
2026-09-04 15:11:29.532 [?] [HOTRELOAD] gen1 applied (initial) | MOD HEALTH: 20 active, all resolved
…                                    (hot-reload watch, time-guard, role and stream lines)
2026-09-04 15:11:30.926 [S] [DIAG] mem WS=3150MB priv=3592MB managed=87MB peakWS=3150MB gc0/1/2=246/49/11 handles=1864 threads=123 | Mission=null GameState=none Campaign=null
2026-09-04 15:11:30.928 [S] GUARD ACTIVITY: none fired this session (nothing crashed on a guarded path)
```

| Line | What it settles |
|---|---|
| `===== … v1.3.2 (harness build 2026-09-04 13:30) session=1265ffd7 =====` | The build that produced everything below, and the session id that separates this launch from the next one in the same file. The **harness** build time is stamped here; it does not move when only the payload is redeployed. |
| `[HOTRELOAD] engine start — hotReload=True roslyn=False prebuilt=True sourceDir=(none)` | The harness's own first line: whether hot-reload is armed on this machine and in which mode. `hotReload=True` is a dev box; a player's log says `False` and no `gen2` can follow. |
| `payload build 15:07:49 applying on bltogether.crashguard.gen1` | The payload half — the pair that matters. The harness is 13:30 and the payload 15:07, which is normal during iteration and is exactly the pair `dist/manifest.txt` exists to keep honest when shipping. `gen1` = a fresh launch; `gen2`, `gen3`… are hot-reloads. |
| `[MO-INIT] MovementOrder constructed with no active mission — returned time 0 instead of crashing (this is the fix firing)` | The guard **firing**, at load, before any mission — the type would otherwise have been poisoned here and every later `Formation.ResetAux` would re-throw the cached failure (see the next excerpt). |
| `[MO-INIT] MovementOrder initialized safely (patched 1 site(s))` | The transpiler found exactly the one site it expects. A count other than 1 is the signal that the game build moved. |
| `[BATTLE-MODE] VANILLA battles active (auto: confidently no session, apply) — removed 0 foreign patch(es)` | The decision, its **reason** (`apply` — the payload-load decision point), the confidence (`confidently no session`, from `PeerDetection`), and what it did: 0 patches removed, because BT has not installed its battle patches yet at load. Contrast with the `start-battle` line below. |
| `MOD HEALTH: 20 active, all resolved` | Only the components that called `Diag.Report`, as a bare **count** — an all-resolved line names nothing (`Harness/Diag.cs:87-103`). **This capture predates the 2026-09-04 health wiring**, so the current build *counts* `battle-mode`, `encounter-loop-guard`, `deployment-guards`, `party-ai-guard`, `hero-creation-guard` and `movementorder-typeinit` too and the number rises. Never read a count across builds as a regression. To compare rosters, use the `[SELFTEST]` block — one named line per registered component — plus each guard's own load tag. |
| `[SELFTEST] running 20 guard decision-logic test(s)…` | The denominator. Fewer tests than components means a component registered no test — read the count, not just the pass line. |
| `[SELFTEST] PASS siege-command-guard.contract — members re-resolved …` | A pass is a **re-resolution** of the members plus a check of the decision table, run against the live game — not a compile-time assertion. This is what tells you a rename has *not* happened. |
| `[SELFTEST] FAIL pregnancy-sync.loopback — threw: …` | A real failure, kept visible: the wire loopback threw an NRE. It names the component (`pregnancy-sync`) and the suite (`.loopback`, a pipeline test rather than a `.contract` decision table). |
| `[SELFTEST] 19 passed, 1 failed` | The summary to grep. `1 failed` with a healthy `MOD HEALTH:` line is normal and important: health says *resolved*, the self-test says *still behaves correctly*. |
| `[HOTRELOAD] gen1 applied (initial) \| MOD HEALTH: …` | The same health text a second time — the suffix is appended unconditionally (`Harness/HotReload.cs:380-381`), so **every** generation prints two copies, this fresh launch included. **Do not count `MOD HEALTH:` lines as generations**; read `gen1` and the `(initial)` / `(reload)` reason. |
| `[DIAG] mem WS=3150MB … handles=1864 threads=123 \| Mission=null GameState=none Campaign=null` | The heartbeat. On its own it is a baseline; its value is the *series* (§0 *Branch: freeze / hang*). Note the role flipped to `[S]` here — the first tick has run. |
| `GUARD ACTIVITY: none fired this session (nothing crashed on a guarded path)` | Nothing has fired **yet**. It is throttled to one line per 120 s and only reprinted when the text changes, so this line's absence later means "unchanged", not "not running". |

Later in that same session, guards had fired and the line carries counts:

```
2026-09-04 15:19:30.921 [S] GUARD ACTIVITY: client-bootstrap-fix=1, bg-tick-budget-guard=6, illness-death-guard=1
```

Each entry is a `SelfHealing.RecordFire` id with the number of times it fired this session — proof
a guarded path was actually hit, which `MOD HEALTH:` can never tell you. Two ids joined this line in
the 2026-09-04 pass and are not in the capture above: `battle-mode`, counted every time BT's battle
patches are actually lifted or restored (`Payload/BattleMode.cs:283,332`), and
`encounter-loop-guard`, counted when the breaker trips (`Payload/EncounterLoopGuard.cs:219`).
`battle-mode=N` is the entry to look for on a "my troops were missing" report — it settles whether
the mode ever switched, which neither `MOD HEALTH:` nor a single `[BATTLE-MODE]` line does.

#### The battle chokepoint doing its job (session `13be0322`)

```
2026-09-04 14:32:03.088 [S] [BATTLE-MODE] VANILLA battles active (auto: confidently no session, start-battle) — removed 24 foreign patch(es)
```

Same decision, different decision point: `start-battle` is `PlayerEncounter.StartBattle`, and by
then BT **has** installed its battle patches — all 24 of them are lifted for this solo battle. This
is the line that matters when reading a "my troops were missing" report: `removed 0` at `apply` is
expected, `removed 24` at `start-battle` is the fix working, and no `start-battle` line at all means
the chokepoint never ran.

**That last reading holds on v1.3.2+ only.** On v1.3.1 and earlier the decision points lived on the
`[TRACE]` tracer, so with the shipped `"tracing": false` no session on any machine produced a
`start-battle` line — that absence is the bug itself, not a symptom of another
(§2 *Runtime tracing*, the tracing-and-behaviour paragraph).

#### A first-chance exception, in full (session `13ec180d`)

```
2026-09-04 14:45:34.050 [S] [CHARGEN] first-chance System.TypeInitializationException: The type initializer for 'TaleWorlds.MountAndBlade.MovementOrder' threw an exception.
      at TaleWorlds.MountAndBlade.Formation.ResetAux()
   <- INNER: System.NullReferenceException: Object reference not set to an instance of an object.
      at TaleWorlds.MountAndBlade.MovementOrder..ctor(MovementOrderEnum orderEnum)
      at TaleWorlds.MountAndBlade.MovementOrder..cctor()
   CONTEXT: Mission=live(mode=StartUp,state=Initializing) GameState=MissionState Campaign=set
   mem WS=4480MB priv=5280MB managed=235MB peakWS=4480MB gc0/1/2=1187/269/41 handles=2005 threads=104
      LIVE TaleWorlds.MountAndBlade.Formation.ResetAux
      LIVE TaleWorlds.MountAndBlade.Formation.Reset
      LIVE TaleWorlds.MountAndBlade.Team.Initialize
      LIVE TaleWorlds.MountAndBlade.Mission+TeamCollection.Add
      LIVE TaleWorlds.MountAndBlade.MissionCombatantsLogic.AddPlayerTeam
      LIVE TaleWorlds.MountAndBlade.MissionCombatantsLogic.OnBehaviorInitialize
      LIVE TaleWorlds.MountAndBlade.Mission.AfterStart
      LIVE TaleWorlds.MountAndBlade.MissionState.FinishMissionLoading
      LIVE TaleWorlds.MountAndBlade.MissionState.TickLoading
      LIVE TaleWorlds.MountAndBlade.MissionState.OnTick
      LIVE TaleWorlds.Core.GameStateManager.OnTick
      LIVE TaleWorlds.Core.Game.OnTick
      LIVE TaleWorlds.Core.GameManagerBase.OnTick
      LIVE TaleWorlds.MountAndBlade.Module.OnApplicationTick
      LIVE TaleWorlds.DotNet.Managed.ApplicationTick
      LIVE ManagedCallbacks.LibraryCallbacksGenerated.Managed_ApplicationTick_Patch1
2026-09-04 14:45:40.554 [S] [repeat] CHARGEN-FC TypeInitializationException @ TaleWorlds.MountAndBlade.Formation.ResetAux ×1 in 6.5s (identical, collapsed)
```

| Part | What it settles |
|---|---|
| `first-chance System.TypeInitializationException … at …Formation.ResetAux()` | The **manifested** frame. This is where the symptom appears and where a naive fix would go; it is not the cause. The capture is session-wide, so this was logged whether or not the game swallowed it. |
| `<- INNER: System.NullReferenceException … at MovementOrder..ctor(MovementOrderEnum) / ..cctor()` | The **cause**. A `TypeInitializationException` is always a wrapper — its inner exception and inner frames are the answer, and here they name the static constructor of a `beforefieldinit` struct. Without the chain this log says only "something failed in `ResetAux`". |
| `CONTEXT: Mission=live(mode=StartUp,state=Initializing) GameState=MissionState Campaign=set` | Engine state **at the throw**. `Mission=live` here is the trap: the cctor that actually failed ran earlier, when `Mission.Current` was null. A cached type-init failure re-throws in a context that has nothing to do with the origin. |
| `mem WS=4480MB priv=5280MB managed=235MB …` | The same counters the `[DIAG]` heartbeat prints, sampled at the throw, so a throw can be placed on the memory curve without correlating timestamps by hand. |
| the `LIVE …` frames | The **actual executing stack**, from `Formation.ResetAux` out to `Managed_ApplicationTick_Patch1`. It names the trigger — a player team being added during `MissionState.FinishMissionLoading` — which the exception's own truncated stack does not show. This is the chain to hand to `Callers.exe`. |
| `[repeat] CHARGEN-FC TypeInitializationException @ …ResetAux ×1 in 6.5s (identical, collapsed)` | `TraceThrottle` collapsing a recurrence: the full block is logged once, later identical throws become a count. `×1` means it happened once more in those 6.5 s — the throttle is not hiding a flood here, but on a per-tick fight the ×N is where the frequency lives. |

---

## 3. The log itself: rotation + throttling

- **Rotation** (harness `Log.cs`): a segment rolls past 8 MB into a rolling window
  `CrashGuard.log.1 … .6` (~48 MB of history), checked every 256 writes. A burst no longer
  discards the evidence being chased.
- **Throttling** (`TraceThrottle`): identical repeated tracer lines are coalesced — first
  occurrence logs in full, repeats collapse to `[repeat] key ×N in Ys (identical, collapsed)` at
  most every 5 s (`Payload/TraceThrottle.cs:31,82`). This is why a per-tick fight (e.g. BT
  re-requesting a time mode our guard blocks) no longer floods the log. When adding a high-frequency
  tracer, route it through `TraceThrottle.Emit(key, msg)`. The key is part of the evidence: the
  `[TIME]` key names the vetoing prefix (§2 tag table), so a collapsed burst still says who won.

---

## 4. Crash reports on disk

All three of these are in the `collect-diagnostics.cmd` bundle now (§0 step 1). Read them there
first; the paths below are for the cases where you are on the machine itself, or need the one file
the bundle deliberately leaves out.

- **TaleWorlds logs**: `C:/ProgramData/Mount and Blade II Bannerlord/logs/` — `rgl_log_*.txt`,
  `rgl_log_errors_*`, `watchdog_log_*`, `launcher_log_*`. `watchdog_*` carries the GPU/build tags;
  an `Unhandled Exception Code 0xE0434352` in `rgl_log_*` is a managed exception that killed the
  process. Collected: 3 newest `rgl_log_*`, 3 newest `rgl_log_errors_*`, 2 newest `watchdog_log_*`,
  newest `launcher_log_*` (`collect-diagnostics.cmd:61-64`).
- **TaleWorlds crash folders**: `C:/ProgramData/Mount and Blade II Bannerlord/crashes/<folder>/`.
  The collector takes the **newest folder's `*.txt` only** (`collect-diagnostics.cmd:67-71`) — the
  dump beside them is far too large to upload. If the text files are not enough, ask for that one
  file by name; it is the only piece of the crash folder you have to request by hand.
- **HTML crash reports**: `%USERPROFILE%/Documents/crashreport*.html` — the Documents **root**,
  which is where the collector looks: newest `*.html` whose name contains "crash"
  (`collect-diagnostics.cmd:75-77`). Reports have been observed there rather than in the
  `Documents/Mount and Blade II Bannerlord/` subfolder ButterLib is usually said to use; check both
  before concluding no report exists. ButterLib does **not** catch every fatal — the mod's own
  first-chance capture is the backstop.

The collector's own closing banner (`collect-diagnostics.cmd:100-103`) prints what it bundled. Treat
the script as the authority on that list, not any prose copy of it — including this one.

---

## 5. Discipline (learned the hard way, 2026-09-04)

- **Do not pattern-match a web result onto this repo.** A known vanilla symptom name is a lead,
  not a diagnosis. Prove it in *these* logs/IL.
- **The manifested frame is not the root cause.** `Formation.ResetAux` was where the crash
  surfaced; the cause was a `beforefieldinit` struct initialized while `Mission.Current` was null.
- **A quiet fix attempt built on a wrong theory is worse than none.** Back a speculative change out
  with a FORWARD commit the moment the evidence contradicts it — never a discard-family git op
  (`CLAUDE.md` § *Working discipline*); keep the diagnostics.
- **Instrument to catch the class, not the instance.** When two failures rhyme (an
  AccessViolation in a finalizer, a null-at-transition), suspect one shared class and add telemetry
  that would reveal it (here: session-wide first-chance + memory heartbeat).
