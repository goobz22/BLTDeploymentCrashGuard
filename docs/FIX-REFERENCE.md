# Fix reference (developer)

Per-fix reference for every guard, fix, advisor and tracer in this module. One entry per
mechanism, grouped by area, followed by three lookup indexes.

For the player-facing description of the same fixes see `README.md`; the **README item**
field below is that document's numbered item, or `n/a` when the mechanism is internal and
not player-facing.

## How to read an entry

Each entry carries a header line with these fields:

| Field | Meaning |
|---|---|
| **README item** | Numbered item in `README.md`, or `n/a` |
| **Source** | Repo-relative file |
| **Class** | Type that owns the mechanism |
| **Tag** | Grep tag written into `CrashGuard.log` (`(untagged)` when the line carries no bracket tag) |
| **Config** | `guardconfig.json` key read by that code (`none` when it reads none directly) |
| **Scope** | solo / host / client / both — which co-op role the mechanism actually acts on |

and then four prose fields: **Mechanism** (patch kind plus the exact game/BT members
patched), **Patched members**, **Limitations**, **Self-test** (what the registered
`SelfHealing` test pins, or `none registered`).

### Global gating

Two config keys gate almost everything and are therefore not repeated per entry:

- `safeMode` (`Payload/PayloadEntry.cs:31-36`) — when true `Apply` returns before any patch
  is installed, so every guard, fix and tracer in this document is off.
- `selfTest` (`Payload/PayloadEntry.cs:103`) — when true `SelfHealing.RunSelfTests()` runs
  after wiring; only the components that call `SelfHealing.RegisterTest` are exercised.

`tracing` (read fresh from disk, `Payload/PayloadEntry.cs:211-232`) gates the tracer bundle,
and is load-bearing for two non-tracer behaviours — see the entries for
`EncounterLoopGuard` and `BattleMode`.

## Contents

- [Battle and deployment crash guards](#battle-and-deployment-crash-guards)
- [Battle mode and payload entry](#battle-mode-and-payload-entry)
- [Diagnostics and tracers](#diagnostics-and-tracers)
- [Gameplay and UI guards](#gameplay-and-ui-guards)
- [Harness](#harness)
- [Indexes](#indexes)

---

## Battle and deployment crash guards

None of the five files in this area reads `guardconfig.json` directly (verified by grep for
`GuardConfig` across `Payload/DeploymentCrashGuards.cs`, `Payload/PartyAiCrashGuard.cs`,
`Payload/EncounterLoopGuard.cs`, `Payload/MapIncidentCrashGuard.cs`,
`Payload/BackgroundTickBudgetGuard.cs` — zero hits). They are gated only by `safeMode`, and
their self-tests only by `selfTest`.

### SetupTeams crash guard

**README item** 1 · **Source** `Payload/DeploymentCrashGuards.cs` · **Class**
`SetupTeamsCrashGuardPatch` · **Tag** (untagged) · **Config** none · **Scope** both (failure
observed on the host)

**Bug.** Vanilla `DeploymentMissionController.SetupTeams()` dereferences
`Mission.InitialPlayerAgent` with no null check. Under BannerlordTogether the player agent
is never spawned during team setup — including on a host with zero clients connected,
`DefaultHostingTopology: LegacyPlayerHost` — so the vanilla line NREs, the exception unwinds
through `Mission.OnTick` into the engine, and the game crashes to desktop unconditionally
(`Payload/DeploymentCrashGuards.cs:8-12`; `UPSTREAM_BUG_REPORT.md:42-93`). Player-side
formations also showed 0/0 "Formation is currently empty" with 90+ healthy troops.

**Mechanism.** Attribute-declared Harmony **finalizer** (`Payload/DeploymentCrashGuards.cs:13-27`),
installed by `harmony.PatchAll(typeof(PayloadEntry).Assembly)` (`Payload/PayloadEntry.cs:46`)
rather than an explicit `Apply()`. Returns `null` for the escaping exception, records a fire
under `setup-teams-guard`, logs the full exception (`:23`) and raises an on-screen notice
(`:24`).

**Patched members.** `TaleWorlds.MountAndBlade.DeploymentMissionController.SetupTeams`
(finalizer).

**Limitations.** Removes the crash only; it does not fix the root cause. The player side
still spawns empty, so solo-host battles remain unplayable
(`UPSTREAM_BUG_REPORT.md:104-108`). The real defect — BT failing to roster and spawn
player-side troops — is upstream.

**Self-test.** None registered; only `SelfHealing.RecordFire("setup-teams-guard")` at `:22`.

### FinishDeployment crash guard (with tail replay)

**README item** 1 · **Source** `Payload/DeploymentCrashGuards.cs` · **Class**
`FinishDeploymentCrashGuardPatch` · **Tag** (untagged) · **Config** none · **Scope** both

**Bug.** The same missing-player-agent condition crashes
`DeploymentMissionController.FinishDeployment`, which also dereferences
`Mission.InitialPlayerAgent` — and that field is re-nulled if the player agent is ever
removed, so the null is not only a startup condition
(`Payload/DeploymentCrashGuards.cs:29-33`). Suppressing the throw alone would leave the
battle frozen mid-deployment: AI ticking still off, player agent still non-detachable and
AI-controlled, dying disabled, the deployment behavior still attached.

**Mechanism.** Harmony **finalizer** that suppresses the exception **and** best-effort
replays the method's remaining tail so the battle unfreezes
(`Payload/DeploymentCrashGuards.cs:37-80`), via `__instance.Mission`:
`Agent.SetDetachableFromFormation(true)`; `Agent.Controller = AgentControllerType.Player`;
`mission.AllowAiTicking = true`; `mission.DisableDying = false`;
`mission.SetFallAvoidSystemActive(false)`; `mission.OnAfterDeploymentFinished()`;
`AccessTools.Method(__instance.GetType(), "AfterDeploymentFinished")?.Invoke(__instance, null)`
(non-public, resolved by name); `mission.RemoveMissionBehavior(__instance)`. Each step is in
its own try/catch so one failing step cannot abort the rest.

**Patched members.** `DeploymentMissionController.FinishDeployment` (finalizer). Replayed
but not patched: `Mission.OnAfterDeploymentFinished`,
`DeploymentMissionController.AfterDeploymentFinished` (reflection),
`Mission.RemoveMissionBehavior`, `Mission.SetFallAvoidSystemActive`,
`Agent.SetDetachableFromFormation`, `Agent.Controller`.

**Limitations.** The tail replay is a hand-maintained mirror of vanilla's tail — a game
update that changes `FinishDeployment`'s tail silently makes the recovery incomplete.
`AfterDeploymentFinished` is resolved by name via `AccessTools` and silently skipped if
renamed. Like the `SetupTeams` guard it does not restore the missing player-side troops.

**Self-test.** None registered; `SelfHealing.RecordFire("finish-deployment-guard")` at `:43`.

### Party-AI crash guard — layer 1: `MobilePartyAi.Tick` prefix

**README item** 6 · **Source** `Payload/PartyAiCrashGuard.cs` · **Class**
`PartyAiCrashGuard` · **Tag** `[AI-GUARD]` · **Config** none · **Scope** both (state arises
during a co-op join; observed on the host)

**Bug.** Crash 2026-08-19 on the host the moment a co-op client joined: NRE in
`MobilePartyAi.GetBehaviors` via `Campaign.PartiesThink`. IL at the fault site (~offset
04B4): the `DefendSettlement` branch reads `_mobileParty.TargetSettlement` and, when that is
null, falls back to `targetParty.TargetSettlement` — with both null it dereferences null.
Vanilla can never produce `DefendSettlement` with no target settlement and no target party;
a party whose fields are synced piecemeal during a co-op join can, for a few ticks
(`Payload/PartyAiCrashGuard.cs:12-23`).

**Mechanism.** Harmony **prefix** on `MobilePartyAi.Tick`, patched via `AccessTools.Method`
(`:39-44`). Reads the private `_mobileParty` field through `AccessTools.Field` (`:37`,
`:65-75`) and, if the party is in exactly the proven-inconsistent state —
`DefaultBehavior == AiBehavior.DefendSettlement && TargetSettlement == null &&
TargetParty == null && ShortTermTargetParty == null` — returns false to skip that party's AI
tick entirely (`:86-93`). Self-heals when sync completes. Any exception inside the prefix is
swallowed and the tick runs normally (fail-open, `:94-98`).

**Patched members.** `TaleWorlds.CampaignSystem.Party.MobilePartyAi.Tick` (prefix). Read by
reflection: `MobilePartyAi._mobileParty`. Read: `MobileParty.DefaultBehavior`,
`.TargetSettlement`, `.TargetParty`, `.ShortTermTargetParty`, `.StringId`.

**Limitations.** Guards only the one proven inconsistent shape; any other half-synced shape
falls through to layer 2. Depends on the private field name `_mobileParty` — if renamed,
`PartyOf()` returns null and the prefix becomes a no-op (`:69`, `:82-84`).

**Self-test.** None registered. Application reports a count line only:
`[AI-GUARD] party-AI crash guard active on N method(s)` (`:57`).

### Party-AI crash guard — layer 2: `GetBehaviors` finalizer (forced Hold)

**README item** 6 · **Source** `Payload/PartyAiCrashGuard.cs` · **Class**
`PartyAiCrashGuard` · **Tag** `[AI-GUARD]` · **Config** none · **Scope** both

**Bug.** Any other escaping exception from `MobilePartyAi.GetBehaviors` during the campaign
party-AI tick is a crash to desktop (`Payload/PartyAiCrashGuard.cs:23-25`).

**Mechanism.** Harmony **finalizer** on `MobilePartyAi.GetBehaviors` (`:45-50`). Because
`GetBehaviors` returns results through by-ref parameters, the finalizer takes them by ref
and substitutes a safe answer: `bestAiBehavior = AiBehavior.Hold`, `behaviorObject = null`,
`bestTargetPoint = party.Position` (or `default(CampaignVec2)` when the party is
unresolvable) — "hold at current position this tick" instead of a crash (`:101-123`).
Returns null to swallow the exception; records a fire under `party-ai-guard`; the recovery
block is itself try/caught (`:117-121`).

**Patched members.** `MobilePartyAi.GetBehaviors` (finalizer; signature
`ref AiBehavior bestAiBehavior, ref IInteractablePoint behaviorObject, ref CampaignVec2 bestTargetPoint`).

**Limitations.** Masks the symptom for one tick per fire; the underlying half-synced state is
BT's to fix. If the by-ref parameter names change, the Harmony ref-binding no longer matches
and the patch does not apply.

**Self-test.** None registered; fires counted via `SelfHealing.RecordFire("party-ai-guard")`
(`:110`).

### Party-AI crash guard — layer 3: `HandleEncounterForMobileParty` finalizer

**README item** 6 · **Source** `Payload/PartyAiCrashGuard.cs` · **Class**
`PartyAiCrashGuard` · **Tag** `[AI-GUARD]` · **Config** none · **Scope** both

**Bug.** Second guarded organ of the same disease (crash 2026-08-19 ~20:28): per-party
encounter handling inside the campaign tick NREs on a half-synced party
(`Payload/PartyAiCrashGuard.cs:125-130`).

**Mechanism.** Harmony **finalizer** on `EncounterManager.HandleEncounterForMobileParty`
(`:51-56`, `:131-147`). Swallows the exception (returns null), records a fire under
`party-ai-guard`, logs the offending party's `StringId`. Safe by construction: skipping one
party's encounter handling for a tick is benign — it reruns next tick and the party heals
when its sync completes (`:127-130`).

**Patched members.** `TaleWorlds.CampaignSystem.EncounterManager.HandleEncounterForMobileParty`
(finalizer, parameter `mobileParty`).

**Limitations.** Symptom suppression only; the party stays half-synced until BT finishes
syncing it. Binds the parameter by the name `mobileParty` — a rename breaks the binding.

**Self-test.** None registered.

### Party-AI crash guard — skip-log coalescing

**README item** n/a · **Source** `Payload/PartyAiCrashGuard.cs` · **Class**
`PartyAiCrashGuard` · **Tag** `[AI-GUARD]` · **Config** none · **Scope** n/a
(instrumentation)

**Mechanism.** `LogSkip()` (`:149-167`) counts layer-1 skips in `_skipsSinceLog` and emits at
most one line per 5000 ms, reporting the party `StringId` plus "N skip(s) since last
report", then resets the counter. `Environment.TickCount` wraparound is handled by the extra
`now >= _lastSkipLogTick` term (`:155`). The whole body is try/caught.

**Limitations.** The 5000 ms window is a compile-time constant; the count is global, not
per-party.

**Self-test.** None.

### Encounter-request loop breaker

**README item** 7 · **Source** `Payload/EncounterLoopGuard.cs` · **Class**
`EncounterLoopGuard` · **Tag** `[ENCOUNTER-GUARD]` · **Config** none directly (but see
limitations: depends on `tracing`) · **Scope** co-op only

**Bug.** Infinite conversation/meeting loop (2026-08-19 20:07-20:08): after the player leaves
an encounter meeting, `PlayerEncounter.Finish` runs, and on the next campaign tick BT's sync
layer re-applies a stuck pending encounter request
(`BattleSyncBehavior.ProcessPendingClientEncounterRequests` →
`ApplyEncounterRequestNow` → `StartPartyEncounter` → `RestartPlayerEncounter`), reopening the
same `encounter_meeting` menu forever — the queue entry is never consumed. Method names were
taken from runtime stack traces in `CrashGuard.log` (`Payload/EncounterLoopGuard.cs:7-14`).

**Mechanism.** Rate-based loop breaker: Harmony **prefix** on every declared
`BattleSyncBehavior.ApplyEncounterRequestNow` overload, found by name over `GetMethods` with
`Public|NonPublic|Static|Instance|DeclaredOnly` (`:61-76`); the type is located with
`PeerDetection.FindCoopType("BattleSyncBehavior")` (`:55`). Constants: `TripCount=4`,
`WindowMs=15000`, `RetryAfterMs=60000`, `FinishChainMs=4000` (`:25-28`). A four-slot ring
buffer of timestamps (`:31`, `:114-117`) trips when four applications land inside 15 s; once
tripped, applications are suppressed and after 60 s of suppression exactly one retry is let
through so the system self-recovers, re-tripping if it has not (`:94-107`). Only
applications that closely follow a **local** `PlayerEncounter.Finish` (within
`FinishChainMs`, stamped by `NoteEncounterFinish`, `:37-45`, `:109-112`) count toward
tripping — the loop signature is finish → immediate re-application. Any exception in the
prefix returns true (fail-open, `:128-131`).

**Patched members.** BannerlordTogether `BattleSyncBehavior.ApplyEncounterRequestNow`
(prefix, all declared overloads). Documented in the chain but not patched:
`BattleSyncBehavior.ProcessPendingClientEncounterRequests`,
`EncounterManager.StartPartyEncounter`, `PlayerEncounter.RestartPlayerEncounter`. Stamp
source: `PlayerEncounter.Finish`, prefixed in `Payload/TracePatches.cs:44` →
`EncounterFinishPrefix` (`Payload/TracePatches.cs:185-189`), which calls
`EncounterLoopGuard.NoteEncounterFinish`.

`Apply()` returns immediately when `BattleSyncBehavior` is not found (BT absent or not loaded
yet) and is retried at `OnGameStart` (`Payload/PayloadEntry.cs:129`).

**Limitations.** Major: `_lastFinishTick` is stamped **only** by the `TracePatches`
`PlayerEncounter.Finish` prefix, and `TracePatches.Apply` runs only when
`tracing=true` (`Payload/PayloadEntry.cs:87-89`). With the shipped default `"tracing": false`,
`followsFinish` is always false (`:109-112`) and the breaker never trips. It also suppresses
the request rather than consuming the stuck queue entry, so the root defect — an unconsumed
pending-request entry in BT — remains. Trip state (`_tripped` / `_recentCalls`) is global,
not per-request; the constants are compile-time, not configurable.

**Self-test.** None registered. Application logs
`[ENCOUNTER-GUARD] encounter-request loop breaker active (N method(s))` (`:80`); fires
recorded as `SelfHealing.RecordFire("encounter-loop-guard")` (`:121`).

### Map-incident guard — root fix: `SiegeProgressChange` consequence lambda

**README item** 8 · **Source** `Payload/MapIncidentCrashGuard.cs` · **Class**
`MapIncidentCrashGuard` · **Tag** `[INCIDENT-GUARD]` · **Config** none · **Scope** both

**Bug.** Map-incident popup crash (field crash 2026-08-30 15:04, `crashreport1.html`):
clicking Confirm on an incident option NREs inside
`TaleWorlds.CampaignSystem.Incidents.IncidentEffect.SiegeProgressChange`'s consequence
lambda, which dereferences
`PlayerSiege.PlayerSiegeEvent.BesiegerCamp.SiegeEngines.SiegePreparations` with no null check
(`Payload/MapIncidentCrashGuard.cs:12-17`).

**Mechanism.** Harmony **prefix** (`SiegeConsequencePrefix`, `:213-246`) on exactly the
crashing lambda(s). Selection is by **IL inspection**, not lambda numbering: over
`IncidentEffect`'s nested types, any method whose name starts with `<SiegeProgressChange>b__`,
returns `List<TextObject>`, **and** whose IL actually calls `PlayerSiege.get_PlayerSiegeEvent`
is patched (`:68-80`, discriminator `:120-158`). If `SiegeChainIntact()` (`:160-175` — the
exact chain vanilla dereferences) holds, the prefix returns true and the real effect runs
untouched. Otherwise two distinct treatments, never a blanket downgrade:

- **(a) Co-op attach gap** — `FindLiveSiegeViaArmy()` (`:177-211`) probes `main.SiegeEvent`,
  `main.CurrentSettlement.SiegeEvent`, `main.AttachedTo.SiegeEvent`,
  `main.Army.LeaderParty.SiegeEvent`, `main.Army.LeaderParty.CurrentSettlement.SiegeEvent`
  and, on a hit, applies the exact vanilla effect — `prep.SetProgress(prep.Progress + amount)`
  plus vanilla's own localized report text
  `{=C0kUpB48}{?AMOUNT > 0}Increased{?}Decreased{\?} siege progress by {ABS(AMOUNT)}%.` with
  `AMOUNT = MathF.Round(amount*100f)` — so co-op keeps the full incident (`:221-235`).
- **(b) Siege genuinely over** — no live siege anywhere:
  `__result = Substitute() = "The siege has already ended."` (`:242-245`, `:261-265`).

The effect amount is read out of the vanilla lambda's own closure display class via the field
`amountGetter` (`Func<float>`) by reflection (`:248-259`).

**Patched members.** `IncidentEffect+<>c__DisplayClass…<SiegeProgressChange>b__N` (prefix;
only lambdas whose IL calls `PlayerSiege.get_PlayerSiegeEvent` — documented as `b__1`, the
consequence; `b__2`, the preview-text lambda, is deliberately left alone). Read:
`TaleWorlds.CampaignSystem.Siege.PlayerSiege.PlayerSiegeEvent`. Written:
`SiegeEvent.BesiegerCamp.SiegeEngines.SiegePreparations.SetProgress` / `.Progress`. Read by
reflection: the display-class field `amountGetter` of type `Func<float>`.

Branch (a) is co-op specific (a peer's party rides in a besieging army without being attached
to the besieger camp); branch (b) reproduces in pure vanilla singleplayer, with the popup
sitting open while the siege ends (`:29-31`).

**Limitations.** Branch (a) repairs the effect at the **effect site only** — every other
`PlayerSiege`-derived code path on that peer still reads null; the BT attach path is the real
fix, and the `[INCIDENT-GUARD] REPAIRED` lines are the field evidence
(`UPSTREAM_BUG_REPORT.md:117-124`). Incidents are still not synced between peers: an
incident's world effects apply only on the confirming peer's process
(`UPSTREAM_BUG_REPORT.md:126-128`). The amount read depends on the closure field literally
being named `amountGetter` (the name derives from the factory's parameter name) — a rename
throws `InvalidOperationException` and the guard falls back to the graceful skip
(`:240-245`). The vanilla report string is hard-copied, so an upstream text change diverges.

**Self-test.** `SelfHealing.RegisterTest(SelfTest)` at `:111`; result name
`map-incident-guard.contract` (`:309-337`). It pins three things: (1) the `IncidentEffect`
type and its `Consequence` method still resolve; (2) `ConsequenceFinalizer` is inert on a
null exception (returns null and leaves `__result` null); (3) the IL **discriminator still
discriminates** — it must find at least one lambda that calls `get_PlayerSiegeEvent` and at
least one that does not (`consequenceLambdas >= 1 && otherLambdas >= 1`), pinning both the
patched and the deliberately-untouched lambda.

### Map-incident guard — class net: `IncidentEffect.Consequence` finalizer

**README item** 8 · **Source** `Payload/MapIncidentCrashGuard.cs` · **Class**
`MapIncidentCrashGuard` · **Tag** `[INCIDENT-GUARD]` · **Config** none · **Scope** both

**Bug.** The bug class behind the siege crash: "incident option handlers assume the world
state that spawned the incident is still live on confirm" (`:37-41`). Any other stale-state
throw inside an incident effect is likewise a crash to desktop.

**Mechanism.** Harmony **finalizer** on `IncidentEffect.Consequence`, the single chokepoint
every incident effect flows through (`:83-89`, `:279-292`). Swallows the exception, records a
fire under `map-incident-guard`, logs the full exception object explicitly marked "root-fix
candidate", and ensures `__result` is a non-null empty `List<TextObject>` so the caller does
not then NRE.

**Patched members.** `IncidentEffect.Consequence` (finalizer, `ref List<TextObject> __result`).

**Limitations.** Deliberately a net, not a fix — every fire is logged as a root-fix candidate
per the mod's fire-tracking contract (`:39-41`). The player loses that incident's effect
silently apart from the log line.

**Self-test.** Covered by the shared `map-incident-guard.contract` test: the `inertOnNull`
assertion calls `ConsequenceFinalizer(null, ref untouched)` and requires it to return null and
leave `__result` null (`:313-314`).

### Map-incident guard — outer belt: `Incident.InvokeOption` finalizer

**README item** 8 · **Source** `Payload/MapIncidentCrashGuard.cs` · **Class**
`MapIncidentCrashGuard` · **Tag** `[INCIDENT-GUARD]` · **Config** none · **Scope** both

**Bug.** Same class, one layer out: a stale-state throw anywhere under the incident option
click handler's campaign entry point crashes the game (`:91-98`).

**Mechanism.** Harmony **finalizer** on `Incident.InvokeOption`, resolved by
`AccessTools.TypeByName("TaleWorlds.CampaignSystem.Incidents.Incident")` and patched only when
its return type is `List<TextObject>` — a shape check that disambiguates overloads
(`:92-98`). Swallows the exception, records a fire, logs "option closed without its effect
(root-fix candidate)", and normalizes `__result` to an empty list (`:294-307`).

**Patched members.** `Incident.InvokeOption` (finalizer, `ref List<TextObject> __result`) —
only the overload returning `List<TextObject>`.

**Limitations.** Outermost net: the incident option closes with no effect applied. A symptom
container only.

**Self-test.** Not directly pinned; the shared self-test covers `IncidentEffect`,
`Consequence` and the IL discriminator, not `InvokeOption`. Health is reported via
`Diag.Report("map-incident-guard", …)` at `:60`, `:103`, `:110`, `:116`, and the apply line
at `:107-109` prints `invokeOption=true/false`.

### Background-tick budget guard

**README item** 18 · **Source** `Payload/BackgroundTickBudgetGuard.cs` · **Class**
`BackgroundTickBudgetGuard` · **Tag** `[TICK-GUARD]` · **Config** none · **Scope** co-op,
effectively host-side

**Bug.** Whole-game freeze during host battles (field hang 2026-08-30 ~15:24, root-caused by
live debugger attach and repeated managed stack samples): BT's
`CoopSubModule.TryBackgroundCampaignTick` runs `Campaign.RealTick` + `Campaign.Tick` on every
application tick while the host is in a mission (`ShouldBackgroundTick`: active state is not
the map but a `MapState` is in the stack). The method has no time budget — when a campaign
tick becomes pathologically expensive (observed: a third army joining the player's ongoing
battle put `EncounterManager.HandleEncounters`, BT's encounter-hold checks and hourly-AI
catch-up into multi-second ticks with all 16 cores pegged) every frame drowns in background
campaign work and the game is unresponsive for minutes
(`Payload/BackgroundTickBudgetGuard.cs:8-19`; `UPSTREAM_BUG_REPORT.md:132-157`).

**Mechanism.** Harmony **prefix + postfix** pair on `CoopSubModule.TryBackgroundCampaignTick`
(`:69-71`). The postfix measures the call with
`Stopwatch.GetTimestamp()`/`Stopwatch.Frequency` (`:104`, `:116`); `ComputeBlockMs`
(`:88-95`) returns 0 at or under `BudgetMs=100` and `Math.Min(elapsedMs, MaxBlockMs=10000)`
above it; the prefix returns false while `Stopwatch.GetTimestamp() < _blockedUntilTimestamp`
(`:97-106`). Net effect: after an over-budget tick, background ticking pauses for as long as
that tick took (capped 10 s), guaranteeing the foreground roughly half of wall time. This is
not a disable — under sub-budget load the guard changes nothing. Skipping a call is safe by
construction because BT's own method starts with many unconditional early-outs (paused,
saving, not host), so callers already tolerate no-op ticks (`:20-25`). `_startTimestamp == 0`
is the sentinel telling the postfix its call was skipped by the prefix (`:112-115`). Tracks
`_worstMs` and `_throttledCalls` and rate-limits logging to at most once per 5000 ms with
wraparound handling (`:124-136`).

**Patched members.** BannerlordTogether `CoopSubModule.TryBackgroundCampaignTick` (prefix +
postfix). Documented but not patched: `CoopSubModule.OnApplicationTick`,
`CoopSubModule.ShouldBackgroundTick`, `Campaign.RealTick`, `Campaign.Tick`, and the hot-stack
members `EncounterManager.HandleEncounters`, BT's
`SuppressClientMirroredPartyHandleEncounterPatch.Prefix`,
`BattleSyncBehavior.CanApplyEncounterHoldThirdPartyCooldownCandidate`,
`AiEngagePartyBehavior.AiHourlyTick`, `FactionManager.IsAtWarAgainstFaction`.

`Apply()` returns silently when `BannerlordTogether.CoopSubModule` is absent — "vanilla needs
no guard" (`:57-61`) — and is retried at `OnBeforeInitialModuleScreen` for a late-loading BT
assembly (`Payload/PayloadEntry.cs:120`). BT's own method early-outs when not host.

**Limitations.** Treats the symptom (frame starvation), not the cause (an unbounded campaign
tick); the upstream fix is to bound per-frame cost or move the background tick off the
render-critical path (`UPSTREAM_BUG_REPORT.md:155-157`). `BudgetMs` and `MaxBlockMs` are
compile-time constants. The co-op background world falls behind while throttled. The throttle
is global static state, so it applies process-wide, and the postfix body is fully try/caught
(`:138-140`) so a measurement failure silently disables throttling for that call. If BT
renames `TryBackgroundCampaignTick` the guard reports a critical health failure and goes
inactive (`:64-67`).

**Self-test.** `SelfHealing.RegisterTest(SelfTest)` at `:76`; result name
`bg-tick-budget-guard.contract` (`:143-156`). Pins (1) target re-resolution — `CoopSubModule`
absent counts as pass/inert ("BT absent (vanilla) — inert"), present requires
`TryBackgroundCampaignTick` to still resolve; (2) the decision logic at four boundary values:
`ComputeBlockMs(100)==0`, `ComputeBlockMs(101)==101`, `ComputeBlockMs(3000)==3000`,
`ComputeBlockMs(120000)==MaxBlockMs(10000)`.

---

## Battle mode and payload entry

### Automatic vanilla/co-op battle switching (patch lift, stash, restore)

**README item** 14 · **Source** `Payload/BattleMode.cs` · **Class** `BattleMode` · **Tag**
`[BATTLE-MODE]` · **Config** `battleMode`, legacy `soloVanillaBattles` · **Scope** both

**Bug.** Hosting alone with BannerlordTogether installed, battles start broken: the co-op
mod's battle pipeline strips the player side out of missions — empty formations and a
`SetupTeams` NRE (proven 2026-08-18). Conversely, stripping the co-op battle patches while a
partner is connected means the partner's army never enters the authoritative battle and the
session is sabotaged.

**Mechanism.** At every battle chokepoint, decide "vanilla" or "co-op".

- **Vanilla:** for a fixed list of native battle/deployment/spawn methods, read
  `Harmony.GetPatchInfo(method)` (`Payload/BattleMode.cs:251`), record every **foreign**
  prefix/postfix/finalizer/transpiler into a static stash (owner, kind, `MethodInfo`,
  priority, `before[]`, `after[]` — `:65-73`, `:308-316`), then
  `harmony.Unpatch(method, HarmonyPatchType.All, owner)` once per distinct foreign owner
  (`:266`).
- **Co-op:** walk the stash, skip anything already present (`IsPresent`, `:321-347`), and
  re-apply each stashed patch under a `new Harmony(stashed.Owner)` with the original
  priority/before/after, dispatching kind 0→prefix, 1→postfix, 2→finalizer (5th `Patch` arg),
  3→transpiler (4th `Patch` arg) (`:193-206`).

No third-party code is read, copied or modified — only runtime patch metadata.

**Patched members.** The lift/restore target list (native, not patched by this mod — their
*foreign* patches are moved):

- `DefaultTroopSupplierProbabilityModel.EnqueueTroopSpawnProbabilitiesAccordingToUnitSpawnPrioritization`
- `MapEventSide.MakeReadyForMission`, `.OnTroopKilled`, `.OnTroopWounded`, `.OnTroopScoreHit`
- `OrderOfBattleCampaignBehavior.GetFormationDataAtIndex`, `.SetFormationInfos`
- `DefaultBattleMissionAgentSpawnLogic.OnSideDeploymentOver`
- `DeploymentMissionController.OnMissionTick`, `.FinishDeployment`, `.SetupAIOfEnemyTeam`
- `BattleInitializationModel.CanPlayerSideDeployWithOrderOfBattle`
- `BattleEndLogic.MissionEnded`, `.OnAgentRemoved`
- `BattleObserverMissionLogic.OnAgentRemoved`
- `OrderOfBattleVM.Initialize`, `.ExecuteBeginMission`, `.OnDeploymentFinalized`,
  `.RefreshValues`
- `SandboxBattleInitializationModel.GetAllAvailableTroopTypes`
- `BattleAgentLogic.OnAgentBuild`, `.CheckUpgrade`, `.OnAgentHit`, `.OnAgentRemoved`

`IsClient()==true` short-circuits straight to co-op (`:120-124`).

**Limitations.** (1) Scope is battle-mission methods only — campaign/map co-op machinery is
deliberately not listed (`:37-38`). (2) The stash is a static `Dictionary` (`:75`) and
`PayloadEntry` statics are fresh per hot-reload generation
(`Payload/PayloadEntry.cs:8-11`), so a payload reload while in vanilla mode loses the stash
and previously-lifted foreign patches can never be restored by that generation. (3) Only
patches present at the moment of a vanilla pass are stashed; a foreign patch applied later
while vanilla is active is caught only at the next decision. (4) Restores are made under
`new Harmony(owner)` — a new instance carrying the original owner id, not the foreign mod's
own instance. (5) The two richest chokepoints (`MissionState.OpenNew`,
`PlayerEncounter.StartBattle`) live inside `TracePatches` hooks
(`Payload/TracePatches.cs:89`, `:181`), which are applied only when `tracing=true`; with
tracing off the decision points are apply / module-screen / game-start / mission-init only.
(6) `EnumerateTargets` uses `DeclaredOnly`, so an inherited (non-overridden) implementation is
not enumerated.

**Self-test.** None. Unlike the repo convention (`Diag.Report` + `SelfHealing.RegisterTest`
per guard), `Payload/BattleMode.cs` and `Payload/PayloadEntry.cs` register no self-test and no
health report — a grep of `Payload/*.cs` shows every other guard does (e.g.
`Payload/ClanModeSoloFix.cs:54-55`) but these two files contain no `Diag.` or `SelfHealing.`
call.

### Fail-toward-co-op decision policy (`DecideAndApply`)

**README item** 14 · **Source** `Payload/BattleMode.cs` · **Class** `BattleMode` · **Tag**
`[BATTLE-MODE]` · **Config** `battleMode` · **Scope** both

**Bug.** An earlier policy treated an unreadable or ambiguous session state as "no session"
and went vanilla mid-session, stripping the co-op battle patches on a machine that was
actually in a session; the partner's army never entered the authoritative battle.

**Mechanism.** Precedence in `DecideAndApply` (`:107-144`): config `solo` → vanilla; config
`coop` → co-op; `PeerDetection.IsClient()==true` → co-op ("auto: we are a client in someone
else's session"); else `AnyRemotePeerConnected()`: `false` → vanilla ("auto: confidently no
session"), `true` → co-op ("auto: remote player connected"), `null` → co-op ("auto: state
unreadable — failing safe to co-op (battleMode=solo forces vanilla)"). Vanilla engages only on
a confident negative. The whole body is wrapped in a try/catch that logs
`[BATTLE-MODE] decide failed (<reason>)` (`:155-158`).

**Limitations.** A solo player whose reflection reads are unreadable stays in co-op mode and
still hits the empty-battle bug; the documented escape hatch is `battleMode=solo`
(`:131-132`).

**Self-test.** None in this file.

### Self-patch exclusion (`IsOwnOwner`)

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `BattleMode` · **Tag**
`[BATTLE-MODE]` · **Config** none · **Scope** both

**Bug.** Unpatching "all foreign patches" by owner would also rip out this mod's own guards,
because it sits on some of the same methods.

**Mechanism.** `IsOwnOwner(owner)` returns
`owner != null && owner.StartsWith("bltogether", StringComparison.Ordinal)` (`:94-97`) —
matching the per-generation Harmony ids `bltogether.crashguard.gen{N}` minted by the harness
(`Harness/HotReload.cs:359`) plus the legacy flat id. `StashKind` skips any patch whose owner
is ours or null/empty (`:286`).

**Limitations.** Prefix match on `bltogether` — a third-party mod whose Harmony id happened to
start with `bltogether` would be treated as ours and never lifted.

**Self-test.** None.

### Idempotent mode latch and change-only logging

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `BattleMode` · **Tag**
`[BATTLE-MODE]` · **Config** none · **Scope** both

**Bug.** `DecideAndApply` runs at four-plus chokepoints per session; naive logging would flood
`CrashGuard.log` and spam the player on screen every mission.

**Mechanism.** `EnsureVanilla`/`EnsureCoop` log only when they actually changed something
(`removed>0` / `restored>0`) or when the latched mode flips (`_lastVanilla != true` /
`!= false`) (`:168-176`, `:216-224`). The player-visible `Log.Screen` fires only when patches
actually moved.

**Self-test.** None.

### `guardconfig.json` reader, default-file writer and legacy key migration

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `BattleMode` · **Tag**
`[BATTLE-MODE]` · **Config** `battleMode`, `soloVanillaBattles` (legacy), `timeAlwaysFlows`
(written, not read here) · **Scope** both

**Bug.** Players have no config file on first run, and v2.0 shipped a different key name
(`soloVanillaBattles`) that would otherwise silently stop working.

**Mechanism.** `binDir = Path.GetDirectoryName(assembly.Location)`;
`moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."))`;
`configPath = moduleRoot/guardconfig.json` (`:353-355`). If absent, write the default
`{ "battleMode": "auto", "timeAlwaysFlows": true }` and return `auto` (`:356-361`). Otherwise
regex-scrape `"battleMode"\s*:\s*"(auto|solo|coop)"` (`:363`). Fallback: if
`"soloVanillaBattles"\s*:\s*false` matches, return `coop` with a legacy log line (`:369-374`).
Anything else → `auto`. The result is cached for the life of the payload generation in
`_configMode` via the `ConfigMode` property (`:79-89`).

**Limitations.** Regex scraping, not real JSON parsing — a commented-out or nested duplicate
key would match. Because the value is cached, editing `battleMode` needs a hot-reload, unlike
the `tracing` flag which is read fresh.

**Self-test.** None.

### Peer detection — packet-liveness fail-safe

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `PeerDetection` ·
**Tag** `[PEER-DETECT]` (type-lookup failures only) · **Config** none · **Scope** both

**Bug.** 2026-08-19 20:27: reflection into the co-op session reported "no remote player" while
BT packets were arriving every ~2 seconds; the mod went solo mid-session and the two players'
game speeds desynced.

**Mechanism.** Any traced BT packet handler stamps `PeerDetection.NoteCoopActivity()` →
`_lastActivityTick = Environment.TickCount` (`:402-405`); the only in-repo caller is
`Payload/TimeEnforcementGuard.cs:234`. `RecentCoopActivity()` is true when
`_lastActivityTick != 0 && now-last < 15000 && now >= last` (the `now >= last` term guards
`Environment.TickCount` wraparound) (`:407-416`). `AnyRemotePeerConnected()` returns true
immediately on recent activity, before any reflection (`:513-516`): packets arriving mean a
live session regardless of what reflection says.

**Limitations.** 15 s window; the stamp only happens on code paths that are actually traced or
patched by other guards, so with those guards absent the fail-safe never arms.

**Self-test.** None.

### Peer detection — tri-state session probe (`AnyRemotePeerConnected`)

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `PeerDetection` ·
**Tag** `[PEER-DETECT]` · **Config** none · **Scope** both

**Bug.** A boolean "is anyone connected" cannot express "I could not read the state", and
treating unreadable as false caused the mid-session false-alone above.

**Mechanism.** Returns `bool?` — true / false / null (unknown). Order (`:511-555`): recent
packets → true; `CoopSession` type missing → null (co-op mod absent or unreadable);
`IsClient==true` → true; then read `IsHost`, `IsClient`, `Server`. If `Server` is null,
return false **only** when `isHost==false && isClient==false`, otherwise null — "a null
Server with unreadable roles previously returned false and caused a mid-session false-alone
(2026-08-19 20:27) — that must be UNKNOWN" (`:531-533`). If `Server` is non-null, enumerate
`GameplayPeerIds` then `ConnectedPeerIds`; first element → true; a collection that was found
but empty → false; no collection found at all → null.

**Limitations.** Reads only the first of `GameplayPeerIds`/`ConnectedPeerIds` that resolves;
renamed BT members degrade to null (unknown), which the caller treats as co-op.

**Self-test.** None.

### Peer detection — session-state snapshot

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `PeerDetection` ·
**Tag** emitted by the caller (e.g. `[TIME-GUARD]`) · **Config** none · **Scope** both

**Mechanism.** `Snapshot()` builds
`isClient=? isHost=? server=null|set <GameplayPeerIds|ConnectedPeerIds>=<count> recentPackets=<bool>`
(`:418-454`), or `sessionType=missing`, or `snapshot failed: <msg>`. Consumed by other guards,
e.g. `Payload/TimeEnforcementGuard.cs:160`.

**Self-test.** None.

### Peer detection — BT type resolver (`FindCoopType`)

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `PeerDetection` ·
**Tag** `[PEER-DETECT]` · **Config** none · **Scope** both

**Bug.** Hard-referencing BannerlordTogether types would make this mod fail to load when BT is
absent or updated.

**Mechanism.** Scan `AppDomain.CurrentDomain.GetAssemblies()` for
`GetName().Name == "BannerlordTogether"`, call `GetTypes()` inside try/catch and on
`ReflectionTypeLoadException` fall back to `loadEx.Types` (partially loaded types are still
usable), match `Type.Name == simpleName`, and return null after the first matching assembly is
exhausted (`:457-491`). Failures log `[PEER-DETECT] type lookup failed for <name>`.

**Limitations.** Only searches an assembly literally named `BannerlordTogether`; matches on
**simple** name, so two same-named types in different BT namespaces resolve to whichever comes
first; the `SessionType` cache latches even a null result (`_searched`, `:497-503`), so a BT
assembly that loads after the first probe is never re-searched for `CoopSession`.

**Self-test.** None.

### Peer detection — shared BT-state accessors

**README item** n/a · **Source** `Payload/BattleMode.cs` · **Class** `PeerDetection` ·
**Tag** none (silent) · **Config** none · **Scope** both

**Mechanism.** One resolution and caching path shared by all guards:
`ReadCoopStaticBool(name)` for `IsHost`/`IsClient`/`IsActive` (`:557-562`);
`ReadCoopStaticString(name)` for e.g. the remote player's hero id (`:564-573`).
`ReadStaticMember` tries `GetProperty(Public|NonPublic|Static)` first, then `GetField`,
catch → null (`:586-602`); `ReadInstanceMember` does the same on an instance (`:604-621`).
`ReadStaticBool` unboxes only if `value is bool`, else null (`:583`).

**Limitations.** All failures are swallowed to null and nothing is logged when a member
disappears, so a BT rename shows up only as "unknown" downstream.

**Self-test.** None.

### Safe mode (global kill switch)

**README item** 27 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** `safeMode` · **Scope** both

**Bug.** If this mod itself causes a crash, a player has no way to disable it short of
deleting the module.

**Mechanism.** First thing after the build banner:
`if (GuardConfig.Bool("safeMode", false))` → log "SAFE MODE — all guards/fixes/tracers
DISABLED via guardconfig.json safeMode=true.", `Log.Screen` "SAFE MODE active — this mod is
doing nothing (guardconfig.json)", and **return** before any patch is installed
(`Payload/PayloadEntry.cs:31-36`).

**Limitations.** Uses the harness's cached `GuardConfig` (not the fresh-from-disk read used
for `tracing`), so it takes effect on the next payload generation or game start, not
instantly.

**Self-test.** n/a.

### Load-order contract: `MovementOrderTypeInitGuard.ApplyEarly` runs first

**README item** n/a · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** none · **Scope** both

**Bug.** Every battle crashes. Root cause: the `MovementOrder` struct is `beforefieldinit`, so
merely *preparing* a patch that references `Formation`/`OrderController` makes the CLR run
`MovementOrder`'s static constructor; if that happens while `Mission.Current` is null the type
initializer fails and the CLR **caches** the failure — the type is permanently poisoned for
the process.

**Mechanism.** `MovementOrderTypeInitGuard.ApplyEarly(harmony)` is called as the very first
statement of `Apply`, ahead of `harmony.PatchAll` and all other guards, with the reasoning
written into the comment (`Payload/PayloadEntry.cs:38-42`). Only then does
`harmony.PatchAll(typeof(PayloadEntry).Assembly)` install the attribute-based patches — "the
two deployment crash finalizers" (`:44-45`).

**Limitations.** Being load-time, it needs a fresh game launch to take effect; a hot-reload of
the payload happens after the type may already have been touched.

**Self-test.** n/a here — registered inside `MovementOrderTypeInitGuard`.

### Fresh-from-disk tracing flag (`FreshTracingFlag`)

**README item** 26 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** `tracing` · **Scope** both

**Bug.** The harness's `GuardConfig` caches `guardconfig.json` text for the whole game
session, so flipping `tracing=true` could not take effect without restarting the game — which
destroyed the live repro being traced.

**Mechanism.** Read `File.ReadAllText(GuardConfig.Path)` directly and regex
`"tracing"\s*:\s*(true|false)` with `RegexOptions.IgnoreCase`; on any failure fall back to
`GuardConfig.Bool("tracing", false)` (`:211-232`). Called once per `Apply`, so editing the
file and hot-reloading the payload turns tracers on without losing the session (`:77-81`).

**Limitations.** Only the `tracing` key gets this treatment; `safeMode` and `selfTest` still
come from the cached `GuardConfig`.

**Self-test.** n/a.

### Tracer bundle gating

**README item** 26 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** `tracing` · **Scope** both

**Mechanism.** Under `if (tracing)` (`:82-93`): `TracePatches.Apply`, `ControlTrace.Apply`,
`TimeTrace.Apply`, `CoopBattleTrace.Apply`, `CharacterCreationTrace.Apply`,
`MovementOrderInitProbe.Apply` ("origin probe for the MovementOrder type-init crash"),
`RoleTrace.Apply`, and `RuntimeDiagnostics.Enabled = true` ("memory/state heartbeat + rich
exception context"); then logs "tracing ENABLED (guardconfig tracing=true)".

**Limitations.** `BattleMode`'s mission-open and start-battle re-decisions live inside
`TracePatches` hooks (`Payload/TracePatches.cs:89`, `:181`), so those two chokepoints exist
only while tracing is on.

**Self-test.** n/a.

### Apply rethrows so the harness keeps the previous generation

**README item** n/a · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** none · **Scope** both

**Mechanism.** `catch (Exception ex) { Log.Info("FAILED to apply patches: " + ex); throw; }` —
"let the harness keep the previous generation on a failed apply" (`:108-112`). The harness
applies the new generation first and only swaps/unpatches the old one on success
(`Harness/HotReload.cs:368-370`).

**Limitations.** Patches installed before the throw are not rolled back by this file; the
harness's owner-string unpatch of the failed generation is what cleans up.

**Self-test.** n/a.

### Late-BT retry on the initial module screen

**README item** n/a · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** none · **Scope** both

**Bug.** BannerlordTogether's assembly can load *after* this payload, so guards that need BT
types silently no-op forever if they only try once at `Apply`.

**Mechanism.** `OnBeforeInitialModuleScreen` re-calls `ClientBootstrapFix.Apply` ("retry in
case the co-op assembly loaded late"), `ClanModeSoloFix.Apply`, `JoinSyncPauseEscape.Apply`,
`BackgroundTickBudgetGuard.Apply` (all "same late-BT-assembly retry (latched once applied)"),
`SiegeCommandGuard.RetryBt` ("hook BT's host player-down releases if BT loaded after us"),
`TimeEnforcementGuard.Apply`, and `BattleMode.DecideAndApply(reason "module-screen")`
(`:115-124`).

**Limitations.** `PeerDetection.SessionType` is **not** re-searched on this retry — its
`_searched` latch caches a null `CoopSession` from the first probe
(`Payload/BattleMode.cs:497-503`).

**Self-test.** n/a.

### Battle-mode chokepoint wiring

**README item** 14 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** `[BATTLE-MODE]` · **Config** none · **Scope** both

**Mechanism.** `BattleMode.DecideAndApply` is called with a reason string at `"apply"`
(`:99`), `"module-screen"` (`:123`), `"game-start"` (`:130`), `"mission-init"` (`:137`), plus
`"mission-open"` and `"start-battle"` from the tracer hooks
(`Payload/TracePatches.cs:89`, `:181`).

**Self-test.** n/a.

### Role tag on every log line (`RefreshRole`)

**README item** 26 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** role tags `H` / `C` / `S` · **Config** none · **Scope** both

**Bug.** Co-op logs from two machines are indistinguishable; you cannot tell whether a line
came from the host, the client, or a solo session.

**Mechanism.** `Tick()` → `RefreshRole()`, throttled to once per 5000 ms with a
`TickCount`-wraparound guard (`_lastRoleTick != 0 && now-_lastRoleTick < 5000 && now >= _lastRoleTick`):
`IsClient()==true` → `Log.SetRoleTag("C")`; else `AnyRemotePeerConnected()==true` → `"H"`;
else `"S"` (`:161-187`). The whole body is in a bare catch.

**Limitations.** `S` is also what an unreadable state yields on a host with no confirmed peer,
so the tag is a hint, not proof.

**Self-test.** n/a.

### Coalesced guard-fire summary (`ReportGuardActivity`)

**README item** 26 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** `GUARD ACTIVITY:` · **Config** none · **Scope** both

**Bug.** Per-fire logging from ~20 guards would drown the log; no logging at all leaves you
blind to which guards are actually firing.

**Mechanism.** `Tick()` → `ReportGuardActivity()`, throttled to once per 120000 ms (2 minutes,
same wraparound guard) and additionally suppressed unless `SelfHealing.FireSummary()` text
changed since last time (`:189-209`).

**Self-test.** n/a.

### Self-test gate and health summary at Apply

**README item** 25 · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** `MOD HEALTH:` · **Config** `selfTest` · **Scope** both

**Bug.** A silently-degraded guard (a member no longer resolvable after a game or BT update)
looks identical to a working one.

**Mechanism.** After wiring, logs `Diag.HealthSummary()`; if `GuardConfig.Bool("selfTest", false)`
it runs `SelfHealing.RunSelfTests()` (`:102-106`).

**Self-test.** This is the gate that runs every guard's registered self-test.

### Payload build stamp

**README item** n/a · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** (untagged) · **Config** none · **Scope** both

**Bug.** During hot-reload iteration it is easy to be looking at logs from the previous
payload build.

**Mechanism.** `PayloadBuild()` returns
`"build " + File.GetLastWriteTime(assembly.Location).ToString("HH:mm:ss")`, falling back to
`"(compiled in-memory)"` (`:234-244`); logged as `payload build HH:mm:ss applying on <harmony.Id>`,
where the Harmony id also reveals the generation number (`:29`).

**Self-test.** n/a.

### Per-generation Harmony and shared-state handles

**README item** n/a · **Source** `Payload/PayloadEntry.cs` · **Class** `PayloadEntry` ·
**Tag** none · **Config** none · **Scope** both

**Bug.** Guards that re-patch on the fly need the current generation's Harmony instance; a
stale one would patch under a dead owner id the harness has already unpatched.

**Mechanism.** `internal static Harmony Harmony` and `internal static ISharedState Shared`,
assigned at the top of `Apply` and documented as "read by guards that (re)patch on the fly
(TracePatches, BattleMode)… Per-generation because statics are fresh" (`:14-25`). Read at
`Payload/TracePatches.cs:89`, `:181`.

**Self-test.** n/a.

---

## Diagnostics and tracers

Six of the files in this area are gated on `tracing`: `TracePatches`, `ControlTrace`,
`CoopBattleTrace`, `CharacterCreationTrace`, `MovementOrderInitProbe`, `RoleTrace` — which
also sets `RuntimeDiagnostics.Enabled`. `safeMode=true` disables everything including the
load-time `MovementOrderTypeInitGuard`. None of these ten files registers a `SelfHealing`
test, so `selfTest` exercises none of them.

### Mission/menu/encounter chokepoint tracer

**README item** 26 · **Source** `Payload/TracePatches.cs` · **Class** `TracePatches` ·
**Tag** `[TRACE]` · **Config** `tracing` · **Scope** both

**Bug.** A village raid (or any unexpected event) suddenly drops the player into a 3D scene,
or the map menu switches to something the player never chose, and nothing in the log says
which code did it or who called it (`Payload/TracePatches.cs:14-31`).

**Mechanism.** Harmony void prefixes/postfixes on the six chokepoints every mission/menu
transition must pass through; each appends a `[TRACE]` line with formatted args plus a
filtered caller stack (`:34-47`, `:86-202`). Patching is by **method name over every declared
overload** (`PatchByName`, `:49-82`), so a game update that adds or changes an overload
degrades to a logged "no patchable method" instead of a crash (`:72-75`).

**Patched members.** `MissionState.OpenNew` (prefix, `:37`); `GameMenu.ActivateGameMenu`
(prefix, `:38`); `GameMenu.SwitchToMenu` (prefix, `:39`);
`EncounterManager.StartSettlementEncounter` (prefix, `:40`);
`EncounterManager.StartPartyEncounter` (prefix, `:41`); `MapEvent.CanPartyJoinBattle`
(postfix, `:42`); `PlayerEncounter.StartBattle` (prefix, `:43`); `PlayerEncounter.Finish`
(prefix, `:44`); `DefaultEncounterGameMenuModel.GetGenericStateMenu` (postfix, `:45`).

**Limitations.** The class header claims "Never changes behavior — every hook is a void
prefix/postfix" (`:15-16`), but three hooks do have side effects: `MissionOpenNewPrefix` calls
`BattleMode.DecideAndApply(…,"mission-open")` (`:89`), `EncounterStartBattlePrefix` calls
`BattleMode.DecideAndApply(…,"start-battle")` (`:181`) and `EncounterFinishPrefix` calls
`EncounterLoopGuard.NoteEncounterFinish()` (`:187`). Turning tracing on therefore changes when
battle mode is re-decided and is what feeds the encounter-loop guard. Noise filters make some
events deliberately invisible: settlement/party encounters and `CanPartyJoinBattle` log only
when the main party is an argument (`:105-129`, `:133-177`), and `GetGenericStateMenu` logs
only when its returned menu id changes (`:191-202`). Stacks are capped at 20 kept frames and
drop `HarmonyLib`, `BLTDeploymentCrashGuard` and `System.*` frames (`:264-283`); args are
`ToString()`'d and truncated at 80 chars (`:228-231`).

**Self-test.** None registered, and no `Diag.Report`; it self-reports only the count line
`[TRACE] tracer active on N method overload(s)` (`:46`).

### Battle command/ownership tracer and control-map dump

**README item** 26 · **Source** `Payload/ControlTrace.cs` · **Class** `ControlTrace` ·
**Tag** `[CONTROL]` · **Config** `tracing` · **Scope** both

**Bug.** In co-op sieges the client player sometimes receives command of the host's army, and
nothing records which native call handed control over (`Payload/ControlTrace.cs:11-17`).

**Mechanism.** Prefixes on every native control-handoff member plus a postfix on
`Mission.OnDeploymentFinished` that dumps the complete control map (`:29-46`, `:227-277`).
Types are resolved by string through `AccessTools.TypeByName` (`:53`) so the payload does not
hard-bind them. `DumpControlMap` walks `Mission.Teams` → each team's general,
`PlayerOrderController.Owner`, and each non-empty formation's `PlayerOwner`/`Captain`/
`IsAIControlled` (`:234-277`), and is callable with any reason string by other guards
(internal, `:234`).

**Patched members.** `Agent.set_Controller` (prefix, `:32`); `Mission.set_MainAgent`
(`:33`); `OrderController.set_Owner` (`:34`); `Formation.set_PlayerOwner` (`:35`);
`Team.set_GeneralAgent` (`:36`); `Team.AssignPlayerAsSergeantOfFormation` (`:37`);
`Mission.OnDeploymentFinished` (postfix, `:38`); `Formation.SetControlledByAI` (`:41`);
`Team.SetPlayerRole` (`:42`); `Team.DelegateCommandToAI` (`:43`); `Formation.TransferUnits`
(`:44`).

**Limitations.** `Agent.set_Controller` logs only transitions to
`AgentControllerType.Player` ("AI churn is noise", `:95-98`) and `SetControlledByAI` logs only
actual flips because the setter early-returns on an unchanged value (`:139-146`) — an AI→AI
re-assert is invisible. Every hook is wrapped in an empty catch (`:93-104` and similar), so a
broken reflection read silently produces no line. Stack depth is capped at 14 frames (`:381`).
The dump is local-machine truth, so host and client logs must be compared side by side.

**Self-test.** None registered; reports `[CONTROL] control tracer active on N method(s)`
(`:45`) and per-member "type not found" / "no patchable method" lines (`:56`, `:79`).

### Co-op battle-formation topology tracer

**README item** 26 · **Source** `Payload/CoopBattleTrace.cs` · **Class** `CoopBattleTrace` ·
**Tag** `[COOP-BATTLE]` · **Config** `tracing` · **Scope** co-op only

**Bug.** On a dedicated server with two gameplay clients it is unknown whether the authority
forms one shared battle (both client parties on one side) or two independent per-client
battles with enemy parties double-counted (`Payload/CoopBattleTrace.cs:9-16`).

**Mechanism.** Void prefixes on four BannerlordTogether internals resolved by simple type name
inside the assembly literally named `BannerlordTogether`
(`PeerDetection.FindCoopType`, `Payload/BattleMode.cs:456-490`; used at
`Payload/CoopBattleTrace.cs:37`, `:60`). Every emitted line is suffixed with a co-op topology
snapshot (`Topo()`, `:149-172`) so role-tagged, timestamped logs from the server and both
clients line up.

**Patched members.** BT `BattleSyncBehavior.SendEncounterRequest` (prefix, `:39`, `:96-101`);
`BattleSyncBehavior.ApplyClientStartedBattleLeaseState` (`:40`, `:103-126`);
`SpNativeBattleBehavior.StartLiveBattle` (`:41`, `:128-131`);
`SpNativeBattleBehavior.AttackLiveConsequence` (`:42`, `:133-136`). Inert when the BT assembly
is absent (`FindCoopType` returns null → `[COOP-BATTLE] type not found`, `:62-64`).

**Limitations.** Latched by a static `_applied` flag, so it applies once per payload
generation (`:26`, `:43-47`). Argument meaning is positional and unvalidated — `arg0`/`arg1`
are assumed attacker/defenderGhost (`:98-99`) and `arg0..arg3`
sessionId/authKey/leasedPartyIds/active (`:105-124`); a BT signature change silently mislabels
fields rather than failing. `Topo()` reads only static `CoopSession` members and returns `?`
for anything missing and an empty string if `CoopSession` itself is absent (`:151-193`).

**Self-test.** None registered; emits
`[COOP-BATTLE] battle-formation tracer active on N method(s)` only when at least one hook
landed (`:43-47`).

### Co-op session role-transition tracer

**README item** 26 · **Source** `Payload/RoleTrace.cs` · **Class** `RoleTrace` · **Tag**
`[ROLE]` · **Config** `tracing` · **Scope** dedicated authority / host primarily

**Bug.** The dedicated-authority role is set at launch from the command line
(`--coop-authority` → `CoopAuthorityRole.DedicatedGraphicalHost`), but loading a save through
the in-game menu appears to re-derive the role and drop dedicated mode ("switched to client
mode out of dedicated server mode") (`Payload/RoleTrace.cs:7-15`).

**Mechanism.** Prefix + postfix on `MBSaveLoad.LoadSaveGameData` bracket the load with a
before/after snapshot of eleven `CoopSession` members (`:44-59`, `:100-110`), and `Tick()`
re-snapshots at most once a second and logs only on change (`:69-98`), so the exact instant
and shape of the role drop is captured. `Tick` is wired from `PayloadEntry.Tick`
(`Payload/PayloadEntry.cs:148`).

**Patched members.** `TaleWorlds.Core.MBSaveLoad.LoadSaveGameData`, falling back to
`TaleWorlds.SaveSystem.MBSaveLoad` (prefix + postfix, `:44-58`). Reads by static reflection:
`CoopSession.{IsHost, IsClient, IsDedicatedAuthority, AuthorityRole, HostMode,
RequestedSessionRole, State, LocalGameplayPlayerCount, SharedSaveMode,
AuthorityAutoLoadSaveName, IsOwnedAuthorityProcess}` (`:23-28`, `:145-164`). Returns
immediately when `CoopSession` is not found (`:40-43`, `:74-76`).

**Limitations.** Only `LoadSaveGameData` is bracketed — a role change from any other path
shows up only as the ≥1 s tick diff (`:82-93`). The one-shot launch line
(`[ROLE] launch args coop-authority=…`) is emitted on the first tick, not at `Apply`
(`:77-81`). `LaunchedAsDedicated` matches only the exact tokens `--coop-authority` and
`--coop-dedicated-authority` (`:116-121`), so an `=value` or abbreviated form reads false. A
snapshot is a flat string, so member ordering, not semantics, decides "changed".

**Self-test.** None registered; logs
`[ROLE] role-transition tracer active (LoadSaveGameData hooks=N)` (`:61`).

### Character-creation lifecycle tracer and first-chance capture

**README item** 26 · **Source** `Payload/CharacterCreationTrace.cs` · **Class**
`CharacterCreationTrace` · **Tag** `[CHARGEN]` (plus throttle key
`CHARGEN-FC <ExceptionType> @ <Namespace.Type.Method>`, `:177`) · **Config** `tracing` ·
**Scope** both

**Bug.** 2026-09-04 field report: the banner-editor preview renders the character lying
sideways during new-character creation at co-op setup, with no exception surfaced
(`Payload/CharacterCreationTrace.cs:13-18`).

**Mechanism.** Prefix + **finalizer** on five `CharacterCreationState` lifecycle methods
(`:38-49`), with the patch shape `harmony.Patch(method, prefix, null, null, finalizer)`
(`:72`). The prefix names the method via `MethodBase __originalMethod` (`:94-97`);
`OnStageActivated` additionally logs the active stage's runtime type name and calls
`RuntimeDiagnostics.Mark("chargen-stage:<Stage>")` for a memory and native-scene snapshot per
stage (`:99-114`). The finalizer logs any exception with its full inner chain and **returns
it** (`:116-123`), so nothing is swallowed. Alongside the lifecycle hooks the file arms a
session-wide first-chance exception observer whose repeats are collapsed through
`TraceThrottle`.

**Patched members.** `CharacterCreationState.OnInitialize` (`:41`), `.OnActivate` (`:42`),
`.OnStageActivated` (`:43`), `.Refresh` (`:44`), `.FinalizeCharacterCreationState` (`:45`).

**Limitations.** Only the state machine is instrumented — the scene/agent-visuals/pose code
that renders the model is not patched, so the sideways model is caught only if something
throws inside a lifecycle call or is picked up by the first-chance capture. `StateType` is a
hard-coded string (`:30`); a namespace change silently yields `[CHARGEN] type not found`
(`:59`). Hard cap of 400 emitted first-chance events per payload generation (`:33-34`,
`:172-176`). Exceptions whose type name starts with `BLTDeploymentCrashGuard` are skipped
(`:163-166`), and any exception with no `SandBox`/`StoryMode`/`TaleWorlds` frame
(`TaleWorlds.Library` excluded) is skipped as "framework-internal noise" (`:167-171`,
`:219-246`) — pure-framework failures are invisible. `[ThreadStatic] _inHandler` stops the
handler observing its own throws (`:35-36`, `:154-158`), so re-entrant throws on the same
thread are dropped. The whole handler body is wrapped in an empty catch: "a tracer must never
take the game down" (`:187-191`). Documentation divergence: `CHANGELOG.md:20-24` (v1.3.2)
describes arming the observer "only while a character is being created… capped per
activation", whereas the shipped code arms it session-wide at `Apply` with one global cap.

**Self-test.** None registered; arming state is printed at load —
`[CHARGEN] character-creation tracer active on N method(s); session-wide first-chance
exception capture ARMED|NOT armed` (`:47-48`, `IsArmed` `:127-131`).

### Runtime diagnostics (memory and engine-state telemetry)

**README item** 26 · **Source** `Payload/RuntimeDiagnostics.cs` · **Class**
`RuntimeDiagnostics` · **Tag** `[DIAG]` · **Config** `tracing` · **Scope** both

**Bug.** Three 2026-09-04 symptoms were suspected to be one class, not three bugs: an
`AccessViolationException` in `NativeObject.Finalize` (native memory freed underneath), a
character rendered folded/sideways (native mesh/skeleton), and a `MovementOrder` NRE (managed
engine state null at a mission transition) — the unifying hypothesis being engine state or
native memory touched while half-initialized or already freed, possibly under memory or cache
pressure (`Payload/RuntimeDiagnostics.cs:8-21`).

**Mechanism.** A ~15 s heartbeat plus forced `Mark()` lines at every mission/scene transition
log working set, private bytes, managed heap, peak working set, GC gen0/1/2 counts, handle and
thread counts (`MemoryLine`, `:74-93`) alongside an engine snapshot: `Mission`
(mode/state/scene-null), `GameStateManager.ActiveState` type name, `Campaign` present
(`StateContext`, `:99-157`). Also provides `LiveGameStack(skip)` — the current thread's
game-only frames — used by the exception capture and the `MovementOrder` probe (`:159-196`).
`PayloadEntry` sets `RuntimeDiagnostics.Enabled = true` only under `tracing`
(`Payload/PayloadEntry.cs:91`); `Heartbeat`/`Mark` no-op when disabled (`:36-40`, `:59-63`).

**Patched members.** None. Reads `Mission.Current` + `.Mode` + `.CurrentState` + `.Scene`
(`:112-120`); `GameStateManager.Current.ActiveState` (`:130-138`); `Campaign.Current` (`:150`);
`Process.GetCurrentProcess().WorkingSet64` / `PrivateMemorySize64` / `HandleCount` /
`Threads.Count`, `GC.GetTotalMemory` / `CollectionCount` (`:78-87`, `:198-206`).

**Limitations.** Patches nothing — no behaviour change at all (`:22`). The heartbeat interval
is 15000 ms and `Mark()` resets the heartbeat clock, so a burst of marks suppresses the next
periodic line (`:29`, `:66`). Every engine read is individually try-caught and degrades to
`threw:<ExceptionType>` or `?`, because during a transition any of these accessors can itself
throw (`:95-98`, `:117-125`). Memory numbers are whole MB (integer division, `:208-211`), so
sub-MB drift is invisible.

**Self-test.** None registered.

### `MovementOrder` type-init origin probe

**README item** n/a · **Source** `Payload/MovementOrderInitProbe.cs` · **Class**
`MovementOrderInitProbe` · **Tag** `[MO-PROBE]` · **Config** `tracing` · **Scope** both/solo

**Bug.** Every battle load crashed with `TypeInitializationException` on `MovementOrder`, but
the logged throw (at `Formation.ResetAux` inside `Mission.AfterStart`, where `Mission.Current`
is already live) is a **cached re-throw** of an earlier first-touch failure — .NET runs a type
initializer once, caches the failure, and every later access re-throws the original exception
without re-running the constructor. So only the collateral had ever been captured, never the
origin (`Payload/MovementOrderInitProbe.cs:7-17`).

**Mechanism.** Patch the **instance constructor** `MovementOrder..ctor(MovementOrderEnum)` —
whose first-ever call happens inside the static constructor, i.e. exactly at the origin. A
prefix logs, for the first 12 constructions, the enum value, whether `Mission.Current` is
null, the memory line and the live game stack (`:52-71`); a finalizer logs the exception at the
instant it is really thrown with state context, memory and live stack, and returns it
unchanged so the crash still surfaces as-is (`:73-93`).

**Patched members.** `TaleWorlds.MountAndBlade.MovementOrder..ctor(MovementOrder.MovementOrderEnum)`
— prefix + finalizer via `AccessTools.Constructor` (`:34`, `:40-43`).

**Limitations.** Capped at the first 12 constructions — "the six defaults + a few real orders
is enough to see the pattern" (`:27-28`, `:56-60`); later constructions are invisible.
Diagnostic only: it never prevents the crash (that is `MovementOrderTypeInitGuard`'s job) and
explicitly never swallows (`:92`). If the constructor signature changes, the probe logs
`[MO-PROBE] MovementOrder..ctor(MovementOrderEnum) not found — probe inactive` and does nothing
(`:36-39`). Because it runs under `tracing`, i.e. after `MovementOrderTypeInitGuard.ApplyEarly`
has already forced a successful init (`Payload/PayloadEntry.cs:42` vs `:89`), on a normal load
the origin construction has already happened before the probe is installed.

**Self-test.** None registered; logs
`[MO-PROBE] MovementOrder ctor origin probe active (logs first 12 constructions + any throw)`
(`:44`).

### `MovementOrder` type-init guard (load-time fix)

**README item** n/a · **Source** `Payload/MovementOrderTypeInitGuard.cs` · **Class**
`MovementOrderTypeInitGuard` · **Tag** `[MO-INIT]` (`Payload/MovementOrderTypeInitGuard.cs:52,
64, 69, 72, 110, 128`) · **Config** none · **Scope** both/solo

**Bug.** The poisoned-type-initializer crash described in the probe entry above: the CLR
caches a failed `MovementOrder` static-constructor run, permanently poisoning the type for the
process.

**Mechanism.** `ApplyEarly(harmony)` runs as the very first statement of `PayloadEntry.Apply`,
ahead of `PatchAll` and every other guard (`Payload/PayloadEntry.cs:38-42`). It forces the type
to initialize safely — via `RunClassConstructor` plus a transpiler that rewrites the
`Mission.Current` / `get_CurrentTime` pair inside `MovementOrder..ctor` — and reports what it
did in the load log.

**Patched members.** `MovementOrder..ctor` (transpiler on the
`get_Current`;`get_CurrentTime` site pair); `RuntimeHelpers.RunClassConstructor` on
`MovementOrder` (called, not patched).

**Limitations.** If the type was already poisoned before the payload loaded,
`RunClassConstructor` rethrows and the guard logs
`[MO-INIT] MovementOrder was ALREADY poisoned before this guard could patch it (origin
earlier than payload load) … the fix must move into the harness SubModule` — the fix cannot
help that session and must move earlier (`:36-39`, `:67-71`). If the game changes the
constructor IL, the transpiler patches zero sites and logs
`[MO-INIT] transpiler found no Mission.Current.CurrentTime site in MovementOrder..ctor (game
changed?) — leaving ctor unmodified` (`:108-111`), leaving the crash unfixed but not worsened.
Only the `get_Current`;`get_CurrentTime` **pair** is matched — a `get_Current` stored to a
local first would not be rewritten (`:97-99`). Being a load-time fix it needs a fresh game
launch, not a payload hot-reload.

**Self-test.** No `SelfHealing` test. The load log is the oracle and deliberately
disambiguates the two hypotheses: "initialized safely (patched N site(s))" = fix active and
crash prevented, versus "ALREADY poisoned before guard" = origin earlier than payload load
(`:36-39`, `:64-71`).

### Coalescing tracer emitter (`TraceThrottle`)

**README item** 26 · **Source** `Payload/TraceThrottle.cs` · **Class** `TraceThrottle` ·
**Tag** `[repeat]` (rollup line prefix; the full first line carries the caller's own tag, e.g.
`[TIME]` or `[CHARGEN]`) · **Config** none · **Scope** both

**Bug.** 2026-09-04 incident: with tracing on at the co-op setup menu, BannerlordTogether's
`EnforcePlaySpeed` retries `UnstoppablePlay` every tick while the time guard blocks the write,
so the `[TIME]` tracer logged that blocked attempt — with a full stack — roughly 60 times a
second, filling the 8 MB log in minutes and rotating the real co-op-setup evidence off the end
(`Payload/TraceThrottle.cs:6-14`; `CHANGELOG.md:5-12`).

**Mechanism.** `Emit(key, message)` logs the first occurrence of a key in full (with its
stack) and counts identical repeats, flushing at most once per 5000 ms window as
`[repeat] <key> ×N in Ys (identical, collapsed)` (`:38-84`). The dictionary is
Ordinal-compared, lock-protected and bounded at 512 keys — on pathological cardinality it
clears rather than growing forever (`:29-32`, `:54-58`). `Reset()` drops all runs (e.g.
between missions) so counts do not span unrelated states (`:86-93`).

**Patched members.** None. Consumed by `Payload/TimeTrace.cs:123` and
`Payload/CharacterCreationTrace.cs:186`.

**Limitations.** Ordering with plain `Log.Info` lines is best-effort by design: a run's tail
count flushes on its next repeat or window, not instantly — "which is exactly the tradeoff
that stops the flood" (`:34-37`). A repeat that never recurs after the window never gets its
final count flushed. `Environment.TickCount` wrap (~24.9 days) is handled only by a
negative-delta guard (`:64-65`). A `MaxKeys` overflow clears all counters, losing in-flight run
counts (`:54-57`). It deliberately lives in the payload, not the harness `Log`, because the
harness DLL is locked while the game runs — so this fix could land by hot-reload with no
restart (`:20-21`); statics are fresh per payload generation, so a reload starts clean (`:18`).

**Self-test.** None registered.

### Log streamer (`filebin.net` upload)

**README item** 26 · **Source** `Payload/LogStreamer.cs` · **Class** `LogStreamer` · **Tag**
`[STREAM]` · **Config** `logStreamBin`, plus the sidecar file `logstream.txt` in the module
root · **Scope** both

**Bug.** Diagnosing a co-op bug needs both machines' logs, but asking the other player to find
and send a log file every time is friction that loses the evidence
(`Payload/LogStreamer.cs:8-17`).

**Mechanism.** When a bin id is configured, a tick-driven uploader POSTs the log to
`https://filebin.net` roughly once a minute, but only when the file has **grown** since the
last upload (`:92-124`). The bin id comes from `logstream.txt` in the module root first, else
from a regex over `guardconfig.json`'s `logStreamBin` (`:44-74`); the installer writes
`logstream.txt` from the `BLTGUARD_BIN` environment variable (`:12-14`; `install.cmd:62-64`).
Upload runs on a `ThreadPool` worker, fully try-caught, with TLS 1.2 forced on, custom `bin`
and `filename` headers, 120 s timeouts, and a role+machine-tagged filename
`blt-<RoleTag>-<MachineName>.log` so server and client uploads are distinguishable
(`:126-182`, `:150-159`).

**Patched members.** None; driven from `PayloadEntry.Tick` (`Payload/PayloadEntry.cs:149`).
Reads `Log.CurrentPath` and `Log.RoleTag` from the harness (`:106`, `:150`) and calls
`Log.Screen` on first success (`:171`).

**Limitations.** Uploads only the last 2 MB of the log — recent diagnostics live at the end,
and a full multi-MB log blew the request timeout in a live test at 21:16:08 (`:130-132`) — so
evidence older than the tail is never streamed. Minimum 60 s between uploads (`:101-104`) and
skipped entirely if length is unchanged (`:112-115`), so the final seconds before a crash may
never be uploaded. Bin ids are validated as 4–63 characters of letters, digits, `-` and `_`
for `logstream.txt` (`:76-90`) and by the same character class in the `guardconfig` regex
(`:63`). The file is opened `FileShare.ReadWrite` so the live logger keeps writing (`:134`).
Failures log `[STREAM] upload failed: …` and are otherwise ignored (`:174-177`). Uploaded logs
are **public** on filebin.net, and the machine name is included (sanitized, `:184-195`).

**Self-test.** None registered; announces once with the exact URL —
`[STREAM] first upload done: https://filebin.net/<bin>/<file> (~every 60s while the log grows)`
plus an on-screen "log streaming active" (`:167-172`).

---

## Gameplay and UI guards

### Illness death guard

**README item** 11 · **Source** `Payload/IllnessDeathGuard.cs` · **Class**
`IllnessDeathGuard` (Diag component `illness-death-guard`) · **Tag** `[NOSICK]` · **Config**
`noSickness` (default true, `GuardConfig.Bool` at `:38`) · **Scope** both, per-machine

**Bug.** Once the main hero reaches `BecomeOldAge` (55) the vanilla `AgingCampaignBehavior`
rolls a daily old-age death chance; on a hit the hero "Caught Illness", ill days tick up, past
day 3 HP drains 5%·days per day, and at ≤1 HP `KillMainHeroWithIllness` kills the player's
hero. In co-op that permanently ends one player's shared campaign (`:9-16`).

**Mechanism.** Two Harmony prefixes, both resolved by name via `AccessTools`.

1. `AgingCampaignBehavior.IsItTimeOfDeath` prefix returns false for `Hero.MainHero` only — the
   local main hero never rolls the death at all, so the illness is never caught (root cause,
   not a symptom). NPC lords age and die normally (`:79-100`).
2. `AgingCampaignBehavior.DailyTickHero` prefix **cures** an already-ill main hero (an old
   save): sets `Campaign.Current.MainHeroIllDays = -1` and, if
   `hero.DeathMark == KillCharacterActionDetail.DiedOfOldAge`, clears it to `None` through the
   private setter via `AccessTools.PropertySetter(typeof(Hero),"DeathMark")?.Invoke(...)` so
   the `ApplyByDeathMark` branch at the top of `DailyTickHero` cannot finish the kill — then
   returns **true** so vanilla still runs the hero as healthy (`:103-134`).

"Each machine protects its own player, so both co-op players are covered by running this mod"
(`:26-27`). Only `Hero.MainHero` on the local machine is affected; every other hero passes
through (`:83`, `:107`).

**Patched members.** `AgingCampaignBehavior.IsItTimeOfDeath` (prefix);
`AgingCampaignBehavior.DailyTickHero` (prefix). Written by reflection: `Hero.DeathMark`
private setter. Written directly: `Campaign.Current.MainHeroIllDays`.

**Limitations.** The guard goes inactive with a log line if either `AgingCampaignBehavior`
method fails to resolve (`:41-46`). `noSickness=false` disables it with `Diag ok=true`,
"disabled by config" (`:47-52`). Only the main hero is protected; companions and NPCs still
die of old age. Both prefixes swallow their own exceptions and pass through (return true) on
error (`:96-99`, `:129-133`). Design constraint: it must **not** skip `DailyTickHero`
wholesale, because that is exactly the third-party NoSickness mod's bug — aging and
come-of-age events skipped, ill flag never cleared (`:20-23`). `CHANGELOG.md` additionally
records that the fix stands down entirely if the standalone NoSickness mod is present.

**Self-test.** `illness-death-guard.contract` — re-resolves both targets by name at test time
(deliberately not reusing the apply-time resolve, so a rename or move reddens the test) and
proves both prefixes pass through (return true) for a null hero, the only input testable
outside a campaign (`:136-148`).

### Marriage barter guard (atomic dowry)

**README item** 10 · **Source** `Payload/MarriageBarterGuard.cs` · **Class**
`MarriageBarterGuard` (Diag component `marriage-barter-guard`) · **Tag** `[MARRIAGE-GUARD]` ·
**Config** none · **Scope** co-op only, both roles

**Bug.** Money loss under BannerlordTogether: BT's `MarriageFinalBarterApplyPatch` suppresses
the native marriage inside the barter and routes it to host validation, but the sibling
barterables — the gold dowry you pay — apply natively in the same
`BarterManager.ApplyAndFinalizePlayerBarter` loop. When BT's gate then rejects (for example
"clan mode is synchronized" while its sync is still pending) the gold is gone and no marriage
happened (`:11-17`).

**Mechanism.** Harmony **prefix** on `BarterManager.ApplyAndFinalizePlayerBarter` that cancels
the whole barter (returns false) before anything applies, but only under a three-way AND:
(a) `barterData.GetOfferedBarterables()` contains a `MarriageBarterable`; (b) a BT session is
active — `PeerDetection.ReadCoopStaticBool("IsActive") == true`; (c) BT's live clan mode still
reads `Unknown` — `ClanModeSoloFix.ReadLiveMode() == 0`. Otherwise it passes through. Logs the
offerer and other hero names and puts an on-screen line explaining why (`:54-98`).

**Patched members.**
`BarterManager.ApplyAndFinalizePlayerBarter(Hero offererHero, Hero otherHero, BarterData barterData)`
(prefix).

**Limitations.** Blocks the marriage rather than making it succeed — the player must retry once
clan sync lands. If `ReadLiveMode()` returns null (BT unreadable) it deliberately lets the
barter apply (`:83-87`). If BT fixes its ordering upstream the blocking condition simply never
occurs again (`:26-27`). The `Unknown==0` comparison hard-codes BT's obfuscated enum value
`af.bI = 0` (`:84`). Prefix errors pass through (`:94-97`). It self-disables entirely when no
BT session is active (`:79-82`); `ClanModeSoloFix` heals the solo case, so this should never
fire alone.

**Self-test.** `marriage-barter-guard.contract` — re-resolves
`BarterManager.ApplyAndFinalizePlayerBarter` and asserts `Prefix(null,null,null)` returns true
(pass-through on a null barter) (`:112-120`).

### Conversation-camera crash guard

**README item** 3 · **Source** `Payload/ConversationCameraCrashGuard.cs` · **Class**
`ConversationCameraCrashGuard` (Diag component `conversation-camera-guard`) · **Tag**
`[CONVO-CAM]` · **Config** none · **Scope** both/solo

**Bug.** Crash to desktop during the marriage proposal (field crash 2026-08-21 16:39).
`SandBox.View.Missions.MissionConversationCameraView.MakeSpeakerLookToListener` dereferences
the speaker/listener conversation agents; when one is removed mid-conversation — for example
the spouse's state changing the moment a BT-routed marriage applies — the camera tick NREs and
takes the game down (`:7-11`).

**Mechanism.** Harmony **finalizers** (no prefix, postfix or transpiler) on both
`MakeSpeakerLookToListener` and `UpdateAgentLooksForConversation`, resolved by name off
`AccessTools.TypeByName("SandBox.View.Missions.MissionConversationCameraView")`. The finalizer
returns null on a non-null `__exception`, swallowing it: that frame's camera look update is
skipped instead of crashing, and the conversation ends or the camera recovers next tick
(`:19-66`).

**Patched members.** `MissionConversationCameraView.MakeSpeakerLookToListener` (finalizer);
`MissionConversationCameraView.UpdateAgentLooksForConversation` (finalizer).

**Limitations.** Self-disabling by construction: a no-op unless the exception actually occurs,
so if TaleWorlds or BT fix the null it is permanently inert and retirable — visible as
never-fired in the health report (`:13-15`). Patches only the methods that resolve; zero
resolved means the guard is inactive with `Diag ok=false` (`:40-45`). Suppresses **all**
escaping exceptions from those two methods, not only NREs (`:57-66`). Only a camera-look
update is lost, so the visual cost is one frame.

**Self-test.** `conversation-camera-guard.contract` — re-resolves the view type plus
`MakeSpeakerLookToListener` and asserts `Finalizer(null) == null` (inert on a null exception,
no fire recorded) (`:68-77`).

### Dead-hero reactivation fix — caller fix

**README item** 2 · **Source** `Payload/DeadHeroReactivationFix.cs` · **Class**
`DeadHeroReactivationFix` (Diag component `dead-hero-return-fix`) · **Tag** `[DEADHERO]` ·
**Config** none · **Scope** both/solo

**Bug.** Issue-quest crash to desktop (field crash 2026-08-21 22:25): clicking OK on an issue
popup runs `IssueManager.MakeAlternativeTroopsReturn`, which loops the troops you sent as an
alternative solution and calls `Hero.ChangeState(Active)` on every hero among them without
checking `IsAlive`. If a companion died while away, the game reactivates a corpse (`:9-16`).

**Mechanism.** Harmony **prefix** (void, does not skip the original) on
`IssueManager.MakeAlternativeTroopsReturn(TroopRoster)` that calls
`roster.RemoveIf(IsDeadHeroElement)` before the original runs. Dead companions simply do not
return — correct, they are dead — and living troops return exactly as before. This fixes the
buggy data flow and stops a dead hero being added to the party roster (`:21-25`, `:69-89`).
The predicate is
`Character != null && Character.IsHero && Character.HeroObject != null && !HeroObject.IsAlive`,
wrapped in try/catch returning false — "never remove on uncertainty" (`:91-104`).

**Patched members.** `IssueManager.MakeAlternativeTroopsReturn(TroopRoster)` (prefix);
`TroopRoster.RemoveIf(predicate)` (called, not patched).

**Limitations.** Type and exact-signature resolve by name; missing means the caller fix is
inactive with `Diag ok=false`, though the invariant fix below still applies (`:47-56`). A
roster-clean exception is logged and the original is still allowed to run (`:86-88`). Fixes
only this caller — the generic class is covered by the sibling invariant fix.

**Self-test.** `dead-hero-return-fix.contract` — re-resolves
`IssueManager.MakeAlternativeTroopsReturn(TroopRoster)` by exact signature and asserts the
predicate is safe (returns false, does not throw) on `default(TroopRosterElement)`, i.e. an
element with no character (`:157-168`).

### Dead-hero reactivation fix — domain invariant

**README item** 2 · **Source** `Payload/DeadHeroReactivationFix.cs` · **Class**
`DeadHeroReactivationFix` · **Tag** `[DEADHERO]` · **Config** none · **Scope** both/solo

**Bug.** The general class behind the caller bug: any code path may attempt a dead-to-`Active`
hero transition.

**Mechanism.** Harmony prefix on `Hero.ChangeState` blocking a dead-to-`Active` transition.
It deliberately does not block a legitimate revive: "A legitimate revive clears the dead state
first, so IsDead is already false and this never blocks it" (`:28-29`).

**Patched members.** `Hero.ChangeState` (prefix).

**Limitations.** Prefix errors are logged and the original is allowed to run (`:143-146`).
Resolve failure means the invariant is inactive with `Diag ok=false` (`:113-118`). Both halves
self-disable in effect — once TaleWorlds stops feeding dead heroes into these paths the
prefixes never intervene, visible as never-fired in the health report (`:30-31`).

**Self-test.** `dead-hero-activate-invariant.contract` — re-resolves `Hero.ChangeState` and
asserts `ChangeStatePrefix(null, Hero.CharacterStates.Active)` returns **true** (lets the
original run, never throws, on a null instance) (`:170-179`).

### Clan-screen crash guard

**README item** 4 · **Source** `Payload/ClanScreenCrashGuard.cs` · **Class**
`ClanScreenCrashGuard` (Diag component `clan-screen-guard`) · **Tag** `[CLAN-GUARD]` ·
**Config** none · **Scope** primarily co-op client; the finalizer is unconditional and
protects host and solo too

**Bug.** Community-reported "crash when clicking the clan tab (especially after becoming a
mercenary)" in BannerlordTogether co-op. `GauntletClanScreen.CreateDataSource` builds
`ClanManagementVM` over the clan/party graph, which on a client can be half-synced or hold
host-mirrored values, producing an NRE that crashes when the screen opens (`:6-13`).

**Mechanism.** Harmony **finalizer** on `SandBox.GauntletUI.GauntletClanScreen.CreateDataSource`.
On a non-null `__exception` it records a fire, logs, shows an on-screen line, then **recovers**
rather than merely swallowing: it resolves `TaleWorlds.ScreenSystem.ScreenManager` by name and
invokes the static no-arg `PopScreen()` inside its own try/catch, popping back to the map, and
returns null so there is no crash to desktop (`:22-66`). The header notes BannerlordTogether
uses the same finalizer/preflight pattern for the kingdom screen
(`KingdomArmyUiPreflightPatch`) (`:11-12`).

**Patched members.** `GauntletClanScreen.CreateDataSource` (finalizer);
`ScreenManager.PopScreen()` (invoked by reflection on recovery).

**Limitations.** Self-disabling: does nothing unless `CreateDataSource` throws; if BT or
TaleWorlds fix the sync/null bug it is permanently inert and "can be retired" (`:14-18`).
Suppresses any exception type, not only NRE. The clan screen simply closes — the player still
cannot use the clan tab in that state. A `PopScreen` failure is silently ignored (`:62-64`).

**Self-test.** `clan-screen-guard.contract` — re-resolves `GauntletClanScreen` plus
`CreateDataSource` (the thing that breaks on a game or BT update) and asserts
`CreateDataSourceFinalizer(null)` is inert (returns null, records no fire) (`:68-81`).

### Stealth hideout advisor and command guarantee

**README item** 21 · **Source** `Payload/StealthHideoutAdvisor.cs` · **Class**
`StealthHideoutAdvisor` (Diag component `stealth-hideout-advisor`) · **Tag** `[STEALTH]` ·
**Config** none · **Scope** both/solo

**Bug.** Field report 2026-09-01: "spawned me into a main camp not as myself but as a soldier
and I cannot command my army". Decoded from the installed build's IL this is **vanilla
design**, not a bug: `HideoutAmbushMissionController.AfterStart` spawns your hero then
re-dresses it in `Hero.StealthEquipment` with the enemy's clothing colours
(`UpdateSpawnEquipmentAndRefreshVisuals`) — the "soldier" look is your disguise, and the
control trace confirms `MainAgent` is your hero; the mission starts in stealth mode with a
"locate the main camp" objective, troops held back and orders withheld by design (`:8-21`).

**Mechanism.** Harmony **postfix** (parameterless) on
`HideoutAmbushMissionController.AfterStart` that logs and shows an on-screen explainer the
moment a sneak-in starts, so nobody thinks the game broke: "SNEAK-IN: you are disguised in
your stealth outfit — find the main camp to spring the ambush; your troops and orders arrive
when the fight starts" (`:43-48`, `:69-79`). Pure advisory — it changes no game state. The
companion half guarantees command at the stealth-to-battle transition
(`ChangeHideoutMissionModeToBattle`).

**Patched members.**
`SandBox.Missions.MissionLogics.Hideout.HideoutAmbushMissionController.AfterStart` (postfix);
`HideoutAmbushMissionController.ChangeHideoutMissionModeToBattle` (the transition the command
guarantee re-resolves).

**Limitations.** If the controller type does not resolve (an older game build without the
stealth hideout) `Apply` returns silently with no `Diag` report at all (`:37-41`). All
reflection is by name because `SandBox` is not a compile-time reference (`:27`). The postfix
body is wrapped in a bare catch that swallows everything (`:76-78`). The command guarantee
fires only at the named transitions; a later ownership loss is not re-repaired. Missing
transitions are skipped, the patched count is reported in the `Diag` detail, and
`Diag ok = patched > 0` (`:58-59`). The whole postfix is inside a try/catch that logs and
returns (`:114-117`). It does not change the stealth phase itself — orders remain withheld
until the ambush is sprung, by design (`:21`).

**Self-test.** `stealth-hideout-advisor.contract` — asserts the ambush controller type resolves
and that both `AfterStart` and `ChangeHideoutMissionModeToBattle` re-resolve; anything else
reads "controller/transitions not resolved (game update?)" (`:120-128`).

### Clan-party creation advisor

**README item** 22 · **Source** `Payload/ClanPartyCreationAdvisor.cs` · **Class**
`ClanPartyCreationAdvisor` (Diag component `clan-party-advisor`) · **Tag** `[CLAN-PARTY]` ·
**Config** none for the observability half (always on); `partyTroopsOnCreate` gates the
auto-open half · **Scope** both/solo — BannerlordTogether does not touch this path
(`CHANGELOG.md:93`)

**Bug.** Field report 2026-09-01: "I made a party and it didn't allow me to add anyone". The
clan-parties leader popup greys out cards and disables the button with reasons the player
cannot see anywhere in the log (`:14-27`).

**Mechanism.** Two Harmony **postfixes** on `ClanPartiesVM`, all reflection with no
compile-time reference to `ViewModelCollection`.

1. `GetCanCreateNewParty` postfix reads `__result` and the `TextObject` `disabledReason` and,
   when `__result` is false, logs the exact disabled reason plus live context: war-party use
   (`Clan.PlayerClan.WarPartyComponents.Count` + "/" + `WarPartyLimit`),
   `Hero.MainHero.Gold`, and
   `Campaign.Current.Models.ClanFinanceModel.PartyGoldLowerThreshold` (`:78-83`, `:103-117`,
   `:277-300`).
2. `GetNewPartyLeaderCandidates` postfix **enumerates the vanilla result for logging only** —
   never replaces it — printing each candidate's `Title` and either "selectable" or "GREYED
   OUT — <DisabledReason>", then a summary line with totals; if total > 0 and enabled == 0 it
   puts an on-screen hint telling the player to hover a greyed card (`:72-77`, `:119-155`).

Member values are read field-first, property-fallback via `AccessTools.Field`/`Property`
because the card info type uses public fields (`:157-167`). The auto-open half opens vanilla's
manage-troops exchange once a new clan party is created, via
`PartyScreenHelper.OpenScreenAsManageTroops(MobileParty)`.

**Patched members.** `ClanPartiesVM.GetCanCreateNewParty` (postfix; reads `bool __result` +
`TextObject disabledReason`); `ClanPartiesVM.GetNewPartyLeaderCandidates` (postfix; reads
`IEnumerable __result`). Also resolved: `ClanPartiesVM.CreateNewClanParty`,
`ClanCardSelectionItemInfo.IsDisabled`/`.DisabledReason`/`.Title`,
`PartyScreenHelper.OpenScreenAsManageTroops(MobileParty)`.

**Limitations.** Purely observational — it does not make a greyed card selectable. It is safe
**only** because `__result` is never written back (see `docs/MODDING-PITFALLS.md`: an earlier
draft replaced it with an `ArrayList` and would have crashed the popup). Members are read by
name, so a rename silently degrades every card to "selectable" — the self-test is what catches
that. Everything is inside a try/catch that logs "candidate log failed" (`:151-154`).
Constants are hard-coded: `ClientSettleMs` 3000, `PendingTimeoutMs` 15000 (`:50-51`). Only one
pending creation is tracked at a time (a single set of static fields) — a second creation
overwrites the first. If `_autoOpen` is false or `newLeader` is null it degrades to an
on-screen note telling the player to click the party on the map (`:180-184`). Any tick
exception logs, shows the fallback note and clears pending (`:261-266`). Statics reset on every
payload hot-reload generation.

**Self-test.** `clan-party-advisor.contract` — asserts `ClanPartiesVM` resolves with
`GetNewPartyLeaderCandidates` and `CreateNewClanParty`; that `ClanCardSelectionItemInfo`
exposes `IsDisabled` / `DisabledReason` / `Title` as either a field or a property; and that
`PartyScreenHelper.OpenScreenAsManageTroops(MobileParty)` resolves as the "opener" leg
(`:326-342`).

---

## Identity and bootstrap

`myHero` is the only `guardconfig.json` key read inside these six files
(`GuardConfig.String("myHero", "")` at `Payload/CoopHeroIdentityLock.cs:129`). `safeMode`
disables all six; `selfTest` runs the three registered contracts
(`hero-identity-lock.contract`, `client-bootstrap-fix.wiring`, `clanmode-solo-fix.contract`).
`tracing` gates none of them — all six are always on — but it enables
`CharacterCreationTrace`, `ControlTrace`, `RoleTrace` and `RuntimeDiagnostics`, which are the
tracers used to diagnose identity and bootstrap failures.

### Player-identity guard (co-op spawn identity swap)

**README item** 12 · **Source** `Payload/PlayerIdentityGuard.cs` · **Class**
`PlayerIdentityGuard` · **Tag** `[IDENTITY]` · **Config** none · **Scope** both (client and
host)

**Bug.** In a co-op mission with two player heroes, BT's spawn sync sometimes builds the
**other** player's hero as the local player agent — it becomes `Mission.MainAgent` /
`InitialPlayerAgent`, team general, order-controller owner and formation owner — while the
local hero spawns AI-controlled. Field report 2026-08-19 20:11: "I spawned as an AI and an AI
spawned as me" (`Payload/PlayerIdentityGuard.cs:9-15`).

**Mechanism.** Not a Harmony patch — a polling corrector run from `PayloadEntry.Tick`
(`Payload/PayloadEntry.cs:146`), throttled to once per second (`:37-42`). If
`Mission.MainAgent`'s `Character` is not `Hero.MainHero.CharacterObject` (`:69-72`) and an
active human agent with that character exists in `mission.Agents` (`:74-82`), it hands the
impostor back to AI (`controlled.Controller = AgentControllerType.AI`, `:93-96`), sets
`myAgent.Controller = AgentControllerType.Player` (`:103`), then repairs ownership:
`Team.GeneralAgent` (`:110-113`), `Team.PlayerOrderController.Owner` (`:118-122`) and every
`Formation.PlayerOwner` in `Team.FormationsIncludingSpecialAndEmpty` that pointed at the
impostor (`:127-133`). Each repair step is in its own try/catch so a partial repair still
completes; the player is told via `Log.Screen` (`:138`).

**Patched members.** None patched. Written: `Agent.Controller`
(`AgentControllerType.AI`/`.Player`), `Team.GeneralAgent`, `OrderController.Owner` (via
`Team.PlayerOrderController`), `Formation.PlayerOwner` (over
`Team.FormationsIncludingSpecialAndEmpty`). Read: `Mission.Current`, `Mission.MainAgent`,
`Mission.Agents`, `Mission.PlayerTeam`, `Mission.Scene`,
`Mission.GetMissionBehavior<DeploymentMissionController>()`.

**Limitations.** Capped at `MaxCorrectionsPerMission = 5` per mission so it can never fight
another system in a loop (`:27`, `:54-57`). Skipped entirely while a
`DeploymentMissionController` is on the mission — during deployment `Controller=None` on the
player agent is legitimate (`:58-61`). Does nothing when the local hero has no active agent in
the mission (spectating) or when the local hero already is the controlled agent (`:83-86`).
Requires `Campaign.Current != null` and `mission.Scene != null` (`:45-48`). It registers **no**
`SelfHealing` self-test, **no** `Diag.Report` component, and never calls
`SelfHealing.RecordFire` — its only trace is the `[IDENTITY]` log line. `CHANGELOG.md:381`
records it explicitly as a reactive safety net, not a root fix; the shared-save load case is
superseded by `CoopHeroIdentityLock`.

**Self-test.** None registered.

### Co-op hero identity lock (shared-save host handoff)

**README item** 20 · **Source** `Payload/CoopHeroIdentityLock.cs` · **Class**
`CoopHeroIdentityLock` · **Tag** `[IDENTITY]` · **Config** `myHero` · **Scope** host or solo
only (never client)

**Bug.** A Bannerlord save stores exactly one player identity — whoever was `MainHero` when it
was saved. When a co-op couple passes one shared save back and forth, the person **loading**
it to host becomes the previous host's hero (field report 2026-08-30: "when Noah saves as host
and I load our co-op, it loads me as his hero", `CHANGELOG.md:136-138`). BT's identity registry
(slots, steam/password claims) is only consulted on the client join flow; nothing fixes the
loader's identity — verified by assembly scan, `SharedSaveMode` is a bare session flag
(`Payload/CoopHeroIdentityLock.cs:12-21`).

**Mechanism.** A per-machine hero-identity map, `hero-identity.json`, written next to
`guardconfig.json` (`:42-45`), keyed by `Campaign.UniqueGameId` → `Hero.StringId`. Armed per
campaign by `PayloadEntry.OnGameStart` → `OnGameStart()` (`:61-66`,
`Payload/PayloadEntry.cs:132`), then claimed from `PayloadEntry.Tick` → `Tick()` (`:68-97`,
`Payload/PayloadEntry.cs:153`) once `Campaign.Current` and `Hero.MainHero` exist **and**
`Mission.Current` is null — never swap identity inside a mission (`:78-81`). `Claim()`
(`:99-174`): if a hero is recorded and alive but is not `MainHero`, the player is switched with
vanilla's `ChangePlayerCharacterAction.Apply(target)` — the same mechanism death-succession
uses (`:167`) — the map is re-saved, `SelfHealing.RecordFire("hero-identity-lock")` fires, and
both a log line and `Log.Screen` name who the save was last played as (`:171-173`). Learning
paths: a brand-new campaign records `MainHero` automatically (`:140-147`); an existing campaign
is claimed once from `guardconfig` `myHero` by name (`:129-139`); `MaintainRecord()`
(`:179-210`) follows death-succession to the heir.

**Patched members.** None patched. Called: `ChangePlayerCharacterAction.Apply(Hero)` (`:167`).
Read: `Campaign.Current.UniqueGameId` (`:101`), `Hero.MainHero`, `Hero.StringId`,
`Hero.IsAlive`, `Hero.Name`, `Hero.Clan`, `Hero.FindFirst(Func<Hero,bool>)` (`:229`),
`Hero.AllAliveHeroes` (`:235`),
`Campaign.Current.Models.CampaignTimeModel.CampaignStartTime`, `CampaignTime.Now.ToDays`
(`:218-219`), `Mission.Current` (gate only, `:78`).

Returns early when `PeerDetection.IsClient() == true`, both in the claim path (`:84-88`) and in
`MaintainRecord` (`:188-191`), because BT assigns the client's hero through its own claim flow.

**Limitations.** Inactive when `Campaign.UniqueGameId` is null or empty (`:102-106`). An
existing shared campaign needs a one-time explicit claim — a wrong guess would replicate the
very bug it fixes, so it refuses to infer (`:26-33`); with no record, no `myHero` and not a new
campaign it only logs guidance once per session (`:148-156`). "Brand new campaign" is
heuristic: campaign time younger than one day (`:212-225`), returning false on any exception.
`myHero` matching is by hero **name**, case-insensitive, preferring a hero in
`Hero.MainHero.Clan` then any match (`:232-249`) — ambiguous names can mis-target, and an
unmatched name only logs (`:136`). If the recorded hero is gone or dead it does **not** switch:
it keeps the save's player and re-records (`:117-125`). A living recorded hero that differs
from `MainHero` is never clobbered — treated as a foreign or cheat switch (`:176-178`).
`MaintainRecord` only runs after a successful claim this campaign and at most every 60 s
(`:182-187`). Storage is flat regex-parsed JSON with no escaping, so a hero id or campaign id
containing a quote would break the file (`:266-290`). Persist failures are logged and swallowed
(`:304-314`).

**Self-test.** `hero-identity-lock.contract` (`:316-327`) pins (a) a
`ParseMap(FormatMap(...))` round-trip over a two-entry probe map and (b) that
`AccessTools.Method(typeof(ChangePlayerCharacterAction), "Apply")` still resolves — i.e. the
vanilla succession action has not been renamed.

### Client hero-creation guard (half-synced home settlement)

**README item** 5 · **Source** `Payload/ClientHeroCreationGuard.cs` · **Class**
`ClientHeroCreationGuard` · **Tag** `[HEROCREATE-GUARD]` · **Config** none · **Scope** client
condition; the patch is installed unconditionally for every session
(`Payload/PayloadEntry.cs:52`)

**Bug.** Crash 2026-08-19 during client character creation, at the moment of picking a culture
and advancing: NRE in `DefaultSettlementValueModel.FindFarthestDistanceBetweenSettlementsInClan`,
reached via `FindMostSuitableHomeSettlement` ← `Clan.ResetPlayerHomeAndFactionMidSettlement` ←
`CharacterCreationContent.ApplyCulture`. The method dereferences
`clan.MapFaction.FactionMidSettlement` (passing it to `MapDistanceModel.GetDistance`), which is
null on a client whose faction/settlement graph has not finished replicating. Native has no
guard because in single-player the graph is always complete there
(`Payload/ClientHeroCreationGuard.cs:12-19`).

**Mechanism.** Harmony **finalizer** on the public
`DefaultSettlementValueModel.FindMostSuitableHomeSettlement(Clan)`, installed via
`harmony.Patch(method, null, null, null, new HarmonyMethod(...HomeSettlementFinalizer))`
(`:32-38`). On any escaping exception it fires
`SelfHealing.RecordFire("hero-creation-guard")`, substitutes a safe result of the **same shape
the method itself returns in its own edge cases** — `clan.InitialHomeSettlement`, else
`Settlement.All[0]` — assigns it to `ref __result`, logs the suppression with the fallback name
and the original exception message, shows a `Log.Screen` note, and returns null so the
exception is swallowed and culture application completes (`:47-76`).

**Patched members.** `DefaultSettlementValueModel.FindMostSuitableHomeSettlement(Clan)`
(finalizer). Read: `Clan.InitialHomeSettlement` (`:59`), `Settlement.All` / `.Count` / `[0]`
(`:61-63`).

**Limitations.** Suppresses the symptom rather than fixing the null
`clan.MapFaction.FactionMidSettlement` — the returned home settlement can be an arbitrary first
settlement. If the recovery path itself throws, `__result` is set to null (`:70-74`), which can
still NRE downstream. The guard is silently inactive if the method is not found — it logs
"guard inactive" and returns (`:34-37`). It registers no self-test and no `Diag.Report`
component; its only health signal is the fire count.

**Self-test.** None registered (fire tracking only, `:55`).

### Client bootstrap fix (BT action-cache false negative)

**README item** 9 · **Source** `Payload/ClientBootstrapFix.cs` · **Class**
`ClientBootstrapFix` · **Tag** `[CLIENT-FIX]` · **Config** none · **Scope** client (host and
solo sessions never run the audit — `UPSTREAM_BUG_REPORT.md:24`); the prefix is installed for
any session in which the BT assembly is present

**Bug.** Every BT client session permanently half-loads. Before applying its deferred Harmony
patches, BT's `CoopSubModule.TryVerifyNativeActionCacheWhenCampaignMapReady` audits the
engine's `ActionIndexCache`, but it compares the engine's **static** `ActionIndexCache` mirror
fields — which sit at `Index -1`, unprimed, in a client session — against fresh native lookups.
The mismatch makes it log "BootstrapAborted reason=action-cache-mismatch … restartRequired" and
set `_harmonyPatchBootstrapAttempted = true`, which permanently blocks retry, so the whole
session runs with sync patches unapplied. Player symptoms: invisible or missing partner armies,
joins never registering on the host, speed desync, no client hero selection, a client seeing a
host-style map shell (`Payload/ClientBootstrapFix.cs:8-21`; `UPSTREAM_BUG_REPORT.md:3-27`).
BT's own log proves the native catalog is fully loaded (actions=5167, every action code valid,
`diskLoad=False`) — only the static mirror is stale, so it is a false negative.

**Mechanism.** Harmony **prefix** on `TryVerifyNativeActionCacheWhenCampaignMapReady`
(`:74-82`). `VerifyPrefix` (`:147-191`): (1) if `NativeCatalogReady()` is false, return true and
let BT's own wait logic run unchanged — the safety intent is preserved; (2) if
`MirrorsAlreadyPrimed()` (the self-disable probe), log a stand-down line once and return true;
(3) otherwise `PrimeActionIndexCacheMirrors()` re-creates every unprimed static
`ActionIndexCache` mirror from the live catalog via `ActionIndexCache.Create(field.Name)` and
writes it back by reflection (`:288-327`), fires `SelfHealing.RecordFire`, sets BT's static
`_nativeActionCacheVerified = true` by reflection (`:81`, `:174-176`), sets `__result = true`
and returns false to **skip** the original — verification forced to succeed so BT's deferred
patches apply. Any exception in the prefix returns true (pass through). All engine access is
by-name reflection so it is independent of which assembly defines `ActionIndexCache` /
`MBAnimation` (`:32-33`, `:109-124`).

**Patched members.** BT `CoopSubModule.TryVerifyNativeActionCacheWhenCampaignMapReady` (prefix,
`bool __result`). Written by reflection: BT `CoopSubModule._nativeActionCacheVerified`; all
static fields of type `ActionIndexCache` except `act_none`. Read/invoked:
`ActionIndexCache.Create(string)`, `ActionIndexCache.Index`,
`MBAnimation.GetNumActionCodes()`, `MBAnimation.GetNumAnimations()`,
`MBAnimation.GetActionCodeWithName(string)`, `MBAnimation.IsAnyAnimationLoadingFromDisk()` —
the last four as the readiness gate.

**Limitations.** Only takes effect on a fresh process where the prefix beats BT's first (and
only) verify; on a mid-game payload hot-reload it just installs the prefix, since BT will not
verify again (`Payload/PayloadEntry.cs:72-74`). Retried from `OnBeforeInitialModuleScreen` in
case the co-op assembly loaded late (`Payload/PayloadEntry.cs:117`); latched by `_applied`
(`:36-38`, `:83`). If BT is absent it reports `Diag ok` with "no BT present" (`:61-66`) — not a
failure. If the engine action-cache types cannot be resolved, or BT's verify method is not
found, it refuses to activate and reports `Diag critical:true` (`:68-80`). Mirror priming is
best-effort per field: readonly or inaccessible fields are skipped silently and the
force-verify still carries the fix (`:315-319`). `act_none` is deliberately excluded from both
the probe and the prime (`:227`, `:296-299`). It does not fix BT's cache persistence — the
mismatch recurs every launch (`UPSTREAM_BUG_REPORT.md:16-22`).

**Self-test.** `client-bootstrap-fix.wiring` (`:193-208`) pins that **every** reflection target
resolved — `_createMethod`, `_indexProp`, `_getActionCodeWithName`, `_getNumActionCodes`,
`_getNumAnimations`, `_isAnyAnimationLoadingFromDisk` and BT's `_verifiedField` — and that the
`MirrorsAlreadyPrimed()` self-disable probe is callable without throwing. This proves the
wiring is intact independently of the live game reaching the bootstrap path.

### Bootstrap watch (silent `BootstrapAborted` detector)

**README item** 9 · **Source** `Payload/BootstrapWatch.cs` · **Class** `BootstrapWatch` ·
**Tag** `[BOOTSTRAP-WATCH]` · **Config** none · **Scope** both — it scans host, client and solo
BT logs; in practice the abort is a client-session condition

**Bug.** BT's own sync log can record "BootstrapAborted … restartRequired=True", meaning its
deferred patches were never applied and the whole session runs with broken sync — observed
2026-08-19 20:46 on a client with a stale `RuntimeDataCache` `.rdc` from a different version
failing the action-cache audit; symptoms were missing partner armies, joins not registering on
the host, and speed desync. BT does not surface this to the player at all, so the session
silently plays on broken (`Payload/BootstrapWatch.cs:7-15`).

**Mechanism.** Filesystem watcher, no Harmony patch. Two entry points: `CheckAtStartup()` from
`PayloadEntry.Apply` (`:24-27`, `Payload/PayloadEntry.cs:96`) scanning logs written within the
last 24 h, and `Tick()` from `PayloadEntry.Tick` (`:29-48`, `Payload/PayloadEntry.cs:150`) every
120 s scanning logs written within the last 30 minutes, latched by `_warned`. `Scan()`
(`:50-95`) walks the Desktop for `bt-sync-client.txt` / `bt-sync-host.txt` /
`bt-sync-solo.txt`, finds the **last** `BootstrapAborted` occurrence (`FullFind` whole-file at
startup, `TailFind` 256 KB tail mid-session), compares its offset against a persisted per-log
handled-offset ledger so a given abort is acted on only once, then calls `ClearStaleCache()`,
which **renames** (never deletes) every `Modules/BannerlordTogether/RuntimeDataCache/*.rdc` to
`<name>.stale-yyyyMMddHHmmss` so BT's bootstrap rebuilds them fresh. The startup pass clears
silently before this session's bootstrap; the mid-session pass logs and shows `Log.Screen`
"co-op mod did NOT fully load — cache auto-cleared, RESTART THE GAME".

**Patched members.** None. Reads Desktop `bt-sync-client.txt` / `bt-sync-host.txt` /
`bt-sync-solo.txt`; renames `<Modules>/BannerlordTogether/RuntimeDataCache/*.rdc`; writes
`<module root>/bootstrapwatch.state` (`logName|offset` lines).

**Limitations.** Offsets from `FullFind` are approximate — the line-consumption counter assumes
2-byte line endings (`consumed += line.Length + 2`, `:209`) — so the ledger compare is
heuristic, not exact. Mid-session `TailFind` only reads the last 262144 bytes (`:228`).
`_warned` latches after a non-startup warning so there is only one on-screen warning per
session (`:31-34`, `:81`; note `_warned = !startup`, so the startup pass deliberately does not
latch). Only fires for logs modified inside the age window. It cannot repair the current
session — the remedy is a restart. Renaming can fail per file if locked; failures are logged and
the cleared count under-reports (`:118-122`). Module paths are derived from `Assembly.Location`
by walking up three levels for the `Modules` dir and two for the module root (`:105-107`,
`:136-137`), so a non-standard install layout defeats it. All errors are swallowed silently at
the `Scan`/`Tick` level (`:44-47`, `:92-94`). Registers no self-test and no `Diag` component.
Upstream evidence shows the abort reproduces both with and without the `.rdc` present, so
clearing the cache is not a guaranteed cure (`UPSTREAM_BUG_REPORT.md:16-22`) —
`ClientBootstrapFix` is the real fix.

**Self-test.** None registered.

### Clan-mode solo fix (`Unknown` → `Separate`)

**README item** 10 · **Source** `Payload/ClanModeSoloFix.cs` · **Class** `ClanModeSoloFix`
(with `ClanModeSoloDecider`) · **Tag** `[CLANMODE-FIX]` · **Config** none directly · **Scope**
solo host only at runtime; the patch is installed whenever BT is present and is inert the
moment a peer connects or peer state is uncertain

**Bug.** "[BT] Marriage is blocked until clan mode is synchronized" when playing alone.
Decompile-proven: BT's `ClanModeSyncBehavior.CurrentMode` returns `Unknown` (internal enum
`af.bI = 0`) whenever no **remote** identity snapshot has arrived — and hosting with no peer
connected, one never will — so clan mode stays `Unknown` forever and every clan-mode-gated
action, marriage foremost, is blocked for the whole solo session
(`Payload/ClanModeSoloFix.cs:10-16`).

**Mechanism.** A Harmony **transpiler** on the `ClanModeSyncBehavior.CurrentMode` property
getter (`:43-51`) that injects a preamble ahead of BT's own body: call
`ClanModeSoloDecider.ShouldForceSeparate()`; `Brfalse` to the original first instruction; else
`Ldc_I4_1`; `Ret` — i.e. return `Separate` (`af.bi = 1`), the correct clan mode for a single
player (`:64-82`). The `continueOriginal` label is attached to the first original instruction,
so BT's computation runs untouched whenever the decider says no. `ShouldForceSeparate`
(`:129-159`) caches its verdict for 2 s (the getter can be called every frame), reads
`PeerDetection.ReadCoopStaticBool("IsHost") == true` and the tri-state
`PeerDetection.AnyRemotePeerConnected()`, and delegates to
`Decide(anyRemotePeer, isHost) => isHost && anyRemotePeer == false` (`:161-165`) — forcing only
on a confident "hosting and provably alone"; `true`/`null` hands off, and any exception returns
false, leaving BT untouched. It logs only on verdict change. `ReadLiveMode()` (`:84-103`) reads
the live post-patch value through a reflection `Invoke` on the getter, so it goes through the
detour rather than an inlined copy, and is consumed by `Payload/MarriageBarterGuard.cs:83`.

**Patched members.** BT `ClanModeSyncBehavior.get_CurrentMode` (transpiler; resolved via
`AccessTools.TypeByName` + `AccessTools.PropertyGetter`). Read: BT
`ClanModeSyncBehavior.Instance` (static property, in `ReadLiveMode`). Depends on BT's internal
enum `af`: `af.bI = 0` = `Unknown`, `af.bi = 1` = `Separate`.

**Limitations.** Must apply at **module load**, before any campaign code JITs — callers JITted
before the patch keep the inlined original (`:26-28`), so a mid-session hot-reload cannot
un-inline existing callers. If BT is absent or the type/getter was renamed it reports `Diag`
"ClanModeSyncBehavior.CurrentMode not found" and is retried from
`PayloadEntry.OnBeforeInitialModuleScreen` (`Payload/PayloadEntry.cs:118`), latched by
`_applied`. It depends on the raw enum value 1 meaning `Separate` — a BT enum reordering would
silently return the wrong mode. The 2 s verdict cache means up to 2 s of stale verdict right
after a peer connects or disconnects. Peer confidence is only as good as `PeerDetection`: a null
`Server` with unreadable role flags returns null (unknown) and therefore never forces
(`Payload/BattleMode.cs:529-538`). `CHANGELOG.md` describes the same fix as inert once a peer
joins.

**Self-test.** `clanmode-solo-fix.contract` (`:105-119`) re-resolves
`BannerlordTogether.ClanModeSyncBehavior` and its `CurrentMode` getter, asserts `_applied`, and
pins the decision contract with three cases: `Decide(null, isHost:true)` must be false (unknown
peer state never forces), `Decide(false, isHost:false)` must be false (a client never forces),
`Decide(false, isHost:true)` must be true (a confident host-alone forces).

---

## Siege, gates and command

`siegeCommandAll` gates the siege command guard (`Payload/SiegeCommandGuard.cs:87`);
`coopOwnArmyCommand` gates the co-op formation-block split
(`Payload/CoopCommandSplit.cs:72`). Setting either to false disables that component and it
reports healthy-but-disabled. `tracing` gates only the 30-second-throttled
`[GATE] gate is DESTROYED` explanatory line (`Payload/SiegeGatePromptFix.cs:74`); every other
line in these files logs unconditionally.

### Siege command guard (umbrella)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` (Diag component `siege-command-guard`) · **Tag** `[SIEGE-CMD]` · **Config**
`siegeCommandAll` (default true) · **Scope** solo and BT host; a BT client stands down entirely

**Bug.** Defending your own castle in a siege, your placed formations run off to guard walls,
gate and keep instead of holding where you set them down; when the castle is compromised they
abandon the spot and get killed. Field report 2026-09-03 (solo host, defending own castle)
(`Payload/SiegeCommandGuard.cs:16-18`). Root cause read from the installed build's IL: in a
siege battle vanilla's default formation orders **end with AI control on** —
`BattleDeploymentHandler.SetDefaultFormationOrders` calls
`SetOrder(IsSiegeBattle || IsSallyOutBattle ? AIControlOn : AIControlOff)` — run by the player
side's auto-deploy and by the Auto-deploy button. An AI-controlled formation then belongs to
`TacticDefendCastle`, which assigns lanes and key positions (walls, gate, keep), re-plans on a
breach ("retreat to keep", "defend key position") and re-balances troops via
`Formation.TransferUnits` / `Formation.Split`.

**Mechanism.** Seven Harmony patches applied in `Apply()` (`:110-127`): after deployment, in a
siege **defense** where the player is general, every regular formation is taken back from the
AI at its deployed spot, and the castle-defence tactic is denied both AI hand-offs and troop
re-shuffles. Deployment itself is untouched — vanilla auto-deploy still positions formations
first (`:51`). Reports through `Diag.Report(Component, …)` (`:107`, `:132`, `:138`) and
registers `SelfHealing.RegisterTest(SelfTest)` (`:133`). Idempotent via `_applied` (`:81-84`).
The individual patches are documented below.

**Patched members.** `Formation.SetControlledByAI(bool,bool)` (prefix, resolved `:93`, patched
`:110`); `Formation.TransferUnits(Formation,int)` (prefix, `:94`, `:111`);
`Team.SetPlayerRole(bool,bool)` (prefix, `:95`, `:112`); `Team.DelegateCommandToAI()` (prefix +
finalizer, `:96`, `:113-114`); `OrderController.SetOrder(OrderType)` (prefix + finalizer,
`:97`, `:115-116`); `Mission.OnDeploymentFinished()` (postfix, `:98`, `:117`);
`AssignPlayerRoleInTeamMissionController.AfterStart` (prefix, optional, `:99-100`, `:121`); BT
`SpNativeBattleHostMissionBehavior.ReleaseHostMainFormationsToAi` /
`ReleaseClientOwnedFormationsToAi` / `ReleaseFieldBattleSourceFormationsToAi` (prefix +
finalizer, `:173-190`).

**Limitations.** Scope is narrow by design: the mission must be `IsSiegeBattle` and **not**
`IsSallyOutBattle` (`:212`); `PlayerTeam.Side` must be `BattleSideEnum.Defender` (`:217`); only
regular formations, index < `(int)FormationClass.NumberOfRegularFormations` (`:59`, `:228`,
`:274`); only after `Mission.IsDeploymentFinished` (`:293`, `:314`); only while
`PlayerTeam.IsPlayerGeneral` (`:226`, `:409`). If the role-controller backing fields do not
resolve, owner-is-general promotion is limited to `Team.SetPlayerRole` and it says so
(`:118-126`). If any core vanilla member is unresolved the whole guard goes inactive rather than
crashing (`:104-109`). BT release hooks are best-effort: missing methods are logged individually
and skipped (`:183-186`). A BT client stands down entirely (`InScope` returns false via
`IsBtClient`, `:221`), logging once that the host's command assignment is authoritative and
advising "host the session (shared-save host handoff)" (`:399-405`). `CHANGELOG.md` records the
deliberate exceptions that keep working — F6 delegate command, vanilla's death hand-off, and
BT's player-down releases on the host — and notes this is a stopgap for command, not a fix for
the empty player side in solo battles.

**Self-test.** `siege-command-guard.contract` (`:523-553`). Pins the members
`Formation.SetControlledByAI(bool,bool)`, `Formation.TransferUnits(Formation,int)`,
`Team.SetPlayerRole(bool,bool)`, `Team.DelegateCommandToAI`,
`OrderController.SetOrder(OrderType)`, `Mission.OnDeploymentFinished`,
`MovementOrder.MovementOrderMove(WorldPosition)`,
`Formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache)`,
`(int)OrderType.AIControlOn == 36`, and that
`TaleWorlds.MountAndBlade.Missions.Handlers.BattleDeploymentHandler` and its
`SetDefaultFormationOrders` still exist (`:525-535`). Plus a 12-row `ShouldRefuseHandoff` truth
table: refuse for formation indices 3, 0 and 7; do not refuse for `requestAi=false`, non-siege,
deployment-not-finished, not-general, index 8, index 9, or any of the three depth counters at 1
(`:536-548`).

### `SetControlledByAI` prefix (AI hand-off refusal)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host

**Bug.** Something inside the mission — the castle-defence tactic, an emptied-and-refilled
formation via `Formation.RemoveUnit`, or `Team.SetPlayerRole` — silently hands a formation you
commanded back to the AI mid-battle.

**Mechanism.** Prefix on `Formation.SetControlledByAI(bool,bool)` taking
`ref bool isControlledByAI`. Returns immediately if the call is a hand-off **to** the player
(`!isControlledByAI`, `:284-287`). Otherwise it checks `InScope` + `IsGuardedFormation`, then
delegates to the pure `ShouldRefuseHandoff(...)` and, on refusal, **mutates the argument** to
false so vanilla still runs but with the opposite input (`:298`). Increments
`_blockedHandoffs`, records a fire, and emits a coalesced log line (`:299-301`). The whole body
is in a try/catch that fails open (`:302-306`).

**Patched members.** `Formation.SetControlledByAI(bool,bool)`.

**Limitations.** Deliberately passes through three legitimate hand-offs, detected by
`[ThreadStatic]` depth counters: the player's own F6 delegate
(`OrderController.SetOrder(OrderType.AIControlOn)`), vanilla's death hand-off
(`Team.DelegateCommandToAI`), and a BT host player-down release (`:44-47`, `:275`). Only
regular formation indices `0..NumberOfRegularFormations-1`.

**Self-test.** The `ShouldRefuseHandoff` decision table (`:536-548`).

### `TransferUnits` prefix (tactic troop-shuffle block)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host

**Bug.** `TacticDefendCastle` re-balances troops between formations
(`Formation.TransferUnits` / `Formation.Split`), so the squad you placed is drained or bloated
behind your back.

**Mechanism.** Prefix on `Formation.TransferUnits(Formation target, int unitCount)` returning
bool. Active only after `Mission.IsDeploymentFinished` (`:314`). Computes
`sourceGuarded` / `targetGuarded` = `IsGuardedFormation && !IsAIControlled` (`:318-319`) and
returns false — fully suppressing the transfer — when either side is a formation the player
commands (`:324-328`). Counts `_blockedTransfers` and logs which direction was stopped.

**Patched members.** `Formation.TransferUnits(Formation,int)`.

**Limitations.** It targets the **tactic-only** API on purpose: the order UI goes through
`OrderController.TransferUnits`, which is untouched, so the player can still re-organize
(`:48-49`). Errors fail open by returning true (`:331-334`).

**Self-test.** Method existence pinned at `:527`.

### `SetPlayerRole` prefix (owner-is-general promotion)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host (`IsBtClient` short-circuits, `:349`)

**Bug.** `Team.SetPlayerRole` hands **every** formation to the AI when the player is not the
general, and `MapEvent.IsPlayerSergeant` demotes the player to sergeant whenever they sit inside
an army led by someone else — even inside their own castle (`:34-36`).

**Mechanism.** Prefix on `Team.SetPlayerRole(ref bool isPlayerGeneral, ref bool isPlayerSergeant)`.
No-op when vanilla already wants `general && !sergeant` (`:341-344`). Skips while
`_delegateDepth` or `_btReleaseDepth` is non-zero — those paths are allowed to demote
(`:345-348`). Otherwise it requires `Team.Side == Defender`, not a BT client, and
`PlayerDefendsOwnSettlementInSiege()`; then rewrites the ref args to `general=true` /
`sergeant=false` and logs what vanilla had wanted (`:349-357`).

**Patched members.** `Team.SetPlayerRole(bool,bool)`.

**Limitations.** Uses campaign-side truth (`MobileParty.MainParty.MapEvent.IsSiegeAssault`,
`PlayerSide == Defender`, `MapEventSettlement.OwnerClan == Clan.PlayerClan`) because it must
decide before the mission's `PlayerTeam` exists (`:242-265`). It promotes only when the defended
settlement belongs to the **player's clan** — defending someone else's castle keeps vanilla
roles.

**Self-test.** `Team.SetPlayerRole(bool,bool)` pinned at `:528`.

### Role-controller `AfterStart` prefix (second role source)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host (`IsBtClient` short-circuits, `:369`)

**Bug.** `AssignPlayerRoleInTeamMissionController` independently decides `IsPlayerGeneral` /
`IsPlayerSergeant`, so fixing `Team.SetPlayerRole` alone can be overridden.

**Mechanism.** Prefix on `AssignPlayerRoleInTeamMissionController.AfterStart` that writes the
compiler-generated auto-property backing fields `<IsPlayerGeneral>k__BackingField = true` and
`<IsPlayerSergeant>k__BackingField = false` via cached `FieldInfo`, when the player defends
their own settlement (`:365-387`). Fields are resolved at `Apply` (`:101-102`).

**Patched members.** `AssignPlayerRoleInTeamMissionController.AfterStart`;
`<IsPlayerGeneral>k__BackingField`; `<IsPlayerSergeant>k__BackingField`.

**Limitations.** An **optional** patch — if the type or either backing field does not resolve,
the patch is skipped and the log says "role controller members not resolved — owner-is-general
promotion limited to Team.SetPlayerRole" (`:123-126`), with `Diag` detail carrying "role
controller unresolved" (`:132`). Backing-field names are compiler-generated and would break if
the property stops being an auto-property.

**Self-test.** Not pinned — only the vanilla core members are.

### `OnDeploymentFinished` postfix (take-over at the deployed spot)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host; a BT client only gets the informational note

**Bug.** Vanilla's siege default for every formation is AI control on, so the moment deployment
ends the castle-defence AI owns your troops.

**Mechanism.** Postfix on `Mission.OnDeploymentFinished`. Iterates
`Team.FormationsIncludingEmpty`, skips indices ≥ the regular-formation count, counts
already-player formations as `held`, and for each AI-controlled one: captures its **current**
position with
`Formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache.GroundVec3)`,
calls `Formation.SetControlledByAI(false,false)`, and, if the `WorldPosition` `IsValid`, issues
`Formation.SetMovementOrder(MovementOrder.MovementOrderMove(spot))` so the formation holds
exactly where auto-deploy put it (`:414-434`). Logs the taken/held split and shows a one-time
`Log.Screen` note (`:439-445`).

**Patched members.** `Mission.OnDeploymentFinished()`;
`Formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache)`;
`Formation.SetControlledByAI(bool,bool)`; `Formation.SetMovementOrder(MovementOrder)`;
`MovementOrder.MovementOrderMove(WorldPosition)`; `Team.FormationsIncludingEmpty`.

**Limitations.** Bails with an explicit log when the player is not the team's general ("another
lord's army — vanilla command applies", `:409-412`). On a BT client it logs the co-op note once
(`_clientNoteLogged`) and returns (`:399-406`). The screen note is shown once per mission
(`_screenNoteShown`).

**Self-test.** `Mission.OnDeploymentFinished`, `MovementOrder.MovementOrderMove(WorldPosition)`
and `Formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache)` pinned at
`:531-533`.

### `SetOrder` prefix/finalizer (F6 explicit-delegate depth counter)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host

**Bug.** A blanket refusal of AI hand-offs would also break the player's own F6 "delegate
command to AI" order.

**Mechanism.** Prefix on `OrderController.SetOrder(OrderType)` increments the `[ThreadStatic]`
`_explicitAiDepth` when `orderType == OrderType.AIControlOn` (`:453-459`); a **finalizer** — not
a postfix, so it still runs if the order throws — decrements it, guarded against going negative,
and returns `__exception` unchanged (`:461-468`). `SetControlledByAIPrefix` passes the hand-off
through while the depth is > 0.

**Patched members.** `OrderController.SetOrder(OrderType)`; `OrderType.AIControlOn`.

**Limitations.** `[ThreadStatic]` — it only correlates calls on the same thread. Reset to 0 in
`OnMissionInit` (`:163`).

**Self-test.** `(int)OrderType.AIControlOn == 36` pinned at `:534`; depth semantics covered by
the row `!ShouldRefuseHandoff(true,true,true,true,3,1,0,0)` at `:546`.

### `DelegateCommandToAI` prefix/finalizer (death hand-off depth counter)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host

**Bug.** When the player falls, vanilla legitimately hands command to the AI via
`Team.DelegateCommandToAI` — the guard must not fight that.

**Mechanism.** The prefix increments the `[ThreadStatic]` `_delegateDepth`, the finalizer
decrements it (`:470-482`). While it is > 0, both `SetControlledByAIPrefix` and
`SetPlayerRolePrefix` stand down (`:275`, `:345-348`).

**Patched members.** `Team.DelegateCommandToAI()`.

**Limitations.** `[ThreadStatic]`; reset in `OnMissionInit` (`:164`).

**Self-test.** `Team.DelegateCommandToAI` pinned at `:529`; row
`!ShouldRefuseHandoff(true,true,true,true,3,0,1,0)` at `:547`.

### BT player-down release hooks (`PatchBtReleases` / `RetryBt`)

**README item** 23 · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** BT host
only; a harmless no-op in solo

**Bug.** On a BT host, when a player goes down BannerlordTogether deliberately releases
formations to the AI; the guard would otherwise block BT's own recovery path.

**Mechanism.** `AccessTools.TypeByName("BannerlordTogether.SpNativeBattle.SpNativeBattleHostMissionBehavior")`,
then patch each of `ReleaseHostMainFormationsToAi`, `ReleaseClientOwnedFormationsToAi`,
`ReleaseFieldBattleSourceFormationsToAi` with a prefix that increments the `[ThreadStatic]`
`_btReleaseDepth` and a finalizer that decrements it (`:168-203`, `:484-496`). Returns a
**count** of successfully hooked methods, reported in the activation line (`:129-131`).
`RetryBt(harmony)` re-runs the scan once if BT's assembly loaded after the payload
(`_btRetried`, only when `_applied && _btPatched == 0`) (`:142-155`); called from
`Payload/PayloadEntry.cs:121`.

**Patched members.** BT `SpNativeBattleHostMissionBehavior.ReleaseHostMainFormationsToAi`,
`.ReleaseClientOwnedFormationsToAi`, `.ReleaseFieldBattleSourceFormationsToAi`.

**Limitations.** If the BT type is absent (no BT installed) it silently returns 0 (`:174-177`).
A renamed BT method logs `[SIEGE-CMD] BT release method not found (BT update?): <name>` and the
others are still hooked (`:183-186`). Only one retry, ever.

**Self-test.** Not pinned — BT members are third-party; the `_btPatched` count is logged
instead.

### Coalescing block tracer (`LogBlocked`)

**README item** n/a · **Source** `Payload/SiegeCommandGuard.cs` · **Class**
`SiegeCommandGuard` · **Tag** `[SIEGE-CMD]` · **Config** `siegeCommandAll` · **Scope** solo and
BT host

**Bug.** Hand-off refusals and transfer blocks can fire many times a second and would flood
`CrashGuard.log`.

**Mechanism.** An `Environment.TickCount`-based five-second coalescer with wraparound safety
(the `now >= _lastBlockLogTick` clause), emitting the latest reason plus the running per-battle
totals "`<N> hand-off(s) refused, <M> troop shuffle(s) stopped`" (`:512-521`). Counters reset in
`OnMissionInit` (`:161-162`).

**Limitations.** Suppressed events are counted, not individually logged.

**Self-test.** n/a.

---

## Harness

The harness is the always-loaded outer module (`Harness/`); the payload is the hot-reloadable
inner assembly. Harness code is infrastructure — its co-op scope is "both" throughout, because
solo, host and client run it identically.

### Assembly-resolve identity pin

**README item** n/a · **Source** `Harness/HotReload.cs` · **Class** `HotReload` · **Tag**
`[HOTRELOAD]` · **Config** none · **Scope** both

**Bug.** The entire mod silently does nothing: the payload assembly fails to load with
"Method 'Apply' in PayloadEntry does not have an implementation", so every guard and fix is
off while the player thinks the mod is installed. Field hits 2026-08-21 15:14 (whole payload
silently failed to load) and 2026-08-29 22:44 (gen2 rejected mid-session; tracing could not be
enabled without a restart).

**Mechanism.** `AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLoadedAssemblies`
(`Harness/HotReload.cs:63`). The handler (`:134-192`): (1) any simple name starting
`BLTDeploymentCrashGuard.Payload` returns null — never redirect a payload name, each generation
is its own per-build-stamped assembly (`:139-142`); (2) simple name `0Harmony` is **pinned** to
`typeof(HarmonyLib.Harmony).Assembly` and the harness's own simple name is pinned to
`typeof(HotReload).Assembly`, because those two assemblies' types cross the harness/payload
boundary — `IPayload.Apply` takes a `HarmonyLib.Harmony`, and `ISharedState`/`Log`/`GuardConfig`
live in the harness (`:144-165`); (3) otherwise it scans
`AppDomain.CurrentDomain.GetAssemblies()` skipping `IsDynamic`, returns the first non-dynamic
match by simple name, and logs "(AMBIGUOUS: N loaded copies share this name; took the first)"
when more than one matched (`:167-185`); (4) no match → log "no loaded match (deferring to
other resolvers)" and return null. The whole body is wrapped in an empty catch so the resolver
can never throw into the binder (`:188-190`).

**Patched members.** None (event subscription, not a Harmony patch):
`AppDomain.CurrentDomain.AssemblyResolve`, `AssemblyName(args.Name).Name`,
`Assembly.IsDynamic`, `AppDomain.CurrentDomain.GetAssemblies()`.

**Limitations.** Cannot help on the `Assembly.Load(bytes)` path at all: default-context probing
succeeds — it finds the game's own `0Harmony` in the app base — so `AssemblyResolve` never
fires (`:281-285`). When several copies of a non-pinned name are loaded it arbitrarily takes
the first and only logs the ambiguity. The log message blames `AssemblyVersion` while the
proven root cause is the assembly **name**; detection is by `Location` string comparison only.

### Generation loading, watching and reload lifecycle

**README item** n/a · **Source** `Harness/HotReload.cs` · **Class** `HotReload` · **Tag**
`[HOTRELOAD]`, `[HOTRELOAD][DIAG]` · **Config** `hotReload`, `hotReloadRoslyn`,
`payloadSourceDir` · **Scope** both (dev)

**Mechanism.** Payload generations are loaded by shadow-copy `LoadFrom` when
`!_useRoslyn && File.Exists(_prebuiltPath)` (`:294`); each generation is applied under its own
Harmony id `bltogether.crashguard.gen{N}` (`:359`), and the **new** generation is applied first
with the old one swapped and unpatched only on success (`:368-370`). `Diag.HealthSummary()` is
printed on every successful generation apply (`:380-381`). Watching and Roslyn compilation are
gated on `hotReload=true` **and** the presence of a `.hotreload-dev` marker file in the module
root (`Harness/HotReload.cs:70-72`); the shadow-copy `LoadFrom` generation loader itself runs
on the player path too, since that is the normal load-once path. See `HOTRELOAD.md` for the
workflow.

**Limitations.** Any exception in the shadow-copy path falls back to a byte-load with a logged
warning (`:327-329`). Shadow files accumulate on disk until `CleanStaleShadows` runs, which
happens once, when `_current == null` — so a long dev session accumulates one shadow per reload
attempt until the next launch. Unpatch failure of the previous generation is logged but
tolerated, so both generations' patches can coexist after a partial failure. The reloader is
not wired into `Tick()` or `OnMissionInit()`, so those two entry points never retry
(`:90-126`). `_pendingReload` is `volatile` but `_debounceTick` is a plain `int` written from
the watcher thread and read from the main thread (`:37` vs `:45`, `:483`). Only `ex.Message` is
logged for lifecycle and tick errors (no stack), unlike the full `ex` logged for start and
generation-load failures.

### Roslyn compile-from-source with prebuilt fallback

**README item** n/a · **Source** `Harness/PayloadCompiler.cs` · **Class** `PayloadCompiler` ·
**Tag** `[HOTRELOAD]` · **Config** `hotReloadRoslyn`, `payloadSourceDir` · **Scope** both (dev
only)

**Bug.** Roslyn on .NET Framework 4.8 inside Bannerlord can bind-conflict with ButterLib's
older `System.Collections.Immutable` / `System.Reflection.Metadata`; if `Emit` throws, a naive
implementation would leave the mod with no payload at all.

**Mechanism.** `PayloadCompiler` is compiled in only under the `ROSLYN` `#if` (harness built
with `-p:Roslyn=true`, which also adds the `Microsoft.CodeAnalysis.CSharp` package); the
default build omits it entirely so the mod loads without the Roslyn DLLs
(`Harness/PayloadCompiler.cs:15-24`, `:27-37`). `CompileFromSource` parses every `*.cs` under
`sourceDir` with `SearchOption.AllDirectories` into `CSharpSyntaxTree`s, builds
`MetadataReference`s from every non-dynamic loaded assembly that has a real on-disk `Location`
(deduped case-insensitively), compiles as `BLTDeploymentCrashGuard.Payload`
`DynamicallyLinkedLibrary` at `OptimizationLevel.Release`, and emits to a `MemoryStream`
(`:41-100`). On failure it logs the first 15 error diagnostics as
`[HOTRELOAD] Roslyn ERROR <id> <message> @ <location>` and
`Log.Screen("hot-reload: compile error (see CrashGuard.log) — kept previous generation")`,
returning null (`:89-98`). The caller `LoadPayloadBytes` catches any throw and falls back to
`File.ReadAllBytes` of the prebuilt DLL (`Harness/HotReload.cs:415-446`).

**Limitations.** Roslyn output is byte-loaded (`Assembly.Load(bytes)`), which is exactly the
path whose default-context probing splits the `0Harmony` identity
(`Harness/HotReload.cs:280-287`) — so the Roslyn path carries the identity-split risk the
`LoadFrom` path was built to avoid. Without `ROSLYN` defined it logs
`[HOTRELOAD] Roslyn not compiled in (build with -p:Roslyn=true for edit-.cs reload) — using
prebuilt DLL` and returns null (`:102-103`).

### Rolling-window log rotation

**README item** 26 · **Source** `Harness/Log.cs` · **Class** `Log` · **Tag** n/a (writes the
log itself) · **Config** none · **Scope** both

**Bug.** 2026-09-04 incident: a per-tick tracer filled the 8 MB cap in minutes and, with only
one backup, the flip discarded the very evidence being chased. Separately, an older
once-per-session rotation latch let `CrashGuard.log` reach 283 MB because the only check ran
while the file was still small (`Harness/Log.cs:80-87`).

**Mechanism.** `RotateIfNeeded`, called under `Sync` from every `Info`, re-checks every
`RotateCheckEveryWrites` (256) writes via `_writesSinceRotateCheck++ % 256 != 0`; when the file
exceeds `MaxLogBytes` (8 MB) it deletes the oldest segment (`.6` = `MaxSegments`), shifts
`.5`→`.6` … `.1`→`.2`, then moves the live file to `.1` — a rolling window of six segments,
about 48 MB of history (`:13-15`, `:88-120`). The whole body is in a swallowing try/catch.

**Limitations.** The size check is amortised, so the file can exceed 8 MB by up to 255 writes'
worth before rotating. This is a harness change, so it takes effect on the next game launch,
not by hot-reload. About 48 MB of history is still a finite window. It replaced the v1.1.0
single-file rotation (`CrashGuard.log` → `.1` overwrite), under which a log burst could discard
the very evidence being chased (`CHANGELOG.md:346-348`).

**Self-test.** None registered.

### Never-fatal logging and role-tagged lines

**README item** 26 · **Source** `Harness/Log.cs` · **Class** `Log` · **Tag** `[Deploy Guard]`
(screen prefix), `[H]`/`[C]`/`[S]`/`[?]` (role tag) · **Config** none · **Scope** both

**Bug.** A logging failure (locked file, missing directory) must never take the game down; and
log lines must be attributable to host, client or solo without the harness depending on
payload types.

**Mechanism.** `Log.Info` takes a static lock, rotates, and
`File.AppendAllText`s `"yyyy-MM-dd HH:mm:ss.fff [<roleTag>] <message>"`, all inside a try/catch
with the comment "logging must never take the game down" (`:62-76`). The role tag is
**inverted**: the payload, which owns peer detection, pushes it in via `Log.SetRoleTag(tag)`,
which ignores null/empty; the default is `?` (`:8-10`, `:18-19`, `:32-39`). `Log.Screen` wraps
`InformationManager.DisplayMessage(new InformationMessage("[Deploy Guard] " + message, new Color(1f, 0.75f, 0.3f)))`
in try/catch (`:122-131`). The log path is `<moduleRoot>/CrashGuard.log`, derived from the
harness assembly `Location`'s directory + `"../.."`, falling back to the relative
`BLTDeploymentCrashGuard.log` on any throw (`:41-60`).

**Self-test.** None registered.

### Health summary and critical-missing on-screen warning

**README item** 25 · **Source** `Harness/Diag.cs` · **Class** `Diag` · **Tag** `MOD HEALTH:`,
`[Deploy Guard]` (screen) · **Config** none · **Scope** both

**Bug.** A BannerlordTogether update renames a method, a by-name reflection guard silently
fails to resolve it, and the player keeps playing with a fix that is not actually installed.

**Mechanism.** Every guard calls `Diag.Report(component, ok, detail, critical: false)`, which
appends to `_healthy` or `_degraded` as `"component (detail)"` and raises `_criticalMissing`
when `critical` (`:71-85`). `HealthSummary()` emits `MOD HEALTH: N active` plus, when degraded,
`", M NOT resolved -> <list>  (likely a BannerlordTogether update renamed a method — check for
a mod update)"`, and if any critical component is missing also
`Log.Screen("WARNING: a core BLT-guard fix did not load (BT may have updated) — see
CrashGuard.log")` (`:87-104`). Printed by the reload engine on every successful generation
apply (`Harness/HotReload.cs:380-381`).

**Limitations.** `HealthSummary` has the side effect of showing a screen message, so calling it
more than once re-warns (`:96`).

### Version / build-time / session-id banner

**README item** 25 · **Source** `Harness/Diag.cs` · **Class** `Diag` · **Tag**
`===== BLT Deployment Crash Guard … =====` · **Config** none · **Scope** both

**Bug.** Log evidence from a stale or mismatched build is indistinguishable from the current
one.

**Mechanism.** `Diag.Version` reads the assembly identity back (Major.Minor.Build) rather than
hardcoding — the single source of truth is `<Version>` in `Directory.Build.props`, stamped by
MSBuild (`:15-30`). `Diag.BuildTime()` is
`File.GetLastWriteTime(assembly Location)` formatted `"yyyy-MM-dd HH:mm"` (`:45-56`).
`SessionId` is `(Environment.TickCount ^ (pid << 8)).ToString("x8")`, generated once per launch
and living in the harness so it survives payload reloads (`:8-11`, `:32-43`). `Banner()` is
`===== BLT Deployment Crash Guard v<ver> (harness build <time>) session=<id> =====`, logged
first thing in `OnSubModuleLoad` (`:58-61`, `Harness/SubModule.cs:19`).

**Limitations.** `BuildTime` here is the **harness** write time; the comment notes the
generation banner uses the payload build time instead (`:49`).

---

## Indexes

Built after all areas are documented — see the end of this file.
