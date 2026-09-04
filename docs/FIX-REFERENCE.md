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
not enumerated. (7) `Harmony.Unpatch(method, HarmonyPatchType.All, owner)` is per-owner and
coarse: it removes every patch kind that owner has on the method, even though the stash records
each kind separately (`:256-266`).

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
load-time `MovementOrderTypeInitGuard`.

**Health for a tracer is its load line, not `MOD HEALTH` or `[SELFTEST]`.** No file in this
area calls `Diag.Report` or `SelfHealing.RegisterTest` (verified by grep across all ten), so
`selfTest: true` exercises none of them and they never appear in the `MOD HEALTH:` counts.
Their health report is what they print at load: "tracer active on N method(s)", "type not
found: X", "no patchable method X" (`Payload/TracePatches.cs:46`, `Payload/ControlTrace.cs:45`,
`Payload/CoopBattleTrace.cs:46`, `Payload/RoleTrace.cs:61`,
`Payload/CharacterCreationTrace.cs:47-48`, `Payload/MovementOrderInitProbe.cs:44`,
`Payload/MovementOrderTypeInitGuard.cs:64-71`). Because every hook is resolved by name, a silent
hook miss looks exactly like "the bug did not happen" — read those counts before trusting an
absence of trace output.

| File | Tag | Gate | Patches | Note |
|---|---|---|---|---|
| `TracePatches.cs` | `[TRACE]` | `tracing` | 9 mission/menu/encounter chokepoints, by name over every overload | not behaviour-neutral |
| `ControlTrace.cs` | `[CONTROL]` | `tracing` | 11 control-handoff members + `Mission.OnDeploymentFinished` | dump is local-machine truth |
| `CoopBattleTrace.cs` | `[COOP-BATTLE]` | `tracing` | 4 BannerlordTogether internals | inert without BT |
| `RoleTrace.cs` | `[ROLE]` | `tracing` + `PayloadEntry.Tick` | `MBSaveLoad.LoadSaveGameData` | inert without BT, silently |
| `CharacterCreationTrace.cs` | `[CHARGEN]` | `tracing` | 5 `CharacterCreationState` methods + `AppDomain.FirstChanceException` | session-wide capture |
| `RuntimeDiagnostics.cs` | `[DIAG]` | `tracing` | nothing — pure reads | heartbeat + shared context/stack helpers |
| `MovementOrderInitProbe.cs` | `[MO-PROBE]` | `tracing` | `MovementOrder..ctor(enum)` prefix + finalizer | diagnostic only |
| `MovementOrderTypeInitGuard.cs` | `[MO-INIT]` | none (only `safeMode`) | `MovementOrder..ctor(enum)` transpiler + forced static init | the real crash fix; load-time |
| `TraceThrottle.cs` | `[repeat]` | n/a (library) | nothing | coalescing emitter |
| `LogStreamer.cs` | `[STREAM]` | `logStreamBin` / `logstream.txt` | nothing — driven from `Tick` | uploads the last 2 MB, publicly |

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
generation (`:26`, `:43-47`). It resolves BT types during `PayloadEntry.Apply` **only** and,
unlike the co-op *fixes*, is not retried in `OnBeforeInitialModuleScreen` (contrast
`Payload/PayloadEntry.cs:117-121`) — if BT's assembly loads after the payload it stays inert for
the session, and a payload hot-reload (fresh statics, fresh `Apply`) is the practical way to
pick BT up late. Argument meaning is positional and unvalidated — `arg0`/`arg1`
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

**Limitations.** When `CoopSession` is not found it returns **silently** — no `[ROLE]` line of
any kind, unlike `CoopBattleTrace` (`:62-64`) and `ControlTrace` (`:56`), which log "type not
found". So an absent `[ROLE] role-transition tracer active` line means either "BT not loaded" or
"BT renamed `CoopSession`", and the log cannot tell you which. Like `CoopBattleTrace` it binds
BT once, at `Apply`, and is never retried. Only `LoadSaveGameData` is bracketed — a role change
from any other path shows up only as the ≥1 s tick diff (`:82-93`). The one-shot launch line
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
thread are dropped. Inner chains are walked to depth 8 (`:205`) and each exception shows at most
14 frames (`:266`). The whole handler body is wrapped in an empty catch: "a tracer must never
take the game down" (`:187-191`). Read all of that before concluding "nothing threw" from a
quiet log. Documentation divergence: `CHANGELOG.md:20-24` (v1.3.2)
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
the origin construction has already happened before the probe is installed — the probe then sees
only later, ordinary constructions. To use it as an origin probe you must move it ahead of the
guard, or disable the guard's forced init; otherwise its value is confirming that later
constructions have a live mission.

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
ahead of `PatchAll` and every other guard (`Payload/PayloadEntry.cs:38-42`), unconditionally —
there is no `tracing` gate; only `safeMode` stops it. Two parts: a **transpiler** that collapses
`call Mission::get_Current; callvirt Mission::get_CurrentTime` inside `MovementOrder..ctor` into
one `SafeCurrentTime()` call, and `RuntimeHelpers.RunClassConstructor` (`:82`) to pin the static
init to that safe moment. It reports what it did in the load log.

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

**Reading `[repeat]` lines.** A rollup is flushed on the *next* repeat after the 5 s window, not
on a timer, so `[repeat] … ×N` is a frequency signal rather than an exact count. Keys must be
built from stable identity (exception type + frame), never from a timestamp or a value: unstable
keys both defeat coalescing and, past 512 live keys, trigger a full clear that discards every
in-flight count.

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

Each of these registers a `Diag` component id, which is the name that appears in the
`MOD HEALTH:` line and in `[SELFTEST]` results:

| Fix | Component id | Tag | Config | Patched members |
|---|---|---|---|---|
| #2 dead hero (caller) | `dead-hero-return-fix` | `[DEADHERO]` | — | `IssueManager.MakeAlternativeTroopsReturn(TroopRoster)` prefix |
| #2 dead hero (invariant) | `dead-hero-activate-invariant` | `[DEADHERO]` | — | `Hero.ChangeState(CharacterStates)` prefix |
| #3 conversation camera | `conversation-camera-guard` | `[CONVO-CAM]` | — | `MissionConversationCameraView.MakeSpeakerLookToListener` + `UpdateAgentLooksForConversation` finalizers |
| #4 clan screen | `clan-screen-guard` | `[CLAN-GUARD]` | — | `GauntletClanScreen.CreateDataSource` finalizer (+ reflective `ScreenManager.PopScreen()`) |
| #10 atomic dowry | `marriage-barter-guard` | `[MARRIAGE-GUARD]` | — | `BarterManager.ApplyAndFinalizePlayerBarter(Hero,Hero,BarterData)` prefix |
| #11 no sickness | `illness-death-guard` | `[NOSICK]` | `noSickness` | `AgingCampaignBehavior.IsItTimeOfDeath` + `DailyTickHero` prefixes |
| #21 sneak-in | `stealth-hideout-advisor` | `[STEALTH]` | — | `HideoutAmbushMissionController.AfterStart` postfix + the three stealth→battle transitions |
| #22 party creation | `clan-party-advisor` | `[CLAN-PARTY]` | `partyTroopsOnCreate` | `ClanPartiesVM.GetCanCreateNewParty` / `GetNewPartyLeaderCandidates` / `CreateNewClanParty` postfixes |

Component ids are registered at `Payload/IllnessDeathGuard.cs:44`,
`Payload/MarriageBarterGuard.cs:39`, `Payload/ConversationCameraCrashGuard.cs:26`,
`Payload/DeadHeroReactivationFix.cs:54` and `:116`, `Payload/ClanScreenCrashGuard.cs:31`,
`Payload/StealthHideoutAdvisor.cs:59`, `Payload/ClanPartyCreationAdvisor.cs:68`.

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
and the three stealth→battle transitions the command guarantee hooks —
`ChangeHideoutMissionModeToBattle`, `StartBossFightBattleModeInternal`,
`StartBossFightDuelModeInternal` (`:49-57`). The guarantee asserts `Team.GeneralAgent` and
`PlayerOrderController.Owner`.

**Limitations.** If the controller type does not resolve (an older game build without the
stealth hideout) `Apply` returns silently with no `Diag` report at all (`:37-41`). All
reflection is by name because `SandBox` is not a compile-time reference (`:27`). The postfix
body is wrapped in a bare catch that swallows everything (`:76-78`). The command guarantee
asserts ownership exactly once per transition (`:81-118` has no periodic re-check), so if BT's
battle patches take ownership back later in the ambush it is **not** re-repaired — unlike
`CoopCommandSplit`, which re-applies every half second. Missing transitions are skipped, the
patched count is reported in the `Diag` detail, and `Diag ok = patched > 0` (`:58-59`) — so a
partial resolve after a game update still reports healthy; read the "active on N method(s)"
count in the log. The whole postfix is inside a try/catch that logs and
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
and skipped (`:183-186`). Formation indices 8 (general) and 9 (bodyguard) are never touched. The
take-over only issues a MOVE order when the captured `WorldPosition` is valid (`:429-432`).
`OnMissionInit()` zeroes both counters, the one-shot flags and all three depth counters
(`:161-165`; called from `Payload/PayloadEntry.cs:138`). Every patch body fails **open** into
vanilla behaviour. A BT client stands down entirely (`InScope` returns false via
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

#### Legitimate hand-offs the guard must let through

Each is tracked by its own `[ThreadStatic]` depth counter (declared `:61-66`); the prefix
increments and the **finalizer** decrements, so an exception cannot leak the counter.

| Counter | Incremented by | Stands down |
|---|---|---|
| `_explicitAiDepth` | `OrderController.SetOrder(OrderType.AIControlOn)` — the player's own F6 | hand-off refusal |
| `_delegateDepth` | `Team.DelegateCommandToAI()` — vanilla's hand-off when the player falls | hand-off refusal **and** the owner-is-general promotion |
| `_btReleaseDepth` | BT's three `Release*FormationsToAi` host methods | hand-off refusal **and** the owner-is-general promotion |

Counters are thread-local — they only correlate calls on the same thread — and are zeroed in
`OnMissionInit` (`:163-165`).

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

### Siege gate prompt fix (gates at rest activate their points)

**README item** 19 · **Source** `Payload/SiegeGatePromptFix.cs` · **Class**
`SiegeGatePromptFix` (Diag component `siege-gate-prompt-fix`) · **Tag** `[GATE]` · **Config**
`tracing` (for one explanatory line only) · **Scope** both — "Works in vanilla and co-op
(missions are local; BT has no gate code)" (`:27`)

**Bug.** Defending a castle with the gate open, there is no "F: Close" prompt (field report
2026-08-30); a never-cycled closed gate has no prompt at all. The gate's standing points are
permanently dead (`:9-27`). Root-caused in the installed build's IL: `CastleGate.ServerTick`
activates the gate's standing points only when the door's animation parameter is exactly
≥ 1.0; anything less deactivates every point — and vanilla itself parks a closed gate at a
frozen 0.99 (`SetInitialStateOfGate`), while an opened door can settle a float-hair under 1.0.

**Mechanism.** Postfix on `CastleGate.ServerTick`. Reads the private
`CastleGate._doorSkeleton` (cached `FieldInfo`, `:42`) as `TaleWorlds.Engine.Skeleton`, gets
`Skeleton.GetAnimationParameterAtChannel(0)`, and acts **only** inside the band [0.98, 1.0) —
≥ 1f means vanilla activated correctly, < 0.98 is a genuine mid-swing door (`:87-91`). Inside
the band it applies vanilla's own direction rule:
`excludedTag = State == CastleGate.GateState.Closed ? "close" : "open"`; for every
`StandingPoint`, `deactivate = point.GameEntity.HasTag(excludedTag)`, applied through
`StandingPoint.SetIsDeactivatedSynched` only when it differs from `point.IsDeactivated`
(`:94-107`). Counts re-activations, records a fire, and logs at most every 5 s naming the
parameter, the state, and whether the restored prompt is "F Close" or "F Open" (`:108-119`).

**Patched members.** `CastleGate.ServerTick` (postfix, resolved `:43`, patched `:50`). Read:
`CastleGate._doorSkeleton` (private field), `Skeleton.GetAnimationParameterAtChannel(int)`,
`CastleGate.State` / `CastleGate.GateState.Closed`, `CastleGate.StandingPoints`,
`StandingPoint.IsDeactivated`, `GameEntity.HasTag(string)`. Written:
`StandingPoint.SetIsDeactivatedSynched(bool)`.

**Limitations.** Respects `MissionObject.IsDeactivated` — machine-level deactivation is
deliberate (`:66-69`). Leaves a **destroyed** (battering-ram-broken) gate exactly as vanilla
wants it, since broken gates cannot be closed, but with `tracing=true` it logs an explanatory
line at most every 30 s so the absent prompt is not a mystery (`:70-81`). Returns silently if
the skeleton is null (`:82-86`). `RestThreshold` is a hard-coded `0.98f` (`:32`).

**Self-test.** `siege-gate-prompt-fix.contract` (`:127-139`). Pins `_doorSkeletonField`
non-null, `CastleGate.ServerTick` and `StandingPoint.SetIsDeactivatedSynched`; and asserts the
decision band through the pure `Decide(float)`: `Decide(1.0f)==false`, `Decide(0.99f)==true`,
`Decide(0.5f)==false`, `Decide(0.981f)==true` (`:134`).

### Civilian gate close fix (settlement visits)

**README item** 19 · **Source** `Payload/CivilianGateCloseFix.cs` · **Class**
`CivilianGateCloseFix` (Diag component `civilian-gate-fix`) · **Tag** `[GATE]` · **Config**
none · **Scope** both — "settlement visits are local missions on every peer and BT has no gate
code (assembly scan)" (`:25-26`)

**Bug.** Walking around a settlement there is no F prompt to close (or re-open) the
castle/town gate — vanilla treats town gates as scenery (`:9-18`).

**Mechanism.** Postfix on `CastleGate.AfterMissionStart`, **civilian gates only**, guarded by
reading the private `CastleGate._civilianMission` bool (`:40`, `:73-76`). It undoes all three
vanilla locks: invokes the `MissionObject.IsDisabled` property **setter**
(`AccessTools.PropertySetter`, `:41`) with false on the gate machine and on every
`StandingPoint` (`:82-86`), then calls
`CastleGate.SetUsableTeam(Mission.Current.PlayerTeam)` so
`StandingPointWithTeamLimit.IsDisabledForAgent` can match (`:87`). Records a fire and logs the
standing-point count (`:88-90`). Closing then runs through vanilla's own `CloseDoor`
(animation, `SetGateNavMeshState`, colliders), so a closed civilian gate behaves exactly like a
closed siege gate (`:23-25`).

**Patched members.** `CastleGate.AfterMissionStart` (postfix, resolved `:42`, patched `:49`);
`CastleGate.OnTick` and `CastleGate.ServerTick` (finalizers, `:50-56`; see the next entry).
Read: `CastleGate._civilianMission` (private bool). Invoked by reflection:
`MissionObject.IsDisabled` setter. Called: `CastleGate.SetUsableTeam(Team)`,
`CastleGate.StandingPoints`.

**Limitations.** Returns without acting when `Mission.Current.PlayerTeam` is null — "nobody
local to use the gate — leave it as scenery" (`:77-81`). Battle and siege gates are untouched
(`:75`). The nav-mesh ability flags vanilla cleared are deliberately left as vanilla left them
for the open state, so pathing while open is unchanged (`:22-23`). It uses a
reflection-invoked property setter because `IsDisabled` has no public setter.

**Self-test.** `civilian-gate-fix.contract` (`:116-127`). Pins `_civilianField`,
`_setIsDisabled`, `CastleGate.AfterMissionStart`, `CastleGate.CloseDoor`,
`CastleGate.SetUsableTeam`; and asserts `TickFinalizer(null) == null` — the suppressor is inert
on the no-exception path.

### Gate tick finalizer (insurance on newly-ticking civilian gates)

**README item** 19 · **Source** `Payload/CivilianGateCloseFix.cs` · **Class**
`CivilianGateCloseFix` · **Tag** `[GATE]` · **Config** none · **Scope** both

**Bug.** Civilian scenes never ticked gates before this fix; a siege-only assumption inside
`CastleGate.OnTick` / `ServerTick` could now throw and crash a settlement visit (`:98-99`,
`:27-28`).

**Mechanism.** A finalizer registered on both `CastleGate.OnTick` and `CastleGate.ServerTick`
(whichever resolve, `:50-57`). Returns null on a non-null `__exception`, swallowing it so the
tick is skipped rather than crashing the visit; records a fire and logs
`[GATE] SUPPRESSED gate tick error (siege-only assumption in a civilian scene?)` at most every
5 s with wraparound-safe `TickCount` arithmetic (`:100-114`).

**Patched members.** `CastleGate.OnTick`; `CastleGate.ServerTick`.

**Limitations.** Not scoped to civilian gates — it suppresses tick exceptions on **all**
`CastleGate` ticks. The header describes it as pre-emptive insurance for a failure mode with
"none known" instances (`:27-28`).

**Self-test.** `TickFinalizer(null) == null` (`:122`).

### Co-op command split (each player commands their own army)

**README item** 24 · **Source** `Payload/CoopCommandSplit.cs` · **Class**
`CoopCommandSplit` (Diag component `coop-command-split`) · **Tag** `[COOP-CMD]` · **Config**
`coopOwnArmyCommand` (default true, `:72`) · **Scope** both machines run it; solo is inert

**Bug.** In co-op the client can command nothing: vanilla spawns **both** parties' troops into
the same class formations, so every formation is mixed; BT's host-side approval
(`IsClientFormationCommandApproved`) only approves a formation holding the client's troops
alone, so the client's `AllowedFormationMask` stays empty and BT logs
`[SPNATIVE ORDER-GUARD] blocked local …` (`:29-31`). Field request 2026-09-03: "in co-op I
should be able to command my own army while the host commands theirs" (`:16-17`).

**Mechanism.** Keep the two players' parties in **separate formation blocks** on both machines
— host party and every AI party on the side in indices 0–3 (I–IV: infantry, archers, cavalry,
horse archers), client party in 4–7 (V–VIII, same order) (`:34-38`). Enforced at three points:
a postfix on **every** `Mission.SpawnTroop` overload returning `Agent` (`:79-87`, `:188-212`),
a postfix on `Mission.OnDeploymentFinished` (`:95`, `:214-228`), and a 500 ms `Tick()` called
from `PayloadEntry` (`:49`, `:122-147`; `Payload/PayloadEntry.cs:155`). Constants:
`BlockSize = 4`, `EnforceIntervalMs = 500`, `ResolveRetryMs = 2000` (`:48-51`) because the Order of
Battle screen and reinforcements re-sort by class. Placement logic in `Place()`: resolve the
agent's owning `PartyBase` via `(agent.Origin as PartyAgentOrigin).Party`, compute the target
index from `CharacterObject.DefaultFormationClass`, and assign
`agent.Formation = agent.Team.GetFormation((FormationClass)TargetIndex(...))` (`:267-297`).
With the blocks clean, BT's own rules do the rest (`:38-41`).

**Patched members.** `Mission.SpawnTroop` — all overloads with
`ReturnType == typeof(Agent)`, enumerated by `GetMethods` with
`Public|NonPublic|Instance|DeclaredOnly` (postfix, `:79-87`); `Mission.OnDeploymentFinished()`
(postfix, `:88`, `:95`). Read/written: `Team.GetFormation(FormationClass)`,
`Team.FormationsIncludingEmpty`, `Formation.ApplyActionOnEachUnit(Action<Agent>)`,
`Formation.CountOfUnits`, `Agent.Formation` (settable), `Agent.Origin`, `Agent.IsHuman`,
`Agent.IsPlayerControlled`, `Agent.Team`, `Agent.Character`, `Agent.Main`,
`PartyAgentOrigin.Party`, `CharacterObject.DefaultFormationClass`,
`CharacterObject.HeroObject`, `Hero.MainHero`, `Hero.PartyBelongedTo`, `PartyBase.MainParty`,
`MBObjectManager.Instance.GetObject<Hero>`/`<CharacterObject>`. BT members relied on but not
patched: `IsClientFormationCommandApproved`, `AllowedFormationMask`,
`SendFormationMembershipSnapshot`, `ApplyClientFormationMembership` →
`ResolveFormationByClass`.

**Limitations.** Known trade-off: only four formations per player while a remote player is in
the battle — troop preferences beyond the basic four (`Skirmisher`, `HeavyInfantry`,
`LightCavalry`, `HeavyCavalry`) fold into them (`BasicSlot`, `:151-168`;
`CHANGELOG.md:47-48`). Player heroes are **never** moved: `agent == Agent.Main`,
`hero == Hero.MainHero`, or hero/character `StringId` equal to the BT ghost-hero id (`:273`,
`:299-323`); companions travel with their party. Inert unless **both** parties resolve — it
needs a live BT session with a remote peer, a non-empty `CoopSession` `GhostHeroStringId`, and
a resolvable ghost `Hero` whose `PartyBelongedTo.Party` is not `PartyBase.MainParty`
(`:327-379`). `ResolveParties` retries at most every 2 s (`ResolveRetryMs`, `:51`,
`:334-338`) and caches once resolved. Only agents on `Mission.PlayerTeam` are touched
(`:269`). `IsOutOfBlock` ignores indices < 0 and ≥ 8, so non-regular formations are never
re-sorted (`:177-184`).

**Self-test.** `coop-command-split.contract` (`:416-444`). Pins
`Mission.OnDeploymentFinished`, `Team.GetFormation(FormationClass)`, the `Agent.Formation`
property, the `PartyAgentOrigin.Party` property, and a count > 0 of `Mission.SpawnTroop`
overloads returning `Agent`. Asserts the full block mapping:
`TargetIndex(false, Infantry/Ranged/Cavalry/HorseArcher) == 0/1/2/3`, `Skirmisher` and
`HeavyInfantry` → 0, `LightCavalry` and `HeavyCavalry` → 2, and the client versions == 4/5/6/7;
plus `IsOutOfBlock` true for (client,0), (client,3), (host,4), (host,7) and false for
(client,4), (client,7), (host,0), (host,3), (client,8), (host,9), (host,-1) (`:430-439`).

### Co-op command briefing (`Announce`)

**README item** 24 · **Source** `Payload/CoopCommandSplit.cs` · **Class** `CoopCommandSplit` ·
**Tag** `[COOP-CMD]` · **Config** `coopOwnArmyCommand` · **Scope** both

**Bug.** Players cannot tell which formations are theirs after the split.

**Mechanism.** Once per battle (`_announced`), writes a full `Log.Info` naming each hero and
their block and explaining BT's approval rule, plus a short `Log.Screen` line: "Co-op:
`<host>` commands I–IV, `<client>` commands V–VIII (own army each)" (`:381-391`). Hero names
are read defensively through `HeroName()`, which returns `?` on any failure (`:393-403`).

**Limitations.** Fires only after the first successful placement or enforcement; names come
from `Hero.Name.ToString()`.

**Self-test.** n/a.

### Co-op split log coalescer (`LogRateLimited`)

**README item** n/a · **Source** `Payload/CoopCommandSplit.cs` · **Class** `CoopCommandSplit` ·
**Tag** `[COOP-CMD]` · **Config** `coopOwnArmyCommand` · **Scope** both

**Mechanism.** A single shared 5 s `TickCount` gate with wraparound safety, used for tick
errors, spawn placement errors, party-resolution errors, deployment enforcement errors and the
"re-sorted N troop(s) into their owner's block (`<reason>`)" line (`:405-414`; used at `:145`,
`:210`, `:226`, `:261`, `:376`).

**Limitations.** One shared throttle for all message kinds — a burst of one kind can hide
another.

**Self-test.** n/a.

---

## Sync systems

Both sync features ride BannerlordTogether's own network channel and share the same receive
hook, the same frame shape and the same network-thread-to-main-thread queue discipline. They
differ only in their 4-byte magic and their payload model. `pregnancySync` and `stashSync` gate
them; `tracing` gates only the spouse-proximity tracer.

**Wire suites.** `tests/BirthPayloadTest` and `tests/StashPayloadTest` are net472 console
executables that link the **shipping** wire sources — `StashPayloadTest` links all four files
(`tests/StashPayloadTest/StashPayloadTest.csproj:19-24`), because "a birth frame must not read
as stash" is part of the contract. Both exit **1** on any failure
(`tests/BirthPayloadTest/Program.cs:108`, `tests/StashPayloadTest/Program.cs:92`), so they can
gate a build; run both after touching any file under `Payload/PregnancySync/` or
`Payload/StashSync/`.

### Pregnancy / birth sync (host-authoritative)

**README item** 15 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs` · **Class**
`PregnancySyncGuard` (Diag component `pregnancy-sync`) · **Tag** `[PREG-SYNC]` · **Config**
`pregnancySync` · **Scope** both — the host sends, the client receives and reconstructs; fully
inert solo

**Bug.** BannerlordTogether disables the entire pregnancy system for the **client** — its
`SuppressClientPregnancyBehaviorPatch` prefixes `PregnancyCampaignBehavior.RegisterEvents` with
`return !CoopSession.IsClient` — and never replicates births: BT has no family or hero
replication among its roughly ten hand-rolled sync behaviors
(`docs/SPEC-pregnancy-coop-sync.md:5-11`). Player experience: a client's family never grows,
and a child born on the host never appears in the client's Clan → Members, so clan roster,
encyclopedia, inheritance and succession permanently disagree between the two machines.

**Mechanism.** Host authority plus identity replication. **Host:** subscribe
`CampaignEvents.OnGivenBirthEvent` (`:88`), serialize each newborn's non-derivable identity
into `BirthPayloadData`, frame it with `BirthWireFraming`, and send by reflection to
`CoopSession.Server.BroadcastRawReliableOrdered(byte[])` (`:430-457`). **Client:** a Harmony
**prefix** on BT's `ShouldAcceptIncomingPacket` (`:225-239`, `:316-346`) — if the bytes are
ours, enqueue the payload, set `ref __result = false` and return false so BT never dispatches
it; the main-thread `Tick` (`:99-127`) drains the queue and calls
`HeroCreator.DeliverOffSpring(mother, father, isFemale)` (`:372`), then `AlignToHost`
(`:399-426`) to force the host's `StringId`, body properties and name. Clan, parents, culture
and birthday are **not** sent — `DeliverOffSpring` reproduces them identically from the same
parents on both sides (`BirthPayloadData.cs:33-38`). Sending is gated on
`PeerDetection.ReadCoopStaticBool("IsHost") == true` **and** `AnyRemotePeerConnected() == true`
(`:251-258`).

**Patched members.** BT `Network.CoopNetworkBase.ShouldAcceptIncomingPacket` and
`Network.CoopServer.ShouldAcceptIncomingPacket` (prefix), with the legacy-namespace
`BannerlordTogether.CoopNetworkBase` / `.CoopServer` as fallbacks. Not patched:
`CampaignEvents.OnGivenBirthEvent` (`AddNonSerializedListener`),
`CoopSession.Server.BroadcastRawReliableOrdered(byte[])` (reflection invoke),
`HeroCreator.DeliverOffSpring(Hero, Hero, bool)`, `MBObjectManager.Instance.UnregisterObject` /
`RegisterPresumedObject`, `Hero.SetName(TextObject, TextObject)`,
`Hero.StaticBodyProperties`, `Hero.StringId`.

**Limitations.** Only the child's existence and identity are replicated; succession and
inheritance edge cases are explicit non-goals (`SPEC:38-41`). Client-initiated conception timing
is not supported — host authority only (`SPEC:40-42`). If either parent hero cannot be resolved
on the client, the child is skipped with a log line (`:363-368`). If the broadcast reflection
fails, the client silently misses that child until a resync (`:265-269`). The two-machine hop is
the one part no solo test covers — "validated the first time it fires"
(`CHANGELOG.md:182-185`); `pregnancySync` shipped off and became default-on in v1.2.5, at which
point only the wire format and loopback were proven. `AlignToHost` failures are logged as
"partial" and the child is kept anyway (`:422-425`). Replication is live-only — there is no
backfill for a peer that was absent. `BirthPayloadData.StillbornCount` is written (`:288`) and
round-trip-tested but the receiver never applies it: `ReconstructChildren` (`:348-394`) iterates
only `payload.Children`, so a stillbirth tally recorded on the host is not reproduced on the
client. A birth packet is rejected outright above **16** children.

**Self-test.** `LoopbackSelfTest` (`:488-523`), registered even when the feature is disabled
(`:49`). Pins: (a) a **real live hero** (`Hero.MainHero`) serializes into a birth payload and
survives `Frame` → `IsOurPacket` → `TryUnframe` with every `ChildIdentity` field equal
(`IdentityEquals`) and `MotherStringId` equal; (b) BT `PacketType PlayerHeroData = 13` must
**not** be recognized as ours (`!IsOurPacket(new byte[]{13,0,0,0})`, `:508`); (c) a null
`MainHero` (main menu) reports PASS with "pipeline untested this tick, not a failure" rather
than a false red (`:498-500`).

### Conception visibility postfix

**README item** 15 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs` · **Class**
`PregnancySyncGuard` · **Tag** `[PREG]` · **Config** none — deliberately installed regardless
of `pregnancySync`, because it is diagnostic rather than sync (`:50-53`) · **Scope** both/solo

**Bug.** Conception is invisible: the player could not tell whether "waiting at the castle with
my wife" had actually produced a pregnancy roll, so bug reports about pregnancy were
unanswerable from the log (operator ask 2026-08-30, `:50-52`). Verification against the
installed build's IL showed vanilla already works — `PregnancyCampaignBehavior.RefreshSpouseVisit`
fires when `CheckAreNearby` passes (same settlement, so waiting inside the castle counts, or
same party; ages 18–45; chance falls with age and existing children) — and BT's suppression is
literally `return !IsClient`, so the host's rolls run untouched. No behaviour change was needed.

**Mechanism.** Harmony **postfix** on `MakePregnantAction.Apply` (`:142-146`). Logs
`[PREG] conception: <hero> is now pregnant (clan <id>)` for every conception in the world, and
additionally calls `Log.Screen("<hero> is pregnant")` when the hero's clan is
`Hero.MainHero.Clan` (`:172-177`). The whole body is wrapped in a swallow-everything try/catch
(`:179-181`) so an observer can never break a conception.

**Patched members.** `MakePregnantAction.Apply(Hero)` (postfix).

**Limitations.** Postfix only, so it observes conceptions that already happened; it does not
make conception more likely. The on-screen note is limited to the player's own clan to avoid
spam.

**Self-test.** None of its own — covered indirectly by the pregnancy-sync `Apply`/`Diag`
report.

### Spouse-proximity tracer

**README item** 15 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs` · **Class**
`PregnancySyncGuard` · **Tag** `[PREG]` · **Config** `tracing` (`:147`) · **Scope** both/solo

**Bug.** "Did waiting next to her count as being with my wife?" was answerable only by vibes —
vanilla's daily conception roll fires only when
`PregnancyCampaignBehavior.CheckAreNearby(hero, spouse)` passes, and that decision is invisible
(`:129-137`).

**Mechanism.** Harmony **postfix** on the private
`PregnancyCampaignBehavior.CheckAreNearby`, resolved via `AccessTools.TypeByName`
(`:149-155`). Reads `bool __result` and logs
`[PREG] nearby-check <hero> & <spouse>: TOGETHER — daily conception roll happens | apart, no roll (hero@<place>, spouse@<place>)`.
`Place()` reports the `CurrentSettlement` name, else `party <StringId>`, else `nowhere`
(`:201-223`). Filtered to `Hero.MainHero.Clan` only — "the AI world would flood the log"
(`:188-191`).

**Patched members.** `PregnancyCampaignBehavior.CheckAreNearby` (private; postfix).

**Limitations.** Installed only when `tracing` is true at `Apply` time; flipping `tracing`
needs a payload hot-reload. Only player-clan heroes are logged, so AI-clan pregnancy behaviour
stays invisible.

**Self-test.** None — an installation failure is logged as
`[PREG] conception visibility not installed: <msg>` (`:158-161`).

### Per-campaign birth-listener rewiring

**README item** 15 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs` · **Class**
`PregnancySyncGuard` · **Tag** `[PREG-SYNC]` · **Config** `pregnancySync` · **Scope** the
handler self-gates on `IsHost`, but the subscription is installed on every machine

**Bug.** `CampaignEvents` resolves through `Campaign.Current`, which is null at module load and
is per-campaign. Subscribing at `Apply` time would either throw or bind to a dead campaign, so
the host would silently never broadcast a birth (`:59-62`, `:78-79`).

**Mechanism.** `OnGameStart()` (`:80-96`), called from `PayloadEntry.OnGameStart`
(`Payload/PayloadEntry.cs:131`). Idempotent: it returns immediately when disabled, when
`Campaign.Current` is null, or when `ReferenceEquals(_subscribedCampaign, Campaign.Current)` —
so a re-entry does not double-subscribe and loading a new campaign re-subscribes. It uses a
stable static `Sentinel` object as the listener owner (`:75`, `:88`), as
`AddNonSerializedListener` requires.

**Patched members.** None patched:
`CampaignEvents.OnGivenBirthEvent.AddNonSerializedListener(object owner, Action<Hero,List<Hero>,int> handler)`.

**Limitations.** Re-subscribed per campaign, not per payload generation — the comment says so
explicitly (`:61-62`). Failure is swallowed and logged, so a subscribe error degrades to "no
birth sync" rather than a crash (`:92-95`).

**Self-test.** Covered by `LoopbackSelfTest`'s wire half only; the subscription itself is
proven by the `[PREG-SYNC] host birth listener subscribed for this campaign` line (`:90`).

### Reconstruction re-entrancy guard and idempotent reconstruct

**README item** 15 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs` · **Class**
`PregnancySyncGuard` · **Tag** `[PREG-SYNC]` · **Config** `pregnancySync` · **Scope** client

**Bug.** Reconstructing a child with `HeroCreator.DeliverOffSpring` fires the game's own birth
pipeline — which would re-enter `OnGivenBirth` and re-broadcast the same birth (echo loop).
Separately, a re-sent packet or a shared base save would create a duplicate child hero.

**Mechanism.** A static `_reconstructing` flag is set around the `DeliverOffSpring` call in a
try/finally (`:369-386`), and `OnGivenBirth` returns early when it is set (`:247-250`).
Idempotence: `ReconstructChildren` skips any identity whose `StringId` already resolves via
`FindHero` (`:359-362`), so the same packet can be applied any number of times. The catch also
clears the flag defensively (`:390`).

**Patched members.** None; `HeroCreator.DeliverOffSpring` is the guarded call site.

**Limitations.** `_reconstructing` is a plain static bool, not `[ThreadStatic]` — correct only
because reconstruction is confined to the main-thread `Tick` (`:38-39`, `:99-127`); it would be
unsound if reconstruct were ever called off-thread.

**Self-test.** Not directly pinned — `LoopbackSelfTest` deliberately creates no hero ("no bogus
hero created and no network", `:492-493`).

### `AlignToHost` — cross-machine object-id re-keying

**README item** 15 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs` · **Class**
`PregnancySyncGuard` · **Tag** `[PREG-SYNC]` · **Config** `pregnancySync` · **Scope** client

**Bug.** `DeliverOffSpring` randomizes the child's `StringId`, gender, name and appearance. If
the client's child kept its own locally-generated id, every later cross-machine reference —
clan roster, encyclopedia, inheritance, any BT packet naming that hero — would resolve to a
different object, a permanent divergence.

**Mechanism.** `AlignToHost` (`:396-426`): (1) `BodyProperties.FromString(xml, out BodyProperties)`
and assign `child.StaticBodyProperties = bodyProperties.StaticProperties`; (2)
`new TextObject(firstName)` and `child.SetName(firstName, firstName)`; (3) **re-key the object
id** — `MBObjectManager.Instance.UnregisterObject(child)`, set `child.StringId` to the host id,
`MBObjectManager.Instance.RegisterPresumedObject(child)` (`:415-420`). It only re-keys when the
ids actually differ. Gender is forced at creation time by passing `identity.IsFemale` into
`DeliverOffSpring` (`:372`).

**Patched members.** None patched. Called/written:
`MBObjectManager.Instance.UnregisterObject(MBObjectBase)`,
`MBObjectManager.Instance.RegisterPresumedObject(MBObjectBase)`, `Hero.StringId` (setter),
`Hero.StaticBodyProperties` (setter), `Hero.SetName(TextObject, TextObject)`,
`BodyProperties.FromString(string, out BodyProperties)`, `BodyProperties.StaticProperties`.

**Limitations.** Partial failure is tolerated: any throw is caught and logged as
"align-to-host partial for `<id>`" and the child stays with whatever alignment succeeded
(`:422-425`) — a half-aligned hero is preferred over none. Only `StaticProperties`, not full
`BodyProperties`, is transferred.

### Birth wire model (`BirthPayloadData`)

**README item** 15 · **Source** `Payload/PregnancySync/BirthPayloadData.cs` · **Class**
`BirthPayloadData` · **Tag** none (pure model; callers log) · **Config** none · **Scope** both
— the identical file runs on host and client

**Bug.** A malformed or hostile packet parsed on the network thread could throw and take the
game down; and a wire model that referenced TaleWorlds types could not be unit-tested headless,
so format regressions would only surface in-game.

**Mechanism.** A pure class with **no** TaleWorlds dependency (`:9-12`), so
`tests/BirthPayloadTest` compiles the shipping source directly. Format (`:14-20`):
`byte FormatVersion | string MotherStringId | int32 StillbornCount | int32 childCount |
childCount × { string StringId, bool IsFemale, string FirstName, string BodyPropertiesXml,
string FatherStringId }` — `BinaryWriter`/`BinaryReader`, length-prefixed UTF-8 strings,
little-endian. `FromBytes` never throws: null on null or empty input (`:85-88`), null on a
`FormatVersion` mismatch ("a newer/older peer — drop rather than misparse", `:96-99`), null
when `childCount < 0` or `> 16` ("a birth is 1-2, allow slack, reject garbage", `:103-106`),
and a blanket catch returning null (`:122-125`). `WriteString`/`ReadString` coalesce null to
`""` so fields are never null (`:148-156`).

**Limitations.** No CRC or authentication — trust is inherited from BT's channel. The version
gate is exact equality, so there is no forward-compatibility path other than dropping. The
`childCount` cap of 16 would reject a hypothetical mod with larger litters.

**Self-test.** `tests/BirthPayloadTest/Program.cs` pins: single birth; twins plus stillborn;
unicode and special characters ("Ölaf Ærling 我", "mère_héros", XML with quotes and brackets);
empty fields → empty, not null; stillborn-only with zero children; null / empty / noise /
truncated / wrong-version → null with no throw; and deterministic re-serialization (the same
input produces identical bytes, guarding against dictionary-ordering nondeterminism creeping in
later, `:73`).

### Birth framing (`BirthWireFraming`)

**README item** 15 · **Source** `Payload/PregnancySync/BirthWireFraming.cs` · **Class**
`BirthWireFraming` · **Tag** none · **Config** none · **Scope** both

**Bug.** There is no free packet channel: BT dispatches by first byte and its `PacketType` byte
enum consumes every value 1..255. Riding the same channel naively would either collide with a
real BT packet type or be misparsed by BT.

**Mechanism.** Frame = `[0x00 marker][4-byte magic 'B','T','C','G'][BirthPayloadData bytes]`
(`:13-24`). Byte 0 is the one free value and is doubly safe: BT's `OnNetworkReceive` already
rejects zero-length packets, and the dispatch switch has no case for 0 and no default, so even
an unintercepted leading-0 packet is a guaranteed no-op inside BT (`:9-14`). The 4-byte magic
makes our packets unambiguous among any theoretical leading-0 traffic and makes misreading a
real BT packet as ours impossible, since a real BT packet never starts with 0 (`:16-18`).
`IsOurPacket` checks only 5 leading bytes, so it is cheap enough to run on every inbound packet
(`:41-42`). `TryUnframe` returns null on anything not well-formed and never throws (`:59-70`).

**Patched members.** None. Documented but not patched: BT `PacketSerializer.Dispatch`, BT
`OnNetworkReceive`.

**Limitations.** Marker plus magic is a convention, not a checksum — a corrupted BT payload that
happened to start `0x00 'B' 'T' 'C' 'G'` would be handed to the parser, which then returns
null. The header-length constant (5) is duplicated per feature but derived by behaviour in the
tests.

**Self-test.** `tests/BirthPayloadTest/Program.cs:77-103` pins: a framed round-trip equals the
original; the frame is recognized; the frame leads with byte 0; **no** packet with first byte
1..255 is misread as ours even when the rest spells our magic (`:86-97`); a leading 0 without
the magic is not ours (`:99`); and unframe of null, too-short, or framed-but-corrupt-body all
return null.

### Stash sync (co-op shared settlement stash)

**README item** 16 · **Source** `Payload/StashSync/StashSyncGuard.cs` · **Class**
`StashSyncGuard` (Diag component `stash-sync`) · **Tag** `[STASH-SYNC]` · **Config**
`stashSync` · **Scope** both — the host broadcasts and relays, the client sends its own updates

**Bug.** BannerlordTogether has **no** stash code at all (assembly scan 2026-08-30: zero
stash-named members) while it does sync the workshop warehouse
(`WorkshopWarehouseRosterInventoryDonePatch`) — so a stash deposit exists only on the machine
that made it. Player experience: same-clan co-op players do not actually share a stash, and a
client's deposits silently diverge from the authoritative host state and are lost on resync or
save-load (`:14-19`).

**Mechanism.** Full-snapshot replication modeled on BT's own warehouse sync. **Send:** a
Harmony **postfix** on `InventoryLogic.DoneLogic` — the same commit point BT patches for the
warehouse — gated on `__result == true` and on the private field
`InventoryLogic._inventoryMode` equalling `InventoryMode.Stash` (`:135-179`). It snapshots
`Settlement.CurrentSettlement.Stash` into a `StashPayloadData` and sends framed bytes: host →
`CoopSession.Server.BroadcastRawReliableOrdered`, client → `CoopSession.Client.SendRaw`, by
reflection only (`:415-455`). **Receive:** a prefix on BT's `ShouldAcceptIncomingPacket`
recognizing the `BTCS` frame, enqueue on the network thread, consume (`__result = false`,
return false) (`:238-267`). **Apply** on the main-thread `Tick` (`:270-300`): resolve the
settlement by `StringId` over `Settlement.All`, preserve wire-inexpressible stacks,
`stash.Clear()`, re-add every payload entry resolved via
`MBObjectManager.Instance.GetObject<ItemObject>`/`<ItemModifier>` with
`stash.AddToCounts(new EquipmentElement(item, modifier), count)`, then re-add the preserved
stacks (`:302-377`). The host then re-broadcasts an applied client update so every peer
converges — applying never sends, so there is no echo loop (`:371-376`).

**Patched members.** `InventoryLogic.DoneLogic` (postfix); BT
`Network.CoopNetworkBase.ShouldAcceptIncomingPacket` and
`Network.CoopServer.ShouldAcceptIncomingPacket` (prefix), with the legacy-namespace fallbacks.
Read/called: `InventoryLogic._inventoryMode` (private field),
`CoopSession.Server.BroadcastRawReliableOrdered(byte[])`, `CoopSession.Client.SendRaw(byte[])`,
`Settlement.CurrentSettlement`, `Settlement.All`, `Settlement.Stash`, `ItemRoster.Count` /
`GetElementCopyAtIndex` / `Clear` / `AddToCounts`,
`MBObjectManager.Instance.GetObject<ItemObject>` / `<ItemModifier>`,
`Campaign.InventoryManager` → `.InventoryLogic` (reflection read, open-screen check),
`Helpers.InventoryScreenHelper+InventoryMode` (`Enum.Parse`).

It is explicitly inert when neither `IsHost` nor `IsClient` — "no BT session — vanilla
singleplayer needs no sync" (`:148-153`) — and when hosting alone with no remote peer
(`:154-157`).

**Limitations.** Machine-local items (`ItemObject.IsCraftedByPlayer`, or anything whose
`StringId` does not round-trip through the local `MBObjectManager`) can never be expressed on
the wire — they are excluded from snapshots **and** preserved across applies, so each machine
keeps its own crafted stacks (`:38-44`, `:213-234`). Crafted replication would need
`WeaponDesign` serialization, recorded in `UPSTREAM_BUG_REPORT.md:165-176`. Last-closed screen
wins on a simultaneous edit (`:33-34`). An item the payload names but this machine cannot
resolve is skipped with a loud log (`:352-355`). The first sync between two already-diverged
stashes replaces one side wholesale — inherent to snapshot semantics. A send-reflection failure
means "peers will diverge until the next stash edit" (`:167-169`).

**Self-test.** `LoopbackSelfTest` (`:459-499`), registered even when disabled (`:65`). Pins:
(a) a two-entry payload survives `Frame` → `IsOurPacket` → `TryUnframe` with
`Entry.ValueEquals` field-for-field and `SettlementStringId` equal; (b) cross-feature
discrimination in **both** directions — a birth frame must not read as stash and a stash frame
must not read as birth (`:481-482`); (c) a real BT packet (first byte 13) must not match
(`:483`); (d) a payload carrying `Count = -1` must be rejected by `TryUnframe` (`:473-478`).

### `IsMachineLocal` — wire-inexpressible item classification

**README item** 16 · **Source** `Payload/StashSync/StashSyncGuard.cs` · **Class**
`StashSyncGuard` · **Tag** `[STASH-SYNC]` · **Config** `stashSync` · **Scope** both — every
machine classifies its own roster

**Bug.** Two data-loss bugs, both caught in commit review. (1) A naive snapshot-apply silently
**wiped** a player-crafted item: the peer's snapshot structurally cannot mention it, so its
absence looked like a withdrawal and `stash.Clear()` deleted it irrecoverably, with no log line
(the "crafted sword" scenario, `:38-42`). (2) The first attempted fix tested
`item.WeaponDesign != null`, which is true for **every** `<CraftedItem>` definition — 260 in
`SandBoxCore/ModuleData/items/weapons.xml` plus 23 tournament weapons in Native v1.4.8 — so
roughly 283 ordinary vanilla weapons (most swords, axes, mauls, spears, polearms) stopped
syncing entirely: a vanilla sword stashed on the host never reached the client.

**Mechanism.** `IsMachineLocal(ItemObject)` (`:220-234`) returns true only for
`item.IsCraftedByPlayer` (true only for genuinely player-crafted items) **or** when
`!ReferenceEquals(MBObjectManager.Instance.GetObject<ItemObject>(item.StringId), item)` — i.e.
the id does not round-trip to the same object locally. Any throw returns true: "unreadable =
unexpressible — err toward preserving it" (`:230-233`). `BuildPayload` skips such stacks and
counts them, logging "`<n>` machine-local (crafted/unregistered) stack(s) left out of the
snapshot" (`:193-209`). `ApplyPayload` preserves them across the `Clear` (`:335-345`,
`:362-365`).

**Patched members.** None. Read: `ItemObject.IsCraftedByPlayer`;
`MBObjectManager.Instance.GetObject<ItemObject>(string)`. `ItemObject.WeaponDesign` is the
**rejected** test, documented as a trap at `:213-218`.

**Limitations.** Crafted items are never shared; they live on whichever machine crafted them.
Classification is per-machine, so peers on different mod sets or versions can disagree — handled
by the duplication guard below.

**Self-test.** Not covered by the headless suite (it needs a real `ItemObject`); the ~283-weapon
regression is pinned only by the source comment and the review record.

### `payloadIds` duplication guard on preserved stacks

**README item** 16 · **Source** `Payload/StashSync/StashSyncGuard.cs` · **Class**
`StashSyncGuard` · **Tag** `[STASH-SYNC]` · **Config** `stashSync` · **Scope** receiver (both
roles can be the receiver)

**Bug.** Preservation assumes both machines classify an item the same way. If they do not — a
peer on an older version that sent everything resolvable, a differing mod set, or the
`catch { return true; }` fallback firing on only one side — the receiver would apply the peer's
stack **and** re-add its own preserved copy, silently duplicating the item.

**Mechanism.** Before preserving, build a `new HashSet<string>(StringComparer.Ordinal)` of every
`ItemStringId` the payload mentions (`:330-334`) and preserve a local machine-local stack only
when its id is **not** in that set (`:336-345`). The rule stated in the comment: "the payload's
word wins for ids it mentions" (`:327-329`).

**Limitations.** Ordinal comparison of `StringId`s only — a modifier difference is not
considered, so a machine-local stack of the same item id with a different `ItemModifier` is
dropped rather than preserved.

**Self-test.** Not pinned by a test — comment-documented only.

### `ResolveStashModeValue` — live enum resolution

**README item** 16 · **Source** `Payload/StashSync/StashSyncGuard.cs` · **Class**
`StashSyncGuard` · **Tag** `[STASH-SYNC]` · **Config** `stashSync` · **Scope** both/local

**Bug.** The first version hard-coded `private const int StashMode = 3`. Nothing verified it: if
the `InventoryMode` enum ordinal shifted in a game update, stash mode would be silently
mis-detected — no stash would ever sync, or the wrong screen would — while
`Diag.Report("stash-sync", ok, …)` still printed "active", because `ok` only reflects patch
success.

**Mechanism.** `ResolveStashModeValue()` (`:95-115`) resolves
`Helpers.InventoryScreenHelper+InventoryMode` via `AccessTools.TypeByName`, checks `IsEnum`,
does `Enum.Parse(mode, "Stash")` and `Convert.ToInt32`, and stores the result in the static
`_stashMode`. 3 remains only the fallback (`:52-55`). A value change is announced:
`[STASH-SYNC] InventoryMode.Stash resolved to <n> (fallback was <m>) — using the live value`
(`:104-107`). A resolution failure logs "could not resolve InventoryMode.Stash (`<msg>`) — using
fallback 3" rather than throwing (`:111-114`).

**Limitations.** If the enum type or the member **name** changes — not just the ordinal — it
falls back to 3 and only logs; it does not turn the guard red.

**Self-test.** Not pinned; the loopback self-test covers framing only.

### `IsLocalStashScreenOpen` — apply deferral with warn-once

**README item** 16 · **Source** `Payload/StashSync/StashSyncGuard.cs` · **Class**
`StashSyncGuard` · **Tag** `[STASH-SYNC]` · **Config** `stashSync` · **Scope** receiver / local

**Bug.** (1) A peer's update applied while the local player has that stash screen open would
clear the roster underneath a live screen — the screen works on the live `ItemRoster`. (2) The
original check used best-effort reflection with a bare `catch {}` returning "not open", so if
the member name were wrong the deferral would never engage and updates would clear the roster
under a live screen with zero diagnostic.

**Mechanism.** `Tick()` checks `IsLocalStashScreenOpen()` while still holding the queue lock and
**returns without dequeuing**, so the update is applied after the screen closes (`:285-289`).
`IsLocalStashScreenOpen` (`:382-411`) walks `Campaign.InventoryManager` → `.InventoryLogic` by
reflection and compares its `_inventoryMode` to `_stashMode`. It distinguishes "no inventory
session" (`manager == null`, genuinely not open) from "reflection broke" (`managerProp == null`,
or a non-null manager with `logicProp == null`) and, in the broken case, sets an
`_openCheckWarned` latch and logs once: "cannot detect an open inventory screen
(Campaign.InventoryManager reflection broke — game update?) — peer updates apply immediately"
(`:390-400`).

**Patched members.** None. Read by reflection: `Campaign.InventoryManager`,
`<InventoryManager>.InventoryLogic`, `InventoryLogic._inventoryMode`.

**Limitations.** Fails **open** by design — when the reflection chain is broken it applies
immediately rather than blocking sync forever; it only makes that audible. Deferral is unbounded:
queued updates pile up while a stash screen stays open. Last-closed screen wins on a simultaneous
edit (`:33-34`).

**Self-test.** None — the warn-once line is the diagnostic.

### Host relay of applied client updates

**README item** 16 · **Source** `Payload/StashSync/StashSyncGuard.cs` · **Class**
`StashSyncGuard` · **Tag** `[STASH-SYNC]` · **Config** `stashSync` · **Scope** host only

**Bug.** With more than two peers, a client's stash edit would reach only the host — the other
clients would never converge, because a client's `SendRaw` goes to the server, not to peers.

**Mechanism.** At the end of `ApplyPayload`, if
`PeerDetection.ReadCoopStaticBool("IsHost") == true` **and** `AnyRemotePeerConnected() == true`,
the host re-frames and re-broadcasts the payload it just applied (`:371-376`). This is safe
because applying never sends — the send path is only the `DoneLogic` postfix — so there is no
echo loop; and the origin client simply re-applies its own identical state, which is idempotent
under full-snapshot semantics (`:29-31`, `:371-373`).

**Patched members.** None: `CoopSession.Server.BroadcastRawReliableOrdered(byte[])`.

**Limitations.** The relay re-broadcasts the **original** payload, not the host's post-apply
roster — so machine-local stacks the host preserved are not (and cannot be) announced. The
origin client burns a redundant apply.

**Self-test.** Not pinned by a test.

### Stash wire model (`StashPayloadData`)

**README item** 16 · **Source** `Payload/StashSync/StashPayloadData.cs` · **Class**
`StashPayloadData` · **Tag** none · **Config** none · **Scope** both

**Bug.** The original parser bounded `entryCount` but not per-entry `Count`. A corrupt or
truncated packet carrying `Count = -1` flowed straight into `stash.AddToCounts(..., -1)` on a
freshly cleared roster, corrupting the receiver's stash. Worse, the first test suite **asserted**
that negative counts round-trip, so the test encoded the bug; the fixed tests are
`tests/StashPayloadTest/Program.cs:37-53`.

**Mechanism.** A pure class with no TaleWorlds dependency (`:9-12`). Format (`:18-23`):
`byte FormatVersion | string SettlementStringId | int32 entryCount | entryCount ×
{ string ItemStringId, string ModifierStringId ("" = none), int32 Count }`. It is a full
**snapshot**, not a delta — idempotent to re-apply, immune to ordering, and it converges in one
packet (`:15-17`). `FromBytes` never throws: null on null or short input (`:70-73`), null on a
`FormatVersion` mismatch (`:83-86`), null when count < 0 or > 100000 (`:89-92`), null on any
entry with an empty `ItemStringId` or `Count <= 0` — "a sane sender never emits these — corrupt
packet" (`:101-104`) — plus a blanket catch (`:110-113`). `ToBytes` coalesces nulls to `""`
(`:54-59`).

**Limitations.** The blast radius of one bad entry is the **whole packet**: `return null` drops
the entire stash update rather than the offending stack — the receiver logs "received a
malformed stash packet — dropped" — accepted as fail-safe since the sender can no longer emit
those. No modifier-level machine-local classification. A 100000-entry cap.

**Self-test.** `tests/StashPayloadTest/Program.cs` pins: a typical three-stack stash; an
**emptied** stash (must round-trip, not degrade to null, `:28`); unicode ids ("町_têst_9",
"épée_d'or", "rouillé"); negative count rejected, zero count rejected, empty item id rejected
(`:39-53`); a 500-stack hoarder stash (`:56-61`); and null / short / magic-only-no-body /
truncated / unknown-format-version all returning null (`:82-89`).

### Stash framing (`StashWireFraming`)

**README item** 16 · **Source** `Payload/StashSync/StashWireFraming.cs` · **Class**
`StashWireFraming` · **Tag** none · **Config** none · **Scope** both

**Bug.** Two features now ride the same free byte-0 slot on BT's channel. With a single shared
magic, a birth packet and a stash packet would be indistinguishable and each feature's receive
hook would try to parse the other's bytes.

**Mechanism.** Identical transport facts to `BirthWireFraming` — leading byte 0 is the one
`PacketType` value BT never dispatches, so our packets are a no-op inside BT even unintercepted
(`:7-10`) — but a **different** 4-byte magic: `'B','T','C','S'` = "BannerlordTogether
Crash-guard Stash" (`:18-19`) versus birth's `BTCG` = "BannerlordTogether Child Guard"
(`BirthWireFraming.cs:23`). Each feature's receive hook recognizes exactly its own magic and
passes everything else through (`:10-12`). Frame = `[0x00][BTCS][StashPayloadData bytes]`
(`:13`). `IsOurPacket` checks 5 leading bytes; `TryUnframe` returns null and never throws
(`:37-64`).

**Limitations.** `HeaderLength` (5) and the marker constant are duplicated per feature rather
than shared — an intentional cost of keeping each wire file engine-free and independently
linkable into its test.

**Self-test.** `tests/StashPayloadTest/Program.cs:63-79` pins four-way discrimination — a stash
frame recognized as stash, a birth frame not read as stash, a stash frame not read as birth, a
birth frame still recognized by birth — plus the byte-0 marker gate against first bytes 1..255
followed by our exact magic. `StashSyncGuard.LoopbackSelfTest` re-pins the same discrimination
in-game (`:479-483`).

### Network-thread → main-thread queue hop (both sync features)

**README item** 15, 16 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs`,
`Payload/StashSync/StashSyncGuard.cs` · **Class** `PregnancySyncGuard`, `StashSyncGuard` ·
**Tag** `[PREG-SYNC]` / `[STASH-SYNC]` · **Config** `pregnancySync` / `stashSync` · **Scope**
receiver (client for births; both roles for stash)

**Bug.** BT's `ShouldAcceptIncomingPacket` runs on BT's LiteNetLib **network thread**.
`HeroCreator`, `MBObjectManager` and `ItemRoster` mutation must never run off the game thread —
doing so is an engine-corruption and crash path.

**Mechanism.** The receive prefix does only thread-safe byte parsing, then enqueues under a lock
into a static `Queue<T>` (`PregnancySyncGuard.cs:40-41`, `:326-333`; `StashSyncGuard.cs:57-58`,
`:248-254`). `PayloadEntry.Tick` (`Payload/PayloadEntry.cs:151-152`) calls each guard's `Tick`,
which dequeues under the same lock and does the engine work on the main thread
(`PregnancySyncGuard.cs:99-127`, `StashSyncGuard.cs:270-300`). Each drained item is wrapped in
its own try/catch so "a throw here … must never escape onto the game loop. Drop this birth and
keep draining" (`PregnancySyncGuard.cs:118-126`; `StashSyncGuard.cs:291-298`).

**Patched members.** BT `ShouldAcceptIncomingPacket` (the network-thread entry point).

**Limitations.** The queue is unbounded — a flood of packets would grow it; the stash queue
additionally stalls entirely while a local stash screen is open. Dequeue happens only while the
game is ticking, so packets arriving on a menu screen wait.

**Self-test.** The thread hop itself is not pinned by a test; the parse half is covered by both
loopback self-tests.

### Multi-name BT receive-hook resolution (`HookReceive`)

**README item** 15, 16 · **Source** `Payload/PregnancySync/PregnancySyncGuard.cs:225-239`,
`Payload/StashSync/StashSyncGuard.cs:117-131` · **Class** `PregnancySyncGuard`,
`StashSyncGuard` · **Tag** `[PREG-SYNC]` / `[STASH-SYNC]` · **Config** `pregnancySync` /
`stashSync` · **Scope** both

**Bug.** BannerlordTogether moved its network classes to `BannerlordTogether.Network.*` — the
2026-09-01 health line showed pregnancy-sync and stash-sync both "not resolved", i.e. both
features had silently gone dead after a BT update (`CHANGELOG.md:129-132`).

**Mechanism.** `HookReceive` iterates four candidate type names in priority order —
`BannerlordTogether.Network.CoopNetworkBase`, `BannerlordTogether.Network.CoopServer`,
`BannerlordTogether.CoopNetworkBase`, `BannerlordTogether.CoopServer` — resolving each with
`AccessTools.TypeByName` and patching `ShouldAcceptIncomingPacket` wherever it is found,
returning true if any patch landed. Both the base and the `CoopServer` **override** are patched
deliberately (`PregnancySyncGuard.cs:21-22`).

**Limitations.** A future rename of the **method** (not the namespace) still silently degrades —
the only signal is `Diag.Report('pregnancy-sync', false, 'BT receive method not found')` or
`receive=False` in the degraded line (`PregnancySyncGuard.cs:65`; `StashSyncGuard.cs:82-85`).
Because both features patch the same method, the stash prefix must pass non-stash bytes through
so the birth hook can also see them (`StashSyncGuard.cs:243`).

**Self-test.** The health/`Diag` line is the detector; the loopback tests do not exercise the
real BT method.

---

## Time control

`timeAlwaysFlows` and `shareTimeControl` gate two of these fixes; `tracing` gates `TimeTrace`.
`TimeEnforcementGuard`, `MapClickSpeedKeeper` and `JoinSyncPauseEscape` have no config key of
their own. Note that `TimeFlowPatch` and `ShareTimeControl` read their key with their own
one-shot regex, not through `GuardConfig`, and only the literal `false` disables them.

### Main-party idle-hold suppressor (`timeAlwaysFlows`)

**README item** 13 · **Source** `Payload/TimeFlowPatch.cs` · **Class** `TimeFlowPatch` ·
**Tag** `[TIME-FLOW]` · **Config** `timeAlwaysFlows` · **Scope** both — solo, host and client;
the patch is unconditional per process

**Bug.** Campaign time silently stops when your main party arrives at a clicked destination or
goes idle, even though the time-control mode (play / fast-forward) never changed — the speed
buttons still look "playing" but the clock is frozen until you click somewhere new.

**Mechanism.** Harmony **postfix** on every declared `MobileParty.ComputeIsWaiting` overload.
`Apply()` enumerates `typeof(MobileParty).GetMethods(Public|NonPublic|Instance|DeclaredOnly)`
and patches each non-abstract method named exactly `ComputeIsWaiting` (`:44-52`). The postfix
(`:61-79`) forces `__result=false`, but only when `__result` was already true, the feature is
enabled, `__instance != null` and `__instance.IsMainParty` — so `Campaign.TickMapTime`'s
`IsMainPartyWaiting = MobileParty.MainParty.ComputeIsWaiting()` write can never latch true for
the player. The whole postfix body is wrapped in `catch{}` (`:76-78`) so it can never throw into
the campaign tick.

**Patched members.** `MobileParty.ComputeIsWaiting` (all declared, non-abstract overloads).
Read: `MobileParty.IsMainParty`.

**Limitations.** Main party only — AI parties keep vanilla waiting behaviour (`:18-19`). Real
pauses are untouched: Stop mode via the pause button, menus, encounters (`:17-18`). The
wait-menu mode `UnstoppableFastForwardForPartyWaitTime` never consults `IsMainPartyWaiting`, so
wait menus are unaffected (`:19-20`). Config is read once and cached in a `bool?` (`:24`,
`:27-37`), so flipping `guardconfig.json` mid-session only takes effect after a payload reload.
`ReadConfig` only recognizes the literal regex `"timeAlwaysFlows"\s*:\s*false` (`:90`); any
other value, a missing file, or a read exception silently defaults to **enabled** (`:96-99`).
The config path is derived from the assembly location + `"../.."` (`:85-86`). No `Diag.Report`.

**Self-test.** None registered. The only observable evidence is the apply line
`[TIME-FLOW] timeAlwaysFlows=<bool> (patched N method(s))` (`:53`) and the one-shot
`_loggedActive` line on first suppression (`:70-74`).

### `EnforcePlaySpeed` neutralizer

**README item** 13 · **Source** `Payload/TimeEnforcementGuard.cs` · **Class**
`TimeEnforcementGuard` · **Tag** `[TIME-GUARD]` · **Config** none — always on · **Scope** host /
solo-host; active only while no remote player is connected, fully inert with a peer connected

**Bug.** After loading a save mid-session while hosting alone, fast-forward stops working until
you relaunch the game: BannerlordTogether's `CoopCampaignBehavior.EnforcePlaySpeed` runs every
campaign tick and forces `UnstoppablePlay`, stomping whatever speed the player picked (evidence:
`CrashGuard.log` 2026-08-19 00:07-00:08) (`:9-12`).

**Mechanism.** A two-layer neutralizer, "run but neutralize". (1) **Prefix + finalizer** on
every declared non-abstract `CoopCampaignBehavior.EnforcePlaySpeed`, found by name over
`Public|NonPublic|Static|Instance|DeclaredOnly` on the type resolved via
`PeerDetection.FindCoopType("CoopCampaignBehavior")` (`:56-80`). `EnforcePrefix` re-evaluates
peer state at most every 2000 ms (`Environment.TickCount`, with an explicit wraparound guard
`now < _lastCheckTick`) and, when confidently alone, sets the `[ThreadStatic]`
`_inSoloEnforce = true` (`:147-178`); `EnforceFinalizer` clears it and returns `__exception`
unchanged so exceptions still propagate (`:180-184`). (2) **Prefix**
`BlockSoloEnforceWritePrefix` on Campaign's time setters — patched for each of
`set_TimeControlMode`, `SetTimeControlModeLock` and `set_TimeControlModeLock` that
`AccessTools.Method` finds on `AccessTools.TypeByName("TaleWorlds.CampaignSystem.Campaign")` —
returning `!_inSoloEnforce`, i.e. skipping the original **only** for writes made on this thread
inside the enforcer while solo (`:84-92`, `:186-189`). BT's method itself always runs, so its
bookkeeping and sync side effects stay fresh; with a peer connected nothing is touched
(`:16-21`, `:164-167`).

This guard is also the only in-repo caller of `PeerDetection.NoteCoopActivity()` (`:234`), the
packet-liveness stamp, and it consumes `PeerDetection.Snapshot()` (`:160`). It additionally
installs a shared-pause tracer — log-only prefixes on BT `CoopSubModule.SetPaused` and
`ApplyTimeState` — whose apply evidence is
`[TIME-GUARD] shared-pause tracer active on N method(s)` (`:136`); `_pauseTraceApplied` latches
after the first successful application (`:133-137`), and that tracer is **not** gated on
`tracing`.

**Patched members.** BT `CoopCampaignBehavior.EnforcePlaySpeed` (prefix + finalizer, all
declared overloads); `Campaign.set_TimeControlMode` (prefix, skip-original);
`Campaign.SetTimeControlModeLock` and `Campaign.set_TimeControlModeLock` (prefix,
skip-original — whichever of the two lock members exists).

**Limitations.** Fails **toward co-op**: it neutralizes only on a confident "no session" —
`PeerDetection.AnyRemotePeerConnected() != false`, so an unknown (null) peer state counts as
connected and enforcement is left fully intact (`:155-156`). Peer state is re-read only every
2 s, so the switch back to full enforcement when a peer joins lags up to 2 s (`:152`). The
setter prefixes are installed only if at least one `EnforcePlaySpeed` was patched (`count > 0`,
`:81-83`). `_applied` latches, so a second `Apply` is a no-op — but `Apply` is deliberately
retried from `PayloadEntry.OnBeforeInitialModuleScreen` and `OnGameStart` because the BT
assembly may load after us (`Payload/PayloadEntry.cs:122`, `:128`; `:56-59`). The setter block
is thread-scoped, so an unrelated write on another thread during the enforcer window is
unaffected. No `Diag.Report`, no self-test. Scoping this neutralizer to the campaign map was
tried on 2026-09-04 and **reverted** (`docs/ENGINE-NOTES.md:55-57`). The BT member names were
taken from **runtime stack traces**, not from a decompile (`:23-24`), so a BT rename silently
disables the tracer — the only reveal is the "could not trace" line (`:130`) or a missing
"shared-pause tracer active" line. The packet-frame heuristic is name-based (a "Packet"
substring) and depth-bounded to 12 frames, so a deeply-nested or renamed handler is missed, in
which case the liveness stamp simply does not fire (a fail-safe direction).

**Self-test.** None registered. Observable state transitions are logged: the apply line
`[TIME-GUARD] EnforcePlaySpeed neutralizer active (N method(s)) — runs every tick, writes
blocked while no remote player is connected` (`:94`), the peer-state edge line including
`PeerDetection.Snapshot()` (`:160`), and a once-per-edge "neutralizing EnforcePlaySpeed
time-writes" line gated by `_skipLogged` (`:169-173`).

Note the interaction with the `[TIME]` tracer: with `tracing=true` while hosting alone at the
co-op setup menu, BT re-requests `UnstoppablePlay` every tick, this guard blocks the write, the
mode never changes, and BT retries forever. That is why the `[TIME]` tracer routes through
`TraceThrottle` — the blocking behaviour itself is unchanged, only the logging is collapsed
(`CHANGELOG.md:5-12`).

### Shared time control (auto-grant to the client)

**README item** 13 · **Source** `Payload/ShareTimeControl.cs` · **Class** `ShareTimeControl` ·
**Tag** `[SHARE-TIME]` · **Config** `shareTimeControl` · **Scope** host / authority only; the
client process no-ops

**Bug.** The joining player cannot pause, un-pause, set normal speed or fast-forward — they are
stuck at whatever speed the authority broadcasts, and BT prints "[BT] Client time controls are
disabled by the host." (observed live 2026-08-19) (`:12-17`). BT ships
`AllowClientTimeControl` off (`docs/UPSTREAM_CONTRIBUTION.md:64-67`).

**Mechanism.** Not a Harmony patch — a polled reflection driver. `ShareTimeControl.Tick()` is
called every frame from `PayloadEntry.Tick` (`Payload/PayloadEntry.cs:147`) and self-throttles
to one attempt per 3000 ms via `Environment.TickCount` with a wraparound guard (`:62-67`).
`Resolve()` (`:152-188`) finds `CoopSubModule` and `CoopSession` via
`PeerDetection.FindCoopType`, then
`AccessTools.Method(_coopSubModule, "ToggleClientTimeControlPermission", new[]{ typeof(bool).MakeByRefType(), typeof(string).MakeByRefType() })`
and `AccessTools.Method(_coopSubModule, "IsClientTimeControlEnabledForCurrentMenu")`, plus
`CoopSession.IsHost` resolved as a **property** first, falling back to a **field**
(`:167-174`). Only the authority acts (`!IsHost()` → return, `:69-72`). If
`IsClientTimeControlEnabledForCurrentMenu()` already returns true it latches and logs. Otherwise
it invokes the toggle through an `object[2]` and **trusts the out params**, not the (possibly
void) return: `args[0] is bool && (bool)args[0]` = enabled, `args[1] as string` = reason
(`:121-136`). Because it is a **toggle**, if it comes back `(false, null reason)` — meaning the
menu check lied and it toggled the wrong way — it invokes once more to force on (`:94-102`). On
success: `_grantedLogged = true`, a log line and an on-screen notice (`:103-108`).

**Patched members.** None patched. Invoked: BT
`CoopSubModule.ToggleClientTimeControlPermission(out bool, out string)`,
`CoopSubModule.IsClientTimeControlEnabledForCurrentMenu()`. Read: BT `CoopSession.IsHost`
(static property or static field).

**Limitations.** Once granted it stops forever (`_grantedLogged` gate, `:56-61`) — a later host
toggle-off is deliberately respected, and this prevents off/on churn from a misread state check.
The no-arg toggle overload auto-targets the single gameplay client, so this is correct for the
two-player (host-or-dedicated) case only (`:17-20`). The client process no-ops entirely. Benign
reasons containing "no longer connected" or "No connected" are silent; anything else logs a "not
granted yet (`<reason>`) — will retry" line (`:109-113`). `_resolved` latches after **one**
resolution attempt — if BT loaded after the first `Tick`, resolution is never retried
(`:152-158`). Same one-shot regex config read as `TimeFlowPatch`: only the literal
`"shareTimeControl"\s*:\s*false` disables; anything else defaults on (`:190-209`). No
`Diag.Report`, no self-test.

**Known asymmetry.** `Resolve()` sets `_resolved = true` before it knows whether
BannerlordTogether resolved, and it is driven only from `PayloadEntry.Tick` — there is no
lifecycle retry. If BT's assembly were not loaded on the first application tick, shared time
control would stay off for the whole process. `TimeEnforcementGuard` (`:56-59`) and
`JoinSyncPauseEscape` (`:69-73`) instead return without latching and are re-applied from
`OnBeforeInitialModuleScreen` / `OnGameStart` (`Payload/PayloadEntry.cs:119`, `:122`, `:128`).
Latch the *success*, not the *attempt*.

**Self-test.** None registered. Health evidence is the
`[SHARE-TIME] shared time control enabler active` line (`:180`) or, on drift,
`[SHARE-TIME] required method(s) not found (toggle=… menuCheck=…) — shared time control INACTIVE (mod version changed?)`
(`:177`).

### Map-click speed keeper

**README item** 13 · **Source** `Payload/MapClickSpeedKeeper.cs` · **Class**
`MapClickSpeedKeeper` · **Tag** `[CLICK-SPEED]` · **Config** none · **Scope** both (installed
unconditionally; the bug is co-op-specific because only BT enforces the Unstoppable variant)

**Bug.** In co-op, every click-to-move on the campaign map drops the session out of
fast-forward to normal speed and the co-op sync then yanks it back up — a visible fast-forward
flip-flop (observed 2026-08-19 20:18-20:19; every `UnstoppableFastForward` → `StoppablePlay`
transition came from `MapScreen.HandleLeftMouseButtonClick`). Vanilla's "map double click
behavior = keep speed" option only preserves `StoppableFastForward`
(`MapScreen.HandleClickTimeChange` checks `mode==4`) and does not recognize the **unstoppable**
fast-forward variant the co-op mod enforces (`:9-21`).

**Mechanism.** A **prefix + finalizer** on every declared non-abstract
`SandBox.View.Map.MapScreen.HandleLeftMouseButtonClick` (type via `AccessTools.TypeByName`) set
and clear a `[ThreadStatic]` `_inMapClick` flag (`:33-51`, `:68-77`). Then — only if at least
one click method was patched — a **prefix** on
`AccessTools.Method(typeof(Campaign), "set_TimeControlMode")` vetoes exactly one transition:
`_inMapClick && value == CampaignTimeControlMode.StoppablePlay && __instance.TimeControlMode == CampaignTimeControlMode.UnstoppableFastForward`
returns false, skipping the original (`:52-59`, `:79-100`). Everything else passes through
vanilla; clicking while paused still unpauses, because the Stop → StoppablePlay transition is
untouched (`:20-21`).

**Patched members.** `MapScreen.HandleLeftMouseButtonClick` (prefix + finalizer, all declared
instance overloads); `Campaign.set_TimeControlMode` (prefix, conditional skip-original). Read:
`Campaign.TimeControlMode`, `CampaignTimeControlMode.StoppablePlay` / `.UnstoppableFastForward`.

**Limitations.** Vetoes only the `UnstoppableFastForward` → `StoppablePlay` pair; a
`StoppableFastForward` → `StoppablePlay` click-downgrade is left to vanilla's own option. The
setter prefix is installed only when `count > 0` (`:52-53`). If `MapScreen` is not found the
keeper logs "MapScreen not found — keeper idle" and returns without patching the setter
(`:33-38`). The flag is `[ThreadStatic]`, so a time write raised asynchronously from another
thread during a click is not covered. The first veto logs once (`_logged`, `:88-92`). No config
key, no `Diag.Report`, no self-test.

**Self-test.** None; apply evidence is
`[CLICK-SPEED] map-click fast-forward keeper active (N click method(s))` (`:60`).

### Campaign time-control tracer

**README item** 26 · **Source** `Payload/TimeTrace.cs` · **Class** `TimeTrace` · **Tag**
`[TIME]` · **Config** `tracing` · **Scope** both (diagnostic; no peer gating)

**Bug.** Diagnostic, not a fix. The symptom being chased: clicking things on the map (a city,
say) sometimes drops fast-forward when only the pause/play buttons should change speed — the
code path forcing the change was unknown (`:11-14`).

**Mechanism.** Four log-only hooks applied via a generic
`PatchByName(harmony, typeName, methodName, prefixName, postfixName)` helper that resolves the
type with `AccessTools.TypeByName` and patches every declared non-abstract method of that name
(`:38-79`). `Campaign.set_TimeControlMode` gets a **prefix** that skips no-op sets
(`__instance.TimeControlMode == value`) and merely captures the old mode, new mode and rendered
stack into `[ThreadStatic]` fields (`:83-104`), plus a **postfix** that reads the actual mode
afterwards, appends "`^ change SUPPRESSED/ALTERED by another patch — actual mode now <X>`" when
it differs, and emits through `TraceThrottle.Emit` with a dedup key that deliberately ignores
the (identical) stack:
`"TIME " + old + "->" + new + (suppressed ? " SUPPRESSED->" + actual : " applied")`
(`:106-128`). `SetTimeControlModeLock` and `set_TimeControlModeLock` (whichever exists) get a
`LockPrefix` that prints all `__args` (`:130-152`).
`MapTimeControlVM.ExecuteTimeControlChange` gets a `UiButtonPrefix` that marks a genuine UI
button click, so button-driven changes are distinguishable from code-driven ones (`:154-164`).
`Stack()` renders up to 14 frames from depth 2, skipping `HarmonyLib.*`,
`BLTDeploymentCrashGuard.*` and `System.*` frames, and printing the bare method name for frames
with a null `DeclaringType` — the `DMD<…>` dynamic-method frames that name the original patched
caller (`:166-212`).

**Patched members.** `Campaign.set_TimeControlMode` (prefix + postfix);
`Campaign.SetTimeControlModeLock` (prefix); `Campaign.set_TimeControlModeLock` (prefix);
`MapTimeControlVM.ExecuteTimeControlChange` (prefix).

**Limitations.** Applied only when `tracing` is true. Purely observational: it changes no
behaviour. The pending-capture fields are `[ThreadStatic]`, so a prefix on one thread and a
postfix on another would lose the pairing. Only the `set_TimeControlMode` path is throttled; the
lock and UI-button prefixes call `Log.Info` directly and can still flood if hammered. It reports
suppression by comparing post-state, so a patch that re-sets the same value looks "applied".
Coalescing means a run's tail count flushes on its next repeat or window, not instantly
(`Payload/TraceThrottle.cs:34-37`).

**Self-test.** None; apply evidence is `[TIME] time-control tracer active on N method(s)`
(`:42`) — N is the count actually patched, so a drifted member shows up as a lower N plus a
"could not patch `<type>.<method>`" line (`:70`).

### Join-hold pause escape

**README item** 17 · **Source** `Payload/JoinSyncPauseEscape.cs` · **Class**
`JoinSyncPauseEscape` (Diag component `join-sync-pause-escape`) · **Tag** `[JOIN-ESCAPE]` ·
**Config** none · **Scope** host (the authority pressing its own pause / normal-speed keys); no
client behaviour

**Bug.** "I can't unpause after someone joined" (field log 2026-08-22 23:43-23:49). A joining
player's save transfer pauses the host for the whole download, load and hero creation; the
host's pause key does nothing and shows no message at all; and a joiner stuck in a retry loop
froze the host forever (`:8-21`).

**Mechanism.** A **postfix** on `CoopSubModule.ToggleHostManualPause` taking `bool __result`
(the press was handled) and, when present, a postfix on `CoopSubModule.ApplyHostNormalSpeed`
that calls the same handler with `handled=true` (`:109-113`, `:230-238`). `HandleTimePress`
computes `armed` from a 6000 ms window (`ArmWindowMs`, with a `TickCount` wraparound guard
`now - _armedAtTick >= 0`), reads which join reasons currently hold the pause, and dispatches on
the pure function `Decide(pressHandled, stillPaused, joinHoldActive, cancelArmed)`
(`:240-278`). **Arm** → `Log.Screen` names who is holding time and states the window, plus a log
line (`:249-253`). **Cancel** → `CancelJoinSync` invokes BT's own transfer-cancel router
`A("host-cancelled", "The host cancelled the join sync to keep playing. Reconnect to join again.", true)`,
then `CoopSubModule.SetPaused(false, "Host", true, "join-escape")` to clear the manual pause
reason our own presses toggled on, then records a fire plus `Log.Screen` and `Log.Info`
(`:313-335`). Paused state is read via `PeerDetection.ReadCoopStaticBool("IsPaused") == true`
(`:247`). Reason state is read live per query — `_pauseCoordinatorField.GetValue(null)` each
time — so a reassigned coordinator is survived (`:47`, `:286`).

**Patched members.** BT `CoopSubModule.ToggleHostManualPause() -> bool` (postfix; the return
type is **validated** as bool at apply time); BT `CoopSubModule.ApplyHostNormalSpeed` (postfix,
optional). Invoked, not patched: BT `CoopSubModule.MapPauseReason(string)` (for `"SaveSync"` and
`"HeroCreation"`); the pause coordinator's `IsActive(reason)` (found by signature, not name);
the obfuscated save-transfer coordinator's static `A(string,string,bool)`;
`CoopSubModule.SetPaused(bool,string,bool,string)`. Read: BT
`CoopSubModule._pauseCoordinator` (NonPublic|Static field, read live); BT `CoopSession.IsPaused`.

**Limitations.** Self-disabling by design: it acts only when a `SaveSync`/`HeroCreation` reason
is actively holding the pause **and** the player presses a time key — if BT unblocks legacy
joins upstream this never fires and shows as never-fired in the health report (`:34-36`). It
never offers a cancel on uncertainty: `HeldJoinReasons` returns null when the coordinator is
null or the query throws (`:280-311`). `Apply` is strictly validated and **refuses to install**
if `ToggleHostManualPause` is missing or does not return bool, or if `MapPauseReason`,
`SetPaused` or `_pauseCoordinator` are missing — logging the exact missing list and
`Diag.Report(false)` (`:82-93`); a second gate covers the reason query, the cancel router and
both boxed enum values (`:100-107`). `_applied` latches; `Apply` is retried from
`PayloadEntry.OnBeforeInitialModuleScreen` because BT may load late
(`Payload/PayloadEntry.cs:119`; `:69-73`). `FindTransferCancel` scans only the first assembly
named exactly `BannerlordTogether` and returns null if the fingerprint misses (`:169-226`).
Resolved targets are pinned against BT v0.5.0.1 (`:45`). Cancelling is destructive to the
joiner's in-flight transfer — hence the two-press consent gate.

**Self-test.** `join-sync-pause-escape.contract` (`:117`, id at `:361`). It pins three things:
(1) all five reflection targets resolved — `_reasonActiveQuery`, `_cancelTransfer`,
`_setPaused`, `_reasonSaveSync`, `_reasonHeroCreation` (`:341-342`); (2) the reason query is
invocable as a pure read without throwing, against the live coordinator (`:344-353`); (3) the
full `Decide` truth table — `Decide(false,true,true,true)==None` (press not handled, never act),
`Decide(true,false,true,true)==None` (game unpaused fine), `Decide(true,true,false,true)==None`
(no join hold), `Decide(true,true,true,false)==Arm` (the first swallowed press explains and
arms), `Decide(true,true,true,true)==Cancel` (the second press cancels) (`:354-359`). The
failure detail names which of targets / queryReads / logic failed plus "(BT update?)" (`:363`).
`Diag.Report("join-sync-pause-escape", …)` runs on every apply path (`:91`, `:105`, `:116`,
`:122`) and a fire is recorded on an actual cancel (`:326`).

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
workflow. `EnsureLoaded` (`:247-263`) is what raises the loud on-screen
`CRASH GUARD NOT ACTIVE` warning if the payload ever fails to load — a failed payload load used
to be silent and the player kept playing unguarded (`CHANGELOG.md:309-311`). LoadFrom-dedup
detection compares assembly `Location` strings (`:315-324`) and a type-load failure writes a
one-off `[HOTRELOAD][DIAG]` binding-diagnostics evidence pack including the harness-bound
`0Harmony` identity (`:194-233`).

**Limitations.** Any exception in the shadow-copy path falls back to a byte-load with a logged
warning (`:327-329`). Shadow files accumulate on disk until `CleanStaleShadows` runs, which
happens once, when `_current == null` — so a long dev session accumulates one shadow per reload
attempt until the next launch. Unpatch failure of the previous generation is logged but
tolerated, so both generations' patches can coexist after a partial failure. `EnsureLoaded` is
wired only at `OnGameStart` (`:119`) and `OnBeforeInitialModuleScreen` (`:130`), so `Tick()` and
`OnMissionInit()` never retry (`:90-126`). The binding-diagnostics pack is written once per
type-load failure only. `_pendingReload` is `volatile` but `_debounceTick` is a plain `int` written from
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

### Fire tracking (auto-retire detection) and the self-test runner

**README item** 25 · **Source** `Harness/SelfHealing.cs` · **Class** `SelfHealing` · **Tag**
`GUARD ACTIVITY:` · **Config** `selfTest` (read by `PayloadEntry`) · **Scope** both

**Bug.** A guard whose underlying bug BT or TaleWorlds has since fixed keeps running forever,
and nobody can tell which guards are still earning their keep.

**Mechanism.** Every guard calls `SelfHealing.RecordFire(guard)` each time it actually
suppresses a crash or corrects state; counts live in a lock-protected `Dictionary<string,int>`
with `StringComparer.Ordinal` (`:28`, `:43-57`). `FireSummary()` renders
`GUARD ACTIVITY: none fired this session (nothing crashed on a guarded path)` or
`GUARD ACTIVITY: guard=N, …` (`:59-81`). A crash-guard finalizer that never fires across a
session did nothing — the bug it guards no longer occurs — so a permanently-inert guard is safe
to retire (`:9-14`). Fire counts are deliberately kept across reloads; only tests are cleared,
which proves shared state survived (`:94-96`). `RunSelfTests` is gated by `selfTest`
(`:108-110`).

**Limitations.** `RegisterTest` appends to a plain `List<Func<TestResult>>` **without** the
`Sync` lock the fire dictionary uses (`:83-92`); `ResetTests` likewise clears unlocked
(`:97-106`).

**Self-test.** This is the self-test runner; it pins nothing itself. Individual guards pin
their reflected members and decision logic through it.

#### How "self-disabling" is actually enforced

1. **Fire tracking.** Every guard calls `SelfHealing.RecordFire(name)` each time it really
   suppresses a crash or corrects state; `GUARD ACTIVITY:` lists the counts. A guard that never
   fires across a session did nothing — evidence the upstream bug is gone and the guard can be
   retired (`Harness/SelfHealing.cs:9-14`, `:43-81`).
2. **Probes.** A *behaviour* patch — as opposed to a crash finalizer — would keep overriding
   upstream after upstream fixes the bug, silently reintroducing the wrong behaviour. Such a
   patch must test the bug signature first and stand down when it is gone; the named example is
   `ClientBootstrapFix` probing whether BT's action-cache mirrors are already primed
   (`Harness/SelfHealing.cs:15-21`). Register probes so the health report shows them.

### Self-documenting config with regex reader

**README item** n/a · **Source** `Harness/GuardConfig.cs` · **Class** `GuardConfig` · **Tag**
n/a · **Config** writes and reads every key · **Scope** both

**Bug.** Knobs are undiscoverable, and adding a JSON parser dependency to a Bannerlord module
is a binding risk.

**Mechanism.** `GuardConfig.Path` = `<moduleRoot>/guardconfig.json`, derived from the assembly
`Location` directory + `"../.."` (`:17-24`). On first read (latched by `_loaded`) it **writes**
a fully-documented `DefaultJson` if the file is missing, then caches the raw text for the
session; any failure yields an empty string (`:26-48`). `Bool(key, fallback)` matches
`"<key>"\s*:\s*(true|false)` ignoring case; `String(key, fallback)` matches
`"<key>"\s*:\s*"([^"]*)"` — both regex-escape the key and swallow exceptions to return the
fallback (`:50-80`). Every setting ships with a sibling `"_<key>"` documentation string in the
default file (`:82-115`), which is never read back.

**Limitations.** The text is cached for the whole session — editing `guardconfig.json` requires
a restart, it is **not** hot-reloaded (the one exception is `tracing`, which `PayloadEntry`
re-reads from disk). Regex matching is structure-blind: the first match anywhere in the file
wins. `String()` treats an explicit empty value as a **hit**, so the shipped
`"payloadSourceDir": ""` overrides the caller's fallback (`:70-73`, `:113` vs
`Harness/HotReload.cs:72`).

### Harness-owned cross-reload state bag

**README item** n/a · **Source** `Harness/SharedState.cs` · **Class** `SharedState` /
`ISharedState` · **Tag** n/a · **Config** none · **Scope** both

**Bug.** A payload reload creates a fresh assembly with fresh statics, so any state the payload
held is wiped — guard state, the launch session id, and `BattleMode`'s foreign-patch stash
would be lost on every generation swap.

**Mechanism.** `HotReload` holds `private readonly ISharedState _shared = new SharedState()`
created **once** (`Harness/HotReload.cs:36`) and passes the same instance into every
generation's `payload.Apply(harmony, _shared)` (`:367`). `SharedState` is a lock-protected
`Dictionary<string,object>` exposing `Get<T>`, `GetObject`, `Set`, `Has` (`:6-48`); `Get<T>`
silently returns `default(T)` when the key is missing **or** the stored value is not a `T`
(`:11-22`).

**Limitations.** `Get<T>`'s type-mismatch path is indistinguishable from "missing" — use
`Has`/`GetObject` when that matters.

### Thin lifecycle forwarder

**README item** n/a · **Source** `Harness/SubModule.cs` · **Class** `SubModule` · **Tag** n/a ·
**Config** none · **Scope** both

**Bug.** Anything living in the harness needs a full game restart to change, so logic placed
here would kill the hot-reload dev loop.

**Mechanism.** `SubModule : MBSubModuleBase` holds a single static `HotReload _engine` and does
nothing but call `base` then forward, each behind a null check: `OnSubModuleLoad` →
`Log.Info(Diag.Banner())` + `new HotReload()` + `Start()`;
`OnBeforeInitialModuleScreenSetAsRoot` → `OnBeforeInitialModuleScreen()`;
`OnGameStart(Game, IGameStarter)` → `OnGameStart()`; `OnMissionBehaviorInitialize(Mission)` →
`OnMissionInit()`; `OnApplicationTick(float dt)` → `Tick()` (`:12-59`). This is the only
assembly Bannerlord loads via `SubModule.xml` (`:6-11`).

### Public harness API instead of `InternalsVisibleTo`

**README item** n/a · **Source** `Harness/AssemblyInfo.cs` · **Class** n/a (assembly
attributes) · **Tag** n/a · **Config** none · **Scope** both

**Bug.** `InternalsVisibleTo` is matched by **exact** assembly name, but payload builds carry a
per-build stamped assembly name (`Payload.b<stamp>`) — so an `InternalsVisibleTo` entry could
never cover them and the payload would lose access to `Log`/`Diag`/`GuardConfig`/`SelfHealing`.

**Mechanism.** The harness API the payload uses (`Log`, `Diag`, `GuardConfig`, `SelfHealing`,
`IPayload`, `ISharedState`) is declared **public**; the
`[assembly: InternalsVisibleTo("BLTDeploymentCrashGuard.Payload")]` line is retained for the
fixed-name / Roslyn-compiled case (`:1-9`).

---

## Ops, build and install

These are not in-game guards. They are the mechanisms that get the right two DLLs onto a
player's machine, keep the version honest, and get evidence back off the machine. None of them
reads `guardconfig.json` at runtime except where noted.

### Locked-DLL in-place update (rename-aside `.prev`)

**README item** n/a · **Source** `install.cmd` · **Class** n/a (batch) · **Tag** n/a ·
**Config** none · **Scope** both (per-machine installer)

**Bug.** A player tries to update the mod while Bannerlord is running; the game holds the loaded
module DLLs open, so a plain overwrite or download fails and can leave a half-updated install —
harness new and payload old, or a zero-byte DLL.

**Mechanism.** Before downloading, for each of the two DLLs: delete any stale `<name>.prev`,
then `ren` the live file to `<name>.prev`. A rename is permitted on a file that is
locked-for-write, so the loaded DLL is moved aside and the fresh copy is `curl`'d in next to it
(`install.cmd:46-60`). The explicit comment at `install.cmd:49-50` states: "If the game is
running it locks the loaded DLLs; a rename is still allowed, so move the old files aside and
download the new ones next to them."

**Limitations.** `.prev` files accumulate in `bin/Win64_Shipping_Client` — only the immediately
previous one is pruned. The already-loaded old code keeps running until the game restarts, so an
update applied mid-session is not live. The `:fail` branch (`install.cmd:76-80`) still tells the
player to close the game and retry, because `curl` can fail for other reasons.

### Two-assembly install invariant

**README item** n/a · **Source** `install.cmd` · **Class** n/a (batch) · **Tag** n/a ·
**Config** none · **Scope** both

**Bug.** Since v1.2.0 the mod is **two** assemblies. Installing only
`BLTDeploymentCrashGuard.dll` — the file `SubModule.xml` names — gives a module that loads but
has no guards at all, every fix silently absent. `CHANGELOG.md` records the field version of
this: `dist/` still held the v1.1 monolithic DLL while the installer downloaded only the
harness, so anyone installing from the README one-liner got a build with no v1.2.x fix and no
payload.

**Mechanism.** The installer always fetches all three artifacts from the repo's `dist/` folder —
`SubModule.xml`, `BLTDeploymentCrashGuard.dll` (harness) and
`BLTDeploymentCrashGuard.Payload.dll` (payload) — each with `curl -fsSL ... || goto :fail`, so
any single failure aborts the whole install rather than leaving a mismatched pair
(`install.cmd:51-60`). The comment at `install.cmd:46-48` states that the harness is "the module
Bannerlord loads", the payload is "every guard/fix/tracer — the harness loads it", and "Both
must be installed together."

**Limitations.** No version or hash cross-check between the two downloaded DLLs — a
partially-updated `dist/` on GitHub would ship a mismatched harness/payload pair to every
player. The `dist/` listing shows exactly this drift risk (`BLTDeploymentCrashGuard.dll` dated
Sep 4 13:30 versus `BLTDeploymentCrashGuard.Payload.dll` Sep 4 15:07).

### Bannerlord install auto-detection

**README item** n/a · **Source** `install.cmd` · **Class** n/a (batch) · **Tag** n/a ·
**Config** `BANNERLORD_DIR` environment variable · **Scope** both

**Bug.** Players cannot reliably find their Bannerlord folder, and Steam libraries live on
arbitrary drives, so a hand-install lands the DLLs in the wrong place and the mod never appears
in the launcher.

**Mechanism.** Three-tier resolution: (1) `BANNERLORD_DIR` wins outright (`install.cmd:10-12`);
(2) otherwise scan an 11-entry hardcoded list of Steam layouts — `C:\Program Files (x86)\Steam\…`,
`C:\Program Files\Steam\…`, `C:\SteamLibrary\…`, and Steam/SteamLibrary variants on D:, E:, F:,
G: — taking the first whose `\Modules` subfolder exists (`install.cmd:14-28`); (3) prompt the
player to paste the path (`install.cmd:31-32`). Then strip embedded quotes
(`set "GAME=%GAME:\"=%"`, `:35`) and validate `%GAME%\Modules` exists or exit 1 (`:36-39`).

**Limitations.** Steam-only layouts; Epic, GOG, Xbox Game Pass installs and drives beyond G: are
never auto-found — `BANNERLORD_DIR` or the prompt is the only route. Detection accepts any
folder containing `Modules` as a valid Bannerlord install, with no `bin\Win64_Shipping_Client`
check, despite the prompt text asking for a folder that "contains bin\ and Modules\"
(`install.cmd:32`).

### Log-streaming opt-in (`BLTGUARD_BIN` → `logstream.txt`)

**README item** 26 · **Source** `install.cmd` · **Class** n/a (batch) · **Tag** n/a ·
**Config** writes the `logstream.txt` sidecar read by `Payload/LogStreamer.cs` · **Scope** both

**Bug.** A developer or support helper wants the mod's log streamed off-box, but there is no
in-game UI to configure it.

**Mechanism.** If `BLTGUARD_BIN` is set at install time, the installer echoes its value into
`<Mod>\logstream.txt` and prints "Log streaming enabled (bin `<BLTGUARD_BIN>`)." — the module
root file is the runtime switch the mod reads (`install.cmd:62-65`).

**Limitations.** The value is written verbatim with no validation. There is no way to disable it
again except deleting `logstream.txt` by hand; the installer never removes an existing
`logstream.txt` when `BLTGUARD_BIN` is unset.

### One-click log sharing

**README item** 26 · **Source** `share-log.cmd` · **Class** n/a (batch) · **Tag** n/a ·
**Config** none · **Scope** both

**Bug.** Getting a crash log from a non-technical co-op partner is friction: they cannot find
`CrashGuard.log`, and pasting a 10k-line file into Discord is useless.

**Mechanism.** Locate the game (the same Steam scan plus `BANNERLORD_DIR` override and prompt,
`share-log.cmd:10-34`), verify `<Mod>\CrashGuard.log` exists or exit 1 (`:35-39`), then POST it
to `litterbox.catbox.moe` with `reqtype=fileupload` and `time=24h` (`:45`); if the response does
not start with `https://`, retry against `https://0x0.st` with a plain `file=@` field
(`:48-51`). On success, read the URL out of the response file, pipe it to `clip` so it is on the
clipboard, and print it in a banner (`:59-71`). On double failure, print the absolute local path
and tell the player to send it directly (`:53-57`).

**Limitations.** 24-hour link expiry on the primary host. It uploads the log to a public
anonymous file host with no redaction — paths, save names and hero names become world-readable
to anyone with the link. Success is detected purely by `findstr /b "https://"`, so an HTML error
page beginning with a URL would be mistaken for a link.

### Full diagnostics bundle

**README item** 26 · **Source** `collect-diagnostics.cmd` · **Class** n/a (batch) · **Tag** n/a
· **Config** bundles `guardconfig.json` · **Scope** both — it collects the host/client/solo BT
sync logs by name, so one bundle identifies which co-op role the machine was playing

**Bug.** One log is never enough to diagnose a co-op crash: BannerlordTogether's own sync logs
and Bannerlord's crash report live in three different folders, and asking a player for each one
round-trips for days.

**Mechanism.** Stage into `%TEMP%\bltguard-diag`: `CrashGuard.log`, the rotated
`CrashGuard.log.1`, `guardconfig.json` (all from the module root), and BT's `bt-sync-host.txt` /
`bt-sync-client.txt` / `bt-sync-solo.txt` from `%USERPROFILE%\Desktop`
(`collect-diagnostics.cmd:33-38`); then pick the newest `*.html` in `%USERPROFILE%\Documents`
whose filename contains "crash" (`dir /b /o-d`, `findstr /i "crash"`) and copy it in as
`crashreport.html` (`:41-43`). `Compress-Archive` the stage to
`%TEMP%\bltguard-diagnostics.zip` (`:46`), upload with a 72 h litterbox link falling back to
`0x0.st` (`:52-55`), and put the URL on the clipboard (`:60-61`). Every copy is `>nul 2>&1` so a
missing file is skipped, not fatal.

**Limitations.** Depends on `powershell -NoProfile -Command Compress-Archive` (`:46`) — a
PowerShell dependency inside a player-facing `.cmd`; if PowerShell is blocked or restricted the
zip step silently fails and only the "files are staged in `%STAGE%`" error remains (`:47`). The
Steam auto-detect list here is **shorter** than `install.cmd`'s and `share-log.cmd`'s — only six
entries (`:13-20`), missing `D:\Steam`, `E:\Steam`, `F:\Steam` and every `G:` path — so a player
whose install `install.cmd` found automatically may still be prompted here. It `rmdir /s /q`s
the stage folder on every run (`:28`), destroying a prior bundle.

### Single-version-source enforcement (`StampSubModuleVersion`)

**README item** 25 · **Source** `Directory.Build.props` · **Class** n/a (MSBuild target) ·
**Tag** n/a · **Config** none · **Scope** n/a (build time)

**Bug.** The launcher-visible version in `SubModule.xml` drifts from the built assembly version,
so neither the player nor a log reader can tell which build is actually installed —
`SubModule.xml` had drifted to v1.0.0 while the assemblies carried a different version.

**Mechanism.** MSBuild target `StampSubModuleVersion`, `AfterTargets="Build"`, guarded by
`Condition="'$(MSBuildProjectName)' == 'BLTDeploymentCrashGuard'"` so it runs exactly once per
build (harness only, not repeated by the payload build). It uses
`<XmlPoke XmlInputPath="$(MSBuildThisFileDirectory)SubModule.xml" Query="/Module/Version/@value" Value="v$(Version)" />`
(`Directory.Build.props:12-19`). The single source is `<Version>1.3.2</Version>` (`:9`), from
which MSBuild also stamps both assemblies' `AssemblyVersion`/`FileVersion` and which `Diag`
reads back at runtime for the log banner (`:3-7`).

**Limitations.** It pokes only the repo-root `SubModule.xml` (`$(MSBuildThisFileDirectory)`) —
`dist/SubModule.xml` must be copied by hand as part of deploy. Nothing stamps the payload build
if the harness is not rebuilt.

**Self-test.** `Directory.Build.props:3-7` asserts the contract in prose: "THE single source of
truth for the mod version. Everything derives from it … Never write a version number anywhere
else."

### Unique per-build assembly name (`LoadFrom` dedup fix)

**README item** n/a · **Source** `Payload/BLTDeploymentCrashGuard.Payload.csproj` · **Class**
n/a (MSBuild) · **Tag** `[HOTRELOAD]` · **Config** `hotReload` · **Scope** n/a (dev build time)

**Bug.** A hot-reload appeared to succeed but the fix under test never ran: dropping a
freshly-built payload gave you back the already-loaded generation. Field-proven 2026-09-01
17:37, log line quoted in the comment: "LoadFrom deduped to already-loaded 1.2.7.42191". The
`LoadFrom` context dedups simple-named assemblies by **name only**, so the unique
`AssemblyVersion` revision added in v1.2.3 never mattered.

**Mechanism.** Stamp the assembly's internal name per build —
`<PayloadBuildStamp>$([System.DateTime]::UtcNow.ToString("yyMMddHHmmss"))</PayloadBuildStamp>`
and `<AssemblyName>BLTDeploymentCrashGuard.Payload.b$(PayloadBuildStamp)</AssemblyName>`
(csproj `:22-23`) — because "the LoadFrom context dedups simple-named assemblies by NAME ONLY …
A unique name per build is the only identity LoadFrom cannot collapse" (`:12-16`). Then the
`PublishFixedPayloadName` target copies `$(TargetPath)` to the fixed
`BLTDeploymentCrashGuard.Payload.dll` and deletes the stamped file "so bin/ holds exactly one
payload" (`:92-97`), because "csc names the assembly after its OUTPUT FILE, so the stamp must be
the compile-time output name" (`:19-21`).

**Limitations.** Nothing may depend on the internal assembly name; the comment enumerates why
that holds — "the harness finds PayloadEntry by type name, tests link source files,
SubModule.xml lists only the harness" (`:16-18`). A second build inside the same UTC **second**
would collide on the stamp. This change also required making the harness API public, since
`InternalsVisibleTo` cannot match a stamped name, and it requires one game restart (the loaded
harness must be 1.2.8+); every reload after that is clean.

### Apply-new-then-unpatch-old reload ordering

**README item** n/a · **Source** `HOTRELOAD.md` (mechanism implemented in
`Harness/HotReload.cs`) · **Class** `HotReload` · **Tag** `[HOTRELOAD]` · **Config**
`hotReload` · **Scope** both (dev only)

**Bug.** A failed hot-reload could leave the game with **no** guards patched at all — old
generation already unpatched, new one failed to apply — silently reintroducing every crash the
mod fixes, mid-session.

**Mechanism.** "Fresh statics and a per-generation Harmony owner id
(`bltogether.crashguard.gen{N}`); the new generation is applied first, then the previous
generation is `UnpatchAll`'d — a failed reload keeps the previous generation, so the game is
never left unpatched" (`HOTRELOAD.md:10-13`). Success is visible in the log as
`[HOTRELOAD] gen2 applied (reload), unpatched …gen1` and the engine reloads within about 400 ms
of the DLL landing (`HOTRELOAD.md:34`).

**Limitations.** Both generations are briefly patched simultaneously — a double-patch window
between apply and unpatch. Old assemblies cannot unload on .NET Framework, so roughly 1–3 MB
leaks per reload (`HOTRELOAD.md:63`).

### Two-condition hot-reload gate

**README item** n/a · **Source** `HOTRELOAD.md`, `Harness/HotReload.cs:70-72` · **Class**
`HotReload` · **Tag** `[HOTRELOAD]` · **Config** `hotReload` · **Scope** both (dev only)

**Bug.** Shipping a runtime code-loading path — `Assembly.LoadFrom` of a watched file, or a live
Roslyn compiler — to players is a stability and security hazard: a dropped DLL would execute
in-process.

**Mechanism.** Hot-reload requires **both** `"hotReload": true` in `guardconfig.json` **and** an
empty marker file `.hotreload-dev` in the module root
(`Modules/BLTDeploymentCrashGuard/.hotreload-dev`). "Both conditions are required — this makes
runtime code loading impossible on a normal player install" (`HOTRELOAD.md:15-21`). The section
header is explicit: "Enabling hot-reload (dev only — never ship this on)".

### Roslyn edit-`.cs` auto-reload (mode B)

**README item** n/a · **Source** `HOTRELOAD.md` (implementation in
`Harness/PayloadCompiler.cs`) · **Class** `PayloadCompiler` · **Tag** `[HOTRELOAD]` ·
**Config** `hotReloadRoslyn`, `payloadSourceDir` · **Scope** both (dev only)

**Mechanism.** Compile Roslyn support into the harness only under `-p:Roslyn=true` (harness
csproj `:17-19` sets `DefineConstants ROSLYN`; `:25-27` adds `Microsoft.CodeAnalysis.CSharp
4.8.0` conditionally), set `"hotReloadRoslyn": true`, and point `"payloadSourceDir"` at the repo
`Payload/` folder; then "editing any `Payload/*.cs` triggers a runtime Roslyn recompile +
reload — no `dotnet build`" (`HOTRELOAD.md:36-45`). On compile failure "the engine logs it and
falls back to the prebuilt DLL" (`:47-48`).

**Limitations.** "Roslyn on .NET Framework 4.8 inside Bannerlord can bind-conflict with
ButterLib's older `System.Collections.Immutable` / `System.Reflection.Metadata`"
(`HOTRELOAD.md:46-47`). Mode (A) build-and-drop is described as "default, bulletproof, zero
extra deps" (`:24`) and mode (B) as "slicker, fragile on net472" (`:36`).

### Battle-mode stash: documented reload gap

**README item** 14 · **Source** `HOTRELOAD.md:65-68` · **Class** `BattleMode` · **Tag**
`[BATTLE-MODE]` · **Config** `battleMode` · **Scope** solo (`battleMode=coop` lifts nothing and
is unaffected)

**Known gap (Phase B).** "`BattleMode`'s foreign-patch stash does not yet survive a reload …
reloading while in `battleMode=solo` (vanilla, BT battle patches lifted) can leave them lifted.
Reloading in `battleMode=coop` is unaffected (nothing is lifted). Restart if battle mode
misbehaves after a reload."

### IL-probe root-cause method (`MovementOrder` type initializer)

**README item** n/a · **Source** `tools/il-probes/README.md` · **Class** n/a (tooling) ·
**Tag** n/a · **Config** none · **Scope** both

**Mechanism.** The `MovementOrder` type-initializer crash was root-caused with two `IlDump` runs
and one reflection check, no decompiler:
`IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.cctor"`
shows it "builds six defaults via `newobj MovementOrder::.ctor`";
`IlDump.exe … "MovementOrder::.ctor"` shows "the one null-capable line:
`call Mission::get_Current; callvirt Mission::get_CurrentTime`"; plus "a reflection check that
`MovementOrder` is a `beforefieldinit` value type" — so the cctor fires lazily at an
unpredictable first touch (`tools/il-probes/README.md:34-44`).

**Limitations.** Because the resulting guard is a load-time fix that must run before the game
touches the type, it cannot be hot-reloaded — it needs a fresh game launch.

### Pinned package sources

**README item** n/a · **Source** `NuGet.config` · **Class** n/a · **Tag** n/a · **Config** none
· **Scope** n/a (build time)

**Bug.** A machine-level or user-level `NuGet.config` feed (corporate, private, offline)
silently changes what the build resolves, or breaks the build entirely on someone else's box.

**Mechanism.** `<packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>`
— the `<clear />` discards all inherited sources so exactly one feed is in play
(`NuGet.config:3-6`).

**Limitations.** No `packageSourceMapping` and no lock file — versions are pinned only by the
two `PackageReference` declarations (`Microsoft.NETFramework.ReferenceAssemblies` 1.0.3,
`Microsoft.CodeAnalysis.CSharp` 4.8.0).

### Module manifest: dependency ordering and optional BT

**README item** n/a · **Source** `SubModule.xml` · **Class** `BLTDeploymentCrashGuard.SubModule`
· **Tag** n/a · **Config** none · **Scope** both

**Bug.** If the guard loads before `Bannerlord.Harmony` there is no Harmony to patch with; if it
hard-depends on BannerlordTogether it refuses to load for players who do not have BT; if it
declares itself a multiplayer module it will not appear where BT co-op is actually launched
from.

**Mechanism.** `<DependedModules>`: `Bannerlord.Harmony`, `Native`, `SandBoxCore`, `Sandbox` all
at `DependentVersion v1.4.8`, plus `<DependedModule Id="BannerlordTogether" Optional="true" />`
(`SubModule.xml:8-14`). `SingleplayerModule=true`, `MultiplayerModule=false`,
`IsTWCompatible=false` (`:5-7`). One SubModule entry: name `BLTDeploymentCrashGuard`, DLLName
`BLTDeploymentCrashGuard.dll`, `SubModuleClassType BLTDeploymentCrashGuard.SubModule`, with an
empty `<Assemblies />` (`:15-22`).

**Limitations.** `Optional="true"` does **not** guarantee load order after BT — `install.cmd:70-71`
has to tell the player to tick the mod "in the Singleplayer mods list, AFTER BannerlordTogether"
by hand. `<Assemblies />` is empty, so the payload DLL is invisible to the launcher and must be
loaded by the harness itself.

### `dist/` is tracked on purpose

**README item** n/a · **Source** `.gitignore` · **Class** n/a · **Tag** n/a · **Config** none ·
**Scope** n/a (release)

**Bug.** A conventional `.gitignore` would ignore build output, but `install.cmd` downloads the
shipped binaries straight out of the repo, so ignoring them would break every install.

**Mechanism.** `.gitignore` contains only `bin/`, `obj/`, `.runner/` (`:1-3`) — `dist/` is
deliberately absent, so the three shipped artifacts are committed. `install.cmd:9` sets
`REPO=https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main` and `:58-60` fetch
`%REPO%/dist/SubModule.xml`, `%REPO%/dist/BLTDeploymentCrashGuard.dll` and
`%REPO%/dist/BLTDeploymentCrashGuard.Payload.dll`.

**Limitations.** There is no staging or tag gate — any push to `main` that touches `dist/` is
immediately live to every player running `install.cmd`. Binary churn accumulates in git history
(`dist/BLTDeploymentCrashGuard.Payload.dll` is 191,488 bytes, the harness 40,960 bytes).

---

## Indexes

Built after all areas are documented — see the end of this file.
