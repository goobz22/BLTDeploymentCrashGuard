# Engine notes (hard-won facts)

Facts about Mount & Blade II: Bannerlord — and, where they are inseparable, about BannerlordTogether
(BT) — proven during investigations, so we never re-derive them. Each entry says **what is true**,
**how it was proven** (a repo-relative `file:line` and/or the IL member names), **which game members
are involved**, and **the date** it was proven where that is known.

How the facts here were established, in rough order of how often each was used:

- **IL of the installed build**, read with the probes in `tools/il-probes/` (`NameSearch`, `Inspect`,
  `IlDump`, `Callers`, `VerCheck`) — no decompiler. This is what "IL-proven" means below.
- **Live log-only Harmony tracers** on native methods (`Payload/TracePatches.cs`,
  `Payload/ControlTrace.cs`, `Payload/TimeTrace.cs`, `Payload/RoleTrace.cs`,
  `Payload/CharacterCreationTrace.cs`) reproducing the case with `"tracing": true`.
- **Reflection probes** at runtime (`Payload/MovementOrderInitProbe.cs`,
  `Payload/RuntimeDiagnostics.cs`) and the session-wide first-chance exception capture.
- **Self-tests** that pin a member or an enum value so a game update breaks loudly
  (`SelfHealing.RegisterTest`); several numbers below are pinned that way.

**Adding an entry.** Put it in the subsystem section it belongs to, in this shape: a precise
statement, the evidence (`file:line`, IL member names, or the trace that captured it), the game
members involved, and the date. One fact, one home — if a fact already lives in a section, extend
that entry rather than adding a second copy elsewhere; cross-reference instead. Related documents:
`docs/DIAGNOSTICS.md` (how to investigate), `docs/BT-INTERNALS.md` (BT internals),
`docs/FIX-REFERENCE.md` (which fix consumes which fact), `docs/MODDING-PITFALLS.md` (what bit us).

**Verified environment for every finding below** (`UPSTREAM_BUG_REPORT.md:5-6,47-48`): Bannerlord
v1.4.8 (build 1.4.8.119303, Steam), BLSE 1.6.7.356, LauncherEx 1.25.6, Harmony 2.3.6.220,
ButterLib 2.11.1.0, BannerlordTogether v0.5.0.1 (commit `035beead876d66fb1e91d7282cd98bc4f624430b`),
installed via Vortex/Nexus.

---

## Contents

1. [Mission lifecycle and states](#1-mission-lifecycle-and-states)
2. [Teams, formations, orders and formation AI](#2-teams-formations-orders-and-formation-ai)
3. [Siege](#3-siege)
4. [Campaign time control](#4-campaign-time-control)
5. [Encounters, map events and incidents](#5-encounters-map-events-and-incidents)
6. [Heroes, clans, marriage, pregnancy, succession](#6-heroes-clans-marriage-pregnancy-succession)
7. [Settlements, stash, inventory](#7-settlements-stash-inventory)
8. [Party AI and behaviours](#8-party-ai-and-behaviours)
9. [UI / Gauntlet / screens / character creation](#9-ui--gauntlet--screens--character-creation)
10. [Agents and visuals](#10-agents-and-visuals)
11. [Save/load](#11-saveload)
12. [Misc — module layout, assembly binding, reflection, environment](#12-misc--module-layout-assembly-binding-reflection-environment)

---

## 1. Mission lifecycle and states

### Mission load order (2026-09-04)

`MissionState.FinishMissionLoading` calls, in order: `Mission.Tick` → `OnMissionAfterStarting` →
`Mission.AfterStart`. `Mission._current` is set by `Mission.Initialize` (via `Mission.set_Current`),
which runs earlier in `MissionState.OpenNew`. So by the time `AfterStart` adds teams
(`MissionCombatantsLogic.AddPlayerTeam` → `Team.Initialize` → `Formation.Reset` →
`Formation.ResetAux`), `Mission.Current` is already live. This is why a type-init throw logged at
`ResetAux` with `Mission=live` is a cached re-throw, not the origin — see
[MovementOrder](#movementorder-is-a-beforefieldinit-struct-whose-init-needs-a-live-mission-2026-09-04).

Evidence: `Payload/MovementOrderInitProbe.cs:12-16`.
Members: `MissionState.FinishMissionLoading`, `MissionState.OpenNew`, `Mission.Initialize`,
`Mission.set_Current`, `Mission._current`, `Mission.AfterStart`, `Formation.ResetAux`.

### `MissionState.OpenNew` is the single mission chokepoint

Every 3D mission launch passes `MissionState.OpenNew`, whoever initiated it — patching it catches
them all, and it is the **last point before the mission is actually built**. The campaign→mission
transition has three chokepoints in order: the settlement/party encounter,
`PlayerEncounter.StartBattle`, then `MissionState.OpenNew`.

Evidence: `Payload/TracePatches.cs:23-24,:37,:86-91,:179-189` ("Last-chance mode decision before the
mission is built"); `Payload/BattleMode.cs:24-26`.
Members: `TaleWorlds.MountAndBlade.MissionState.OpenNew`,
`TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.StartBattle`.

### `Mission.Current`, `Mission.Scene` and the state getters

- `Mission.Current` **can legitimately be null** at module-load / patch-application time. Load-time
  code must not assume a mission exists (`Payload/PayloadEntry.cs:40-41`).
- `Mission.Current != null` is the reliable "we are inside a mission, not on the campaign map" test —
  used both to defer campaign-level identity changes and to refuse pushing a campaign screen
  (`Payload/CoopHeroIdentityLock.cs:78-81`; `Payload/ClanPartyCreationAdvisor.cs:235-238`).
- `Mission.Scene` can still be **null while `Mission.Current` is already non-null**. A mission is not
  usable until `Scene` exists (`Payload/PlayerIdentityGuard.cs:45-48`).
- `Mission.Mode`, `Mission.CurrentState` and `Mission.Scene` can each **throw** during a
  mission/scene transition, not merely return null — every read needs its own try/catch, degrading to
  `?` / `threw:<ExceptionType>`. A state dump that reads them unguarded becomes the crash it was
  written to explain (`Payload/RuntimeDiagnostics.cs:95-126`, per-property try/catch at `:117-119`;
  the generic `SafeGet<T>` at `Payload/ControlTrace.cs:299-345`).
- The active game state is `GameStateManager.Current.ActiveState`; **`GameStateManager.Current` can
  be null and `ActiveState` can be null** ("no active state") (`Payload/RuntimeDiagnostics.cs:128-144`).
- `Campaign.Current` is null outside a campaign and is the cheapest "are we in a campaign" probe
  (`Payload/RuntimeDiagnostics.cs:146-157`; the same bail-out at
  `Payload/PregnancySync/PregnancySyncGuard.cs:350`, `Payload/StashSync/StashSyncGuard.cs:304`).
- `Mission.get_CurrentTime` just returns the cached field `_cachedMissionTime` — a cheap read, and the
  reason the `MovementOrder` NRE is the `callvirt` on a null `Mission.Current` rather than anything
  inside the getter.

Members: `Mission.Current`, `Mission.Scene`, `Mission.Mode`, `Mission.CurrentState`,
`Mission.get_CurrentTime`, `Mission._cachedMissionTime`, `TaleWorlds.Core.GameStateManager.Current`,
`GameStateManager.ActiveState`, `TaleWorlds.CampaignSystem.Campaign.Current`.

### Deployment: `Mission.InitialPlayerAgent` and the `FinishDeployment` tail (2026-08-18 – 2026-08-20)

- **An exception escaping `DeploymentMissionController.SetupTeams()` is an unconditional
  crash-to-desktop** — it unwinds through `Mission.OnTick` into the native engine, where there is no
  managed catch (`Payload/DeploymentCrashGuards.cs:8-11`).
- Native `SetupTeams()` **dereferences `Mission.InitialPlayerAgent` with no null check**.
  `Mission._initialPlayerAgent` is assigned **only** when an agent is *built* with
  `Controller == AgentControllerType.Player` — i.e. the player-side spawn during team setup must
  produce the player agent. Vanilla can never hit the null, because the native spawn path always
  creates the player agent during `OnSetupTeamsOfSide(PlayerSide)`
  (`UPSTREAM_BUG_REPORT.md:60-70`). Battle setup can therefore NRE in `SetupTeams` and produce empty
  formations when the player side has been stripped out of the mission by another mod's patches
  (`Payload/BattleMode.cs:16-19`).
- `DeploymentMissionController.OnMissionTick` is what reaches `SetupTeams` — the crash stack reads
  `SetupTeams -> OnMissionTick_Patch1` (`UPSTREAM_BUG_REPORT.md:56-62`).
- `DeploymentMissionController.FinishDeployment` **also** dereferences `Mission.InitialPlayerAgent`,
  and the field is **re-nulled if the player agent is ever removed** — the null is not confined to
  mission start. Guard the dereference, not the moment (`Payload/DeploymentCrashGuards.cs:29-32`).
- `FinishDeployment`'s tail, in the exact order a recovery must replay it or the battle stays frozen
  (`Payload/DeploymentCrashGuards.cs:55-71`):
  `player.SetDetachableFromFormation(true)` → `player.Controller = AgentControllerType.Player` →
  `mission.AllowAiTicking = true` → `mission.DisableDying = false` →
  `mission.SetFallAvoidSystemActive(false)` → `mission.OnAfterDeploymentFinished()` → the non-public
  `DeploymentMissionController.AfterDeploymentFinished()` → `mission.RemoveMissionBehavior(controller)`.
- `DeploymentMissionController` is a `MissionBehavior` (it is removed with
  `Mission.RemoveMissionBehavior`) and exposes a `Mission` property on the instance
  (`Payload/DeploymentCrashGuards.cs:47,:70`).
- **While a `DeploymentMissionController` behavior is on the mission, `Controller = None` on the
  player agent is legitimate** — any identity/control corrector must skip the deployment phase
  (`Payload/PlayerIdentityGuard.cs:58-61`, via `Mission.GetMissionBehavior<DeploymentMissionController>()`).
- `Mission.OnDeploymentFinished` is a real, patchable method and the correct moment to snapshot the
  finished control map (`Payload/ControlTrace.cs:25,:38,:227-231`).

### Battle-open call order, captured live (2026-08-18)

A log-only tracer on native methods captured:
`GameMenu.SwitchToMenu(village_hostile_action)` → `GameMenu.SwitchToMenu(encounter)` +
`PlayerEncounter.StartBattle` → `MissionState.OpenNew("Battle", …)` → mission scene ready.
`Mission.InitialPlayerAgent` was **still null at scene-ready and never populated** — an external
guard held team setup 90 s to prove it (`UPSTREAM_BUG_REPORT.md:78-84`).

Two symptoms worth recognising, both from the same run (`UPSTREAM_BUG_REPORT.md:85-93`): the
order-of-battle screen showed every player formation as `0/0` / "Formation is currently empty", while
the **map-event side of the raid kept running** (loot ticks, "You plundered …"). The map-event layer
runs independently of the mission roster, so that combination proves the failure is mission-side
rostering only.

### The battle-takeover surface

The native members a co-op mod must hook to take a battle over — and therefore the exact set this
mod lifts in vanilla mode (`Payload/BattleMode.cs:39-63`):

| Namespace | Members |
|---|---|
| `TaleWorlds.CampaignSystem.GameComponents` | `DefaultTroopSupplierProbabilityModel.EnqueueTroopSpawnProbabilitiesAccordingToUnitSpawnPrioritization` |
| `TaleWorlds.CampaignSystem.MapEvents` | `MapEventSide.{MakeReadyForMission, OnTroopKilled, OnTroopWounded, OnTroopScoreHit}` |
| `TaleWorlds.CampaignSystem.CampaignBehaviors` | `OrderOfBattleCampaignBehavior.{GetFormationDataAtIndex, SetFormationInfos}` |
| `TaleWorlds.MountAndBlade` | `DefaultBattleMissionAgentSpawnLogic.OnSideDeploymentOver`; `DeploymentMissionController.{OnMissionTick, FinishDeployment, SetupAIOfEnemyTeam}`; `BattleEndLogic.{MissionEnded, OnAgentRemoved}`; `BattleObserverMissionLogic.OnAgentRemoved` |
| `TaleWorlds.MountAndBlade.ComponentInterfaces` | `BattleInitializationModel.CanPlayerSideDeployWithOrderOfBattle` (abstract; concrete override in `SandboxBattleInitializationModel`) |
| `TaleWorlds.MountAndBlade.ViewModelCollection.OrderOfBattle` | `OrderOfBattleVM.{Initialize, ExecuteBeginMission, OnDeploymentFinalized, RefreshValues}` |
| `SandBox.GameComponents` | `SandboxBattleInitializationModel.GetAllAvailableTroopTypes` |
| `SandBox.Missions.MissionLogics` | `BattleAgentLogic.{OnAgentBuild, CheckUpgrade, OnAgentHit, OnAgentRemoved}` |

The namespace split is real and matters for `AccessTools.TypeByName` lookups — see
[reflection notes](#enumerating-patch-targets-by-name).

### `Mission.SpawnTroop` has multiple overloads

`Mission.SpawnTroop` has several declared overloads returning `Agent` (public and non-public
instance methods declared on `Mission` itself). Enumerate and patch all of them rather than naming a
signature (`Payload/CoopCommandSplit.cs:79-87,:423-429`).

### Hideout "Sneak in" is the stealth ambush mission (IL-proven, 2026-09-01)

`SandBox.Missions.MissionLogics.Hideout.HideoutAmbushMissionController.AfterStart` spawns **your**
hero and then re-dresses it in `Hero.StealthEquipment` with the **enemy's** clothing colours via
`UpdateSpawnEquipmentAndRefreshVisuals`. The "soldier" appearance is a disguise, not a wrong agent —
`Agent.Main` is still the player's hero (confirmed by the control trace).

The mission **starts in stealth mode** with a "locate the main camp" objective; troops are held back
and orders are withheld **by design**, and being spotted too long fails the counter and ends the
mission ("found by sentries"). Orders and the player's squad arrive only at the stealth→battle
transition, which is also where the player order controller is selected — the three transition
methods are `ChangeHideoutMissionModeToBattle`, `StartBossFightBattleModeInternal` and
`StartBossFightDuelModeInternal`.

Vanilla only *assumes* the local player is the team general and owns that controller — see
[team command links are settable](#reading-and-repairing-the-battle-control-map). Note for triage:
this and [pregnancy](#pregnancy-il-proven-2026-08-30) were both field reports that turned out to be
UX gaps rather than bugs; the correct ship was an explainer plus a guarantee, not a behaviour change.

Evidence: `Payload/StealthHideoutAdvisor.cs:9-21,:31,:43,:49`; `CHANGELOG.md:106-118`.

### Module/game lifecycle the mod hangs on

Module screen (`OnBeforeInitialModuleScreenSetAsRoot`) → game start (`OnGameStart`) → per-mission
init (`OnMissionBehaviorInitialize` / `OnMissionInit`) → per-frame `OnApplicationTick`
(`Payload/PayloadEntry.cs:115-158`; `Harness/SubModule.cs:16-58`). Consequences and the
`MBSubModuleBase` signatures are in [Misc](#module-lifecycle-and-load-order).

---

## 2. Teams, formations, orders and formation AI

### `MovementOrder` is a `beforefieldinit` struct whose init needs a live Mission (2026-09-04)

`TaleWorlds.MountAndBlade.MovementOrder`:

- is a **struct** (`IsValueType == true`, base `System.ValueType`) and **`beforefieldinit`**;
- its `.cctor` builds six template orders (`MovementOrderNull/Charge/Retreat/Stop/Advance/FallBack`)
  by calling `MovementOrder..ctor(MovementOrder.MovementOrderEnum)` — `newobj MovementOrder::.ctor`
  six times, so merely touching the type runs the instance constructor six times;
- that instance ctor's **one** null-capable line is `Mission.Current.CurrentTime`
  (`call Mission::get_Current; callvirt Mission::get_CurrentTime`, netting one `float`).
  `Mission.get_CurrentTime` just returns the cached field `_cachedMissionTime`, so the NRE is the
  `callvirt` on a null `Mission.Current`.

Consequence: because the type is `beforefieldinit`, the CLR may run the `.cctor` at **any** point
before first static-field access — including early type preparation triggered by JIT-compiling or
**Harmony-patching a method that merely references the type** (`Formation`, `OrderController`). That
is why the crash appears at unpredictable moments rather than at a fixed call site. If it happens
before a mission exists, `Mission.Current` is null → NRE → the type initializer fails → .NET
**permanently caches the failure**: every later access re-throws the *original* exception (with its
original inner and stack) without re-running the ctor, so every battle for the rest of the process
dies at `Formation.ResetAux` with a `TypeInitializationException`.

This mod's Formation/OrderController patches (added v1.3.0) are what caused the early preparation.

**Fix** (`Payload/MovementOrderTypeInitGuard.cs`, applied *first* in `PayloadEntry.Apply`): a
transpiler rewrites the `Mission.Current.CurrentTime` read to a null-safe helper (returns 0 when no
mission), then forces the `.cctor` to run immediately under the safe ctor, so the type initializes
successfully and is cached good for the whole process. Giving the six templates `gameTime 0` is
behaviourally safe — they are singletons whose tick timer is irrelevant, and real orders built during
gameplay always have a live mission and get the true time.

Reusable class: *a `beforefieldinit` type whose static init depends on runtime state can be poisoned
by our own patching; initialize it safely at load.*

Evidence: `Payload/MovementOrderTypeInitGuard.cs:14-32,:50,:85-104,:115-138`;
`Payload/MovementOrderInitProbe.cs:12-17`; `Payload/PayloadEntry.cs:38-42`;
`tools/il-probes/README.md:37-43`.
Members: `MovementOrder`, `MovementOrder..ctor(MovementOrderEnum)`, `MovementOrder.MovementOrderEnum`,
`Mission::get_Current`, `Mission::get_CurrentTime`, `Formation`, `OrderController`,
`Formation.ResetAux`, `System.TypeInitializationException`.

### Reading and repairing the battle control map

Without a decompiler, the complete picture is reachable through these members
(`Payload/ControlTrace.cs:234-297,:299-333`; `Payload/PlayerIdentityGuard.cs:105-135,:155`):

| Level | Members |
|---|---|
| Mission | `Current`, `Teams`, `MainAgent` (settable — the local "player" agent), `InitialPlayerAgent`, `PlayerTeam` |
| Team | `Side` (its human-readable identity), `GeneralAgent` (settable), `PlayerOrderController` (an `OrderController` whose `Owner` is a settable `Agent`), `FormationsIncludingSpecialAndEmpty` (the complete list, special and empty included), `FormationsIncludingEmpty`, `IsPlayerGeneral`, `AssignPlayerAsSergeantOfFormation` |
| Formation | `FormationIndex`, `CountOfUnits`, `PlayerOwner`, `Captain`, `IsAIControlled`, `ApplyActionOnEachUnit(Action<Agent>)` |
| Agent | `Character` (whose `Name` may be null), `Index`, `Team` (may be null — fall back to `Mission.PlayerTeam`), `IsMainAgent` |

`Team.Side` is a `BattleSideEnum` (`Payload/SiegeCommandGuard.cs:217,:226,:409,:416`;
`Payload/CoopCommandSplit.cs:240`).

**Vanilla only assumes the local player holds `Team.GeneralAgent` and
`Team.PlayerOrderController.Owner`** — both are settable, so both links can be asserted and repaired
(`Payload/StealthHideoutAdvisor.cs:85-103`). Note `Agent.IsActive` is a **method**, not a property;
treating it as a property is a silent reflection failure that quietly disables the repair.

### Mutators that move command around

- `Formation.set_PlayerOwner(v)` ⇒ `SetControlledByAI(v == null)`. Reassigning `PlayerOwner` also
  flips AI control of that formation (`Payload/PlayerIdentityGuard.cs:127-133`).
- `Formation.SetControlledByAI(bool isControlledByAI)` **early-returns when the value is unchanged**,
  so only real flips do anything and a tracer must compare against `IsAIControlled` to log anything
  meaningful (`Payload/ControlTrace.cs:139-154`). There is also a two-bool overload
  `SetControlledByAI(bool, bool)`; the second argument is passed `false` when a guard takes a
  formation back (`Payload/SiegeCommandGuard.cs:93,:428`).
- `Team.SetPlayerRole(bool isPlayerGeneral, bool isPlayerSergeant)` — the player's battle role is
  that **pair of booleans, not an enum** — and it sets every formation
  `SetControlledByAI(!IsPlayerGeneral)`, i.e. it hands **every** formation to the AI when the player
  is not the general (`Payload/ControlTrace.cs:42,:156-165`; `Payload/SiegeCommandGuard.cs:34,:95`).
- `Team.DelegateCommandToAI()` hands **every** formation of that team to the AI in one call — the
  single-call route by which a player loses command, and vanilla's on-death hand-off
  (`Payload/ControlTrace.cs:43,:167-176`; `Payload/SiegeCommandGuard.cs:46,:63-64,:96`).
- **F6 "delegate command" is literally `OrderController.SetOrder(OrderType.AIControlOn)`**, a
  different path from `Team.DelegateCommandToAI` — both must keep working when suppressing AI
  hand-offs (`CHANGELOG.md:73-75`).
- `OrderController.BeforeSetOrder` returns a formation to the player **only when it is AI-controlled
  AND has a `PlayerOwner`** — both conditions, not either (`Payload/SiegeCommandGuard.cs:31-32`;
  `CHANGELOG.md:63-64`).
- `Formation.RemoveUnit` hands an **emptied** formation back to the AI, so a formation that is later
  refilled (reinforcements) is the AI's again. A one-time hand-off to the player decays as troops die
  (`Payload/SiegeCommandGuard.cs:32-33`; `CHANGELOG.md:64-65`).
- `Formation.TransferUnits(Formation target, int unitCount)` is the **tactic-only** API — the order UI
  transfers troops through `OrderController.TransferUnits` instead, so patching the former does not
  take re-organization away from the player (`Payload/SiegeCommandGuard.cs:48-49,:94`;
  `Payload/ControlTrace.cs:39-40,:44,:178-189`).
- `FormationAI.TickOccasionally` runs its behaviours **only while the formation `IsAIControlled`** —
  removing AI control is what actually stops tactic behaviour, not cancelling an order
  (`Payload/SiegeCommandGuard.cs:26-27`; `CHANGELOG.md:59-60`).
- `(int)OrderType.AIControlOn == 36` in the installed build; the self-test asserts it so a game update
  that reshuffles the enum is caught (`Payload/SiegeCommandGuard.cs:534`).

### Formation classes and indices

- `FormationClass` indices **0–7 are the regular formations**, 8 is general and 9 is bodyguard;
  `FormationClass.NumberOfRegularFormations` is the count of regular ones, and the siege guard's
  self-test pins that indices 8 and 9 are never guarded (`Payload/SiegeCommandGuard.cs:59,:274,:544-545`).
- `Formation.FormationIndex` is **`FormationClass`-typed** — cast to `int` for index arithmetic and
  back for `Team.GetFormation(FormationClass)` (`Payload/SiegeCommandGuard.cs:227,:294,:417`;
  `Payload/CoopCommandSplit.cs:284,:289-290`).
- The four basic classes, in order, are infantry / archers / cavalry / horse archers, occupying
  formations I–IV; V–VIII repeat the same order (`CHANGELOG.md:40-42`).
- `FormationClass` folds into those four roles: `Ranged` → archers;
  `Cavalry` / `LightCavalry` / `HeavyCavalry` → cavalry; `HorseArcher` → horse archers; everything
  else (including `Infantry`, `Skirmisher`, `HeavyInfantry`) → infantry
  (`Payload/CoopCommandSplit.cs:151-168`, verified in the self-test at `:431-436`).
- A troop's intended class is `CharacterObject.DefaultFormationClass`; if the character is null, fall
  back to the agent's current `Formation.FormationIndex` (`Payload/CoopCommandSplit.cs:289`).
- `Agent.Formation` is a **settable** property, so an agent can be re-assigned to another formation
  directly; an agent's owning party is `Agent.Origin` cast to
  `TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin`, then `.Party` (a `PartyBase`)
  (`Payload/CoopCommandSplit.cs:277-278,:295,:420-421`).

### Placing a formation and making it hold

`Formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache)` returns the
formation's order position; `WorldPosition.IsValid` says whether it is usable; and
`MovementOrder.MovementOrderMove(WorldPosition)` builds the hold-here order passed to
`Formation.SetMovementOrder` (`Payload/SiegeCommandGuard.cs:427-432,:532-533`).

### Co-op: vanilla mixes both parties into the same formations

Vanilla spawns **both** parties' troops into the same class formations, so in co-op every formation
is mixed host/client troops (`Payload/CoopCommandSplit.cs:29-31`; `CHANGELOG.md:36-38`). Worse, the
**Order of Battle screen re-sorts formations by class and reinforcements arrive later**, so any custom
formation assignment must be re-applied after deployment and on a timer (here: every half second)
(`Payload/CoopCommandSplit.cs:39-40`; `CHANGELOG.md:42-44`).

Identity handles used to decide whose troop is whose: `Agent.Main` / `Agent.IsPlayerControlled` for
the local player's agent, `Hero.MainHero` for the local hero, and `Hero.StringId` /
`CharacterObject.StringId` as identity keys for a remote player's hero
(`Payload/CoopCommandSplit.cs:273,:299-318`). A hero is looked up by id through
`MBObjectManager.Instance.GetObject<Hero>(id)`, falling back to
`GetObject<CharacterObject>(id).HeroObject` — the id may name either object type — then
`Hero.PartyBelongedTo` gives the `MobileParty` and `MobileParty.Party` the `PartyBase`
(`Payload/CoopCommandSplit.cs:356-363`).

### BT command model in battle (IL-proven, 2026-09-03)

Host approves a formation for the client only when it holds the client's troops alone
(`IsClientFormationCommandApproved`); approved formations form the client's `AllowedFormationMask`;
the client reports its troops' formations once a second (`SendFormationMembershipSnapshot`) and the
host mirrors them (`ApplyClientFormationMembership` → `ResolveFormationByClass`). Vanilla mixes both
parties' troops by class, so the mask is empty and the client commands nothing. `CoopCommandSplit`
folds host party into formations I–IV and client into V–VIII so each block is pure. Remote player
hero id via BT's session ghost-hero string id. Further BT internals: `docs/BT-INTERNALS.md`.

---

## 3. Siege

### Siege defense: vanilla's default is AI control ON (IL-proven, 2026-09-03)

`BattleDeploymentHandler.SetDefaultFormationOrders` ends with
`SetOrder(IsSiegeBattle || IsSallyOutBattle ? AIControlOn : AIControlOff)`, so in a siege battle
vanilla's default for **every** formation is AI control on. It runs from the player side's auto-deploy
path — `MissionOrderDeploymentControllerVM.DeployFormationsOfPlayer` →
`SiegeDeploymentHandler.AutoDeployTeamUsingTeamAI` — and from the Auto-deploy button.

An AI-controlled formation in a castle defense belongs to **`TacticDefendCastle`**: the tactic assigns
lanes and key positions (walls, gate, keep), **re-plans on a breach** ("retreat to keep", "defend key
position") — exactly when the player's troops abandon their placed spot — and re-balances troops
between formations through `Formation.TransferUnits` / `Formation.Split`.

The rest of the hand-off machinery (`FormationAI.TickOccasionally`, `OrderController.BeforeSetOrder`,
`Formation.RemoveUnit`, `Team.SetPlayerRole`, F6) is in
[§2 Mutators that move command around](#mutators-that-move-command-around). `SiegeCommandGuard`
counters these so placed formations hold; detail in that file's header.

Evidence: `Payload/SiegeCommandGuard.cs:21-33`; `CHANGELOG.md:54-62`.

### Who decides the player's role — and when

- `MapEvent.IsPlayerSergeant` demotes the player purely for sitting inside an army led by someone
  else — **even inside the player's own castle** (`Payload/SiegeCommandGuard.cs:35-36`;
  `CHANGELOG.md:66-67`). Owning the settlement is not being the general; the role has to be asserted.
- The role is decided in **two** places, not one: `Team.SetPlayerRole` and
  `TaleWorlds.MountAndBlade.AssignPlayerRoleInTeamMissionController.AfterStart`. The latter's
  `IsPlayerGeneral` / `IsPlayerSergeant` are **get-only auto-properties** whose compiler-generated
  backing fields are literally named `<IsPlayerGeneral>k__BackingField` and
  `<IsPlayerSergeant>k__BackingField`. Patching only the first can be overridden by the second
  (`Payload/SiegeCommandGuard.cs:99-102,:373-380`).
- `SetPlayerRole` runs **before** the mission's `PlayerTeam` exists, so a role decision must come from
  campaign-side truth: `MobileParty.MainParty.MapEvent` with `MapEvent.IsSiegeAssault`,
  `MapEvent.PlayerSide` and `MapEvent.MapEventSettlement`, plus `Settlement.OwnerClan` compared to
  `Clan.PlayerClan` (`Payload/SiegeCommandGuard.cs:245-265`).

### Siege-related `Mission` flags

`Mission` exposes `IsSiegeBattle`, `IsSallyOutBattle`, `IsDeploymentFinished`, `PlayerTeam` and
`DefenderTeam`. **A sally-out is also a siege battle**, so defender-side siege logic must exclude
`IsSallyOutBattle` explicitly (`Payload/SiegeCommandGuard.cs:212-217,:293,:394-395`;
`Payload/CivilianGateCloseFix.cs:15-16`).

### `PlayerSiege.PlayerSiegeEvent` is a computed getter (IL-proven, 2026-08-30)

`PlayerSiege.PlayerSiegeEvent` is **not stored**. It is a computed getter equal to
`MobileParty.MainParty.SiegeEvent ?? MainParty.CurrentSettlement?.SiegeEvent`, verified by IL
inspection of the installed build. There is **no settable mirror**, so a null cannot be repaired by
assignment — a fix has to happen at the effect site. Being computed from the *local* main party also
makes it per-process: a null means *this* peer's own party is not attached to a siege, even when a
siege is live in the world.

A player's real siege is in fact reachable **five** ways —
`MobileParty.SiegeEvent`, `MobileParty.CurrentSettlement.SiegeEvent`,
`MobileParty.AttachedTo.SiegeEvent`, `MobileParty.Army.LeaderParty.SiegeEvent`, and
`MobileParty.Army.LeaderParty.CurrentSettlement.SiegeEvent` — but vanilla's `PlayerSiege` consults
only the first two (`Payload/MapIncidentCrashGuard.cs:189-197`).

Siege members used when repairing one: `SiegeEvent.BesiegerCamp` → `.SiegeEngines` →
`.SiegePreparations`, a `SiegeEvent.SiegeEngineConstructionProgress` exposing `Progress` and
`SetProgress(float)` (vanilla's own siege-progress mutation is `prep.SetProgress(prep.Progress + amount)`),
and `SiegeEvent.BesiegedSettlement` with a `Name` `TextObject` for logging which siege was touched.

Evidence: `Payload/MapIncidentCrashGuard.cs:20-22,:189-197,:225-226,:267-277`;
`UPSTREAM_BUG_REPORT.md:114-117`; `CHANGELOG.md:247-250`. The incident that crashes on this is in
[§5](#map-incidents-il-proven-2026-08-30-game-148).

### Castle gates: standing points and the two ways they die (IL-proven, 2026-08-30)

- `CastleGate.ServerTick` activates the gate's standing points **only** when the door's animation
  parameter is exactly `>= 1.0`: `if (animParam < 1f)` deactivate **all** points, else deactivate only
  the points whose `GameEntity` tag matches the wrong direction. The direction rule is: an **open**
  gate activates its **close** points, a **closed** gate activates its **open** points.
- Vanilla itself parks a **closed** gate at a *frozen* animation parameter of **0.99** in
  `CastleGate.SetInitialStateOfGate`, and an opened door can settle a float-hair under 1.0. In both
  cases the gate is visually at rest but permanently un-interactable, because `ServerTick`'s exact
  test fails. `SiegeGatePromptFix` re-applies vanilla's own tag rule in the band `[0.98, 1.0)`;
  mid-swing doors (`< 0.98`) keep vanilla's everything-off behaviour.
- The parameter is read from the private field `CastleGate._doorSkeleton`
  (`TaleWorlds.Engine.Skeleton`) via `Skeleton.GetAnimationParameterAtChannel(0)`.
- `CastleGate.State` is a `CastleGate.GateState` (with a `Closed` value); the standing points carry a
  `GameEntity` tag of `"open"` or `"close"` (`GameEntity.HasTag(string)`).
- `StandingPoint` exposes `IsDeactivated` and the synchronised setter `SetIsDeactivatedSynched(bool)`;
  `MissionObject` exposes `IsDeactivated` and `IsDestroyed`.
- A **ram-destroyed** gate hangs open and is gone for that battle **by design** — vanilla does not
  allow closing a broken gate, so having no prompt there is correct.

Evidence: `Payload/SiegeGatePromptFix.cs:12-27,:42,:66-101,:116-131`; `CHANGELOG.md:151-160`.

### Civilian missions lock the gate three independent ways (IL-proven, 2026-08-30)

1. Civilian (walk-around) missions call `CastleGate.OpenDoorAndDisableGateForCivilianMission`;
   `SetInitialStateOfGate` then force-opens the door and calls `MissionObject.SetDisabled(true)` on
   the **entire gate machine**, disabling every standing point with it — so
   `CastleGate.GetActionTextForStandingPoint` is never consulted.
2. `CastleGate.CloseDoor()` itself early-outs on `IsDisabled`.
3. `CastleGate.AfterMissionStart` sets the gate's usable team to `Mission.DefenderTeam`, and
   `StandingPointWithTeamLimit.IsDisabledForAgent` requires `agent.Team == UsableTeam` — which in a
   civilian mission never matches the player's team.

All three are deliberate "gates are scenery in town" design: re-enabling the object alone does
nothing, fixing the team alone does nothing. `CastleGate` tracks the situation in the private bool
field `_civilianMission`, and `MissionObject.IsDisabled` has **no public setter** (write it through
`AccessTools.PropertySetter` + `Invoke`). `CastleGate.CloseDoor` / `OpenDoor` perform the whole job —
animation, `SetGateNavMeshState`, colliders — so restoring interaction is sufficient; the closing
behaviour needs no reimplementation. `CastleGate` has both `OnTick` and `ServerTick` and either may be
absent in a given build, so patch whichever resolves. `AfterMissionStart` is a mission-object hook
suitable for postfixing per-gate setup.

Missions (battles and settlement visits alike) are **local on every peer**, and BT contains no gate
code at all (established by an assembly scan) — gate fixes need no networking.

Reusable class: *when an object is "there but has no prompt", look for an exact-threshold test against
an animation/float parameter that vanilla itself never reaches.*

Evidence: `Payload/CivilianGateCloseFix.cs:11-26,:40-57,:73,:82-85,:120`;
`Payload/SiegeGatePromptFix.cs:27`; `CHANGELOG.md:161-171`.

---

## 4. Campaign time control

### The idle hold: time stops without the mode changing

`Campaign.TickMapTime` sets `IsMainPartyWaiting = MobileParty.MainParty.ComputeIsWaiting()` on **every
tick**, and the *stoppable* play / fast-forward modes advance campaign time only while that flag is
false. Consequence: arriving at a clicked destination silently halts time **without changing the
time-control mode** — the speed buttons still read "playing".

`MobileParty.ComputeIsWaiting` is an instance method returning `bool`, declared on `MobileParty`
itself (found with `DeclaredOnly`); `MobileParty.IsMainParty` separates the player's party from AI
parties, and `TimeFlowPatch` postfixes it for the main party only.

The wait-menu mode `CampaignTimeControlMode.UnstoppableFastForwardForPartyWaitTime` **never** consults
that flag, so wait menus keep working even when `ComputeIsWaiting` is forced false. Real pauses (the
pause button's `Stop` mode, menus, encounters) are a separate mechanism entirely — forcing
`ComputeIsWaiting` false does not defeat them.

Evidence: `Payload/TimeFlowPatch.cs:13-20,:44-52,:61-69`.

### The map-click speed downgrade

- `CampaignTimeControlMode.StoppableFastForward` has the numeric value **4**, and vanilla's
  `MapScreen.HandleClickTimeChange` (which implements the "map double click behavior = keep speed"
  option) tests `mode == 4` — so it recognises **only** the stoppable fast-forward variant, never the
  unstoppable one BT enforces (`Payload/MapClickSpeedKeeper.cs:11-14`).
- `SandBox.View.Map.MapScreen.HandleLeftMouseButtonClick` is the path that performs the downgrade:
  every observed `UnstoppableFastForward → StoppablePlay` transition originated there
  (2026-08-19 20:18–20:19) (`Payload/MapClickSpeedKeeper.cs:15-18,:40-51`).
- Clicking the map **while paused** unpauses via a **different** transition, `Stop → StoppablePlay`,
  which is why vetoing `UnstoppableFastForward → StoppablePlay` does not break click-to-unpause
  (`Payload/MapClickSpeedKeeper.cs:20-21,:83-86`).
- `MapScreen` lives in the SandBox view assembly under the full name `SandBox.View.Map.MapScreen` and
  is resolvable by `AccessTools.TypeByName` at payload-apply time
  (`Payload/MapClickSpeedKeeper.cs:33-38`).

### Setting, locking and observing the mode

- `Campaign.TimeControlMode` is a settable property (compiler-generated
  `set_TimeControlMode(CampaignTimeControlMode)`), and reading `__instance.TimeControlMode` inside its
  own setter **prefix** still returns the **old** value — the prefix runs before the write
  (`Payload/MapClickSpeedKeeper.cs:54,:79-87`; `Payload/TimeTrace.cs:83-99`).
- The mode **lock** exists under two member names across builds — `Campaign.SetTimeControlModeLock`
  (a method) and `set_TimeControlModeLock` (a property setter). Probe both and patch whichever exists
  (`Payload/TimeTrace.cs:39-40`; `Payload/TimeEnforcementGuard.cs:84-92`).
- The map bar's time buttons go through
  `TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar.MapTimeControlVM.ExecuteTimeControlChange`
  — hooking it distinguishes a genuine UI button click from a code-driven mode change
  (`Payload/TimeTrace.cs:21-22,:41,:154-164`).
- The `CampaignTimeControlMode` values this area touches: `Stop`, `StoppablePlay`,
  `StoppableFastForward` (== 4), `UnstoppablePlay`, `UnstoppableFastForward`,
  `UnstoppableFastForwardForPartyWaitTime`.
- `Campaign` is reachable both as the compile-time type `TaleWorlds.CampaignSystem.Campaign` and by
  string via `AccessTools.TypeByName("TaleWorlds.CampaignSystem.Campaign")`; the payload uses the
  string form where it must not hard-bind (`Payload/TimeEnforcementGuard.cs:86-87`).

### Time control in co-op (pre-2026-09-04)

BT's `CoopCampaignBehavior.EnforcePlaySpeed` (IL): if host and not paused, it forces
`UnstoppablePlay`/`UnstoppableFastForward` by calling `Campaign.SetTimeControlModeLock(0)` then
`set_TimeControlMode`, **every application tick**. `TimeEnforcementGuard` neutralizes those writes
while no remote peer is connected (solo host). When our guard blocks the write, the mode never
changes, so BT retries every tick — which, with the `[TIME]` tracer on, floods the log unless
coalesced (now handled by `TraceThrottle`).

> Scoping the solo time-neutralizer to the campaign map (a 2026-09-04 hypothesis for the
> sideways-character bug) was tried and **reverted** — it did not affect that bug. Do not re-add it
> without evidence. The sideways/folded character is a separate, likely GPU-side, vanilla issue.

---

## 5. Encounters, map events and incidents

### Mission / menu / encounter chokepoints

Every transition into a 3D scene or a map menu passes one of these, so patching them catches the
event regardless of which mod or which code path initiated it (`Payload/TracePatches.cs:23-30,:36-45`):

| Member | Notes |
|---|---|
| `MissionState.OpenNew` | Every 3D mission launch — see [§1](#missionstateopennew-is-the-single-mission-chokepoint). |
| `GameMenu.ActivateGameMenu` | Map menu **opens**. |
| `GameMenu.SwitchToMenu` | Map menu **switches**. |
| `EncounterManager.StartSettlementEncounter` / `StartPartyEncounter` | Settlement and party encounters. |
| `MapEvent.CanPartyJoinBattle` | Returns `bool` — read it with a postfix `__result` (`:142-149`). |
| `PlayerEncounter.StartBattle` → `PlayerEncounter.Finish` | The player encounter lifecycle (`:179-189`). |
| `DefaultEncounterGameMenuModel.GetGenericStateMenu` | Returns the menu id as a `string` and **can return null** — the tracer prints `(null)` (`:193-202`). |

**AI parties enter settlements constantly.** The un-filtered encounter hook flooded the log on
2026-08-18; only main-party encounters are diagnostic signal. Filter on `MobileParty.IsMainParty` —
and note an argument may be a `PartyBase`, whose `.MobileParty` you check
(`Payload/TracePatches.cs:103-131,:114-118,:159-171`).

### Re-entering an encounter after it finishes

After the player leaves an encounter meeting, `PlayerEncounter.Finish` runs; the encounter can be
reopened via `EncounterManager.StartPartyEncounter` → `PlayerEncounter.RestartPlayerEncounter`, which
re-shows the `encounter_meeting` game menu (`Payload/EncounterLoopGuard.cs:8-14`).

### The campaign-tick hot stack, and why a hang needs a time guard

`EncounterManager.HandleEncounterForMobileParty(MobileParty)` handles one party's encounter per
campaign tick; skipping it for a single party for one tick is benign, because it reruns on the next
tick (`Payload/PartyAiCrashGuard.cs:51,:125-131`).

The chain that can become multi-second is
`Campaign.RealTick` / `Campaign.Tick` → `EncounterManager.HandleEncounters` →
`AiEngagePartyBehavior.AiHourlyTick` → `FactionManager.IsAtWarAgainstFaction`
(`UPSTREAM_BUG_REPORT.md:140-147`).

**During a campaign hang nothing throws** — the exception/cooldown machinery never helps. A
frame-starvation hang needs a **time** guard, not an exception guard
(`UPSTREAM_BUG_REPORT.md:153-154`).

### Map incidents (IL-proven, 2026-08-30; game 1.4.8+)

- Game build **1.4.8 added map incidents** in the namespace `TaleWorlds.CampaignSystem.Incidents`
  (types `Incident` and `IncidentEffect`) (`Payload/MapIncidentCrashGuard.cs:42,:56,:92`;
  `UPSTREAM_BUG_REPORT.md:112-113`).
- `IncidentEffect.SiegeProgressChange`'s **consequence** lambda dereferences the whole chain
  `PlayerSiege.PlayerSiegeEvent.BesiegerCamp.SiegeEngines.SiegePreparations` with **no null check** —
  a guaranteed NRE when the player's party is not attached to a siege
  (`Payload/MapIncidentCrashGuard.cs:14-17,:161-175`). The siege-side fact behind it is
  [`PlayerSiege.PlayerSiegeEvent` is a computed getter](#playersiegeplayersiegeevent-is-a-computed-getter-il-proven-2026-08-30).
- `SiegeProgressChange` compiles to **at least two** nested-display-class lambdas named
  `<SiegeProgressChange>b__N`, **both returning `List<TextObject>`**: `b__1` is the consequence (calls
  `PlayerSiege.get_PlayerSiegeEvent`, crashes) and `b__2` is the preview text (never touches the
  siege). **Select the target by IL — does it call `get_PlayerSiegeEvent`? — never by `b__` number**,
  so the harmless preview lambda stays untouched and compiler renumbering across game patches cannot
  break the target (`Payload/MapIncidentCrashGuard.cs:33-35,:72-74`).
- The lambda's closure display class carries a field named **`amountGetter` of type `Func<float>`** —
  the name derives from the factory method's parameter name
  (`Payload/MapIncidentCrashGuard.cs:248-259`).
- Vanilla's siege-progress report text is localization id `{=C0kUpB48}` —
  `{?AMOUNT > 0}Increased{?}Decreased{\?} siege progress by {ABS(AMOUNT)}%.` — with the `AMOUNT`
  variable set to `MathF.Round(amount * 100f)`, so a repaired effect can reproduce it exactly
  (`Payload/MapIncidentCrashGuard.cs:231-233`).
- **`IncidentEffect.Consequence()` is the single choke point every incident effect flows through**,
  returning `List<TextObject>`; `Incident.InvokeOption` is the campaign entry point behind the
  option-click handler and **also** returns `List<TextObject>` — the return-type check is what
  disambiguates its overloads. Both are patchable as a family-wide safety net
  (`Payload/MapIncidentCrashGuard.cs:83-98,:279,:294`; `CHANGELOG.md:257-259`).
- The incident-popup-vs-dead-siege crash **reproduces in pure vanilla singleplayer**: the popup sits
  open while the siege ends, so on confirm no siege exists anywhere to receive progress
  (`Payload/MapIncidentCrashGuard.cs:29-31`).

---

## 6. Heroes, clans, marriage, pregnancy, succession

### Old-age illness death (`AgingCampaignBehavior`)

`TaleWorlds.CampaignSystem.CampaignBehaviors.AgingCampaignBehavior` exposes `IsItTimeOfDeath(Hero)`
and `DailyTickHero(Hero)` — both unambiguous, so both are patchable with a bare `AccessTools.Method`
with no explicit signature (`Payload/IllnessDeathGuard.cs:66-79,:103`).

The decompile-proven vanilla flow (`Payload/IllnessDeathGuard.cs:9-16,:112,:116-123`):

1. `BecomeOldAge` is **55**. Once the main hero is `age >= BecomeOldAge`, **every** daily tick calls
   `IsItTimeOfDeath`, which rolls `ProbabilityOfDeath`.
2. On a hit the main hero "Caught Illness": `Campaign.MainHeroIllDays` goes from `-1` to `0`, and
   `Hero.IsMainHeroIll` is defined as `MainHeroIllDays != -1`. `Campaign.MainHeroIllDays` is
   **publicly writable** (the guard assigns `-1` directly).
3. `DailyTickHero` increments the ill days; past day 3 it drains HP at 5% × days daily, and at ≤ 1 HP
   it kills via `KillMainHeroWithIllness` — unless an extra life is consumed.
4. `KillMainHeroWithIllness` sets `Hero.DeathMark` to
   `KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge`, and `DailyTickHero` has an
   `ApplyByDeathMark` branch **at the top** that finishes the kill from that mark on a later tick.
   `Hero.DeathMark` has a **private setter** — clear it through
   `AccessTools.PropertySetter(typeof(Hero), "DeathMark")` with `KillCharacterActionDetail.None`.

`Hero.Age` is a floating-point value (cast to `int` for logging); `Hero.MainHero` and `Hero.Name` are
the standard accessors, and reference comparison (`hero != Hero.MainHero`) is the idiom used to scope
a patch to the local player (`Payload/IllnessDeathGuard.cs:83,:91`).

### Dead heroes and `HeroDeveloper` (IL-proven)

- `Hero.ChangeState(Hero.CharacterStates.Active)` fires `OnHeroActivatedEvent`, which reaches
  `CharacterDevelopmentCampaignBehavior.OnHeroActivated` and calls
  `hero.HeroDeveloper.DevelopCharacterStats()` (`Payload/DeadHeroReactivationFix.cs:13-14`).
- `Hero.OnDeath()` nulls the private `_heroDeveloper` field and is the **only** place it is ever
  nulled (proven). A dead `Hero` therefore always has a null `HeroDeveloper`, and any post-death
  dereference NREs (`Payload/DeadHeroReactivationFix.cs:15-16`). The manifested frame (the
  character-development behavior) is never the root cause — the callers are.
- `TaleWorlds.CampaignSystem.Issues.IssueManager.MakeAlternativeTroopsReturn(TroopRoster)` loops the
  alternative-solution troops and calls `Hero.ChangeState(Active)` on every hero among them **without
  any `IsAlive` check** — the vanilla defect (`Payload/DeadHeroReactivationFix.cs:10-13,:47-49`).
- A legitimate revive **clears the dead state before** calling `ChangeState`, so `Hero.IsDead` is
  already false — meaning a `dead → Active` prefix block never interferes with a real revive and is
  safe as a domain invariant (`Payload/DeadHeroReactivationFix.cs:28-29`).

Member surface used here: `Hero.CharacterStates` is a nested enum on `Hero` with an `Active` member;
`Hero` exposes `IsDead`, `IsAlive`, `StringId` and `Name`; `CharacterObject` exposes `IsHero` and
`HeroObject` (and **`HeroObject` can be null even when `IsHero` is true**)
(`Payload/DeadHeroReactivationFix.cs:95-98,:131-135,:151`).

### Hero member surface and identity operations

- `Hero` exposes `IsFemale`, `FirstName` (a nullable `TextObject`), `Name` (`TextObject`), `Father`
  (nullable), `Clan` (nullable), `CurrentSettlement` (nullable), and `PartyBelongedTo` (which itself
  has a `StringId`). **`Hero.MainHero` can be null** — on the main menu, for instance
  (`Payload/PregnancySync/PregnancySyncGuard.cs:172-176,:209-216,:302-312,:482,:496-500`).
- `Hero.FindFirst(Func<Hero,bool>)` searches **all** heroes including the dead;
  `Hero.AllAliveHeroes` enumerates only the living; `Hero.StringId` is the stable persistent id and
  `Hero.Name` needs `ToString()` (`Payload/CoopHeroIdentityLock.cs:227-255`;
  `Payload/PregnancySync/PregnancySyncGuard.cs:477`).
- `Hero` derives from `MBObjectBase`, whose `StringId` is the shared member
  (`Payload/PregnancySync/PregnancySyncGuard.cs:461-469`).
- **`Hero.StringId` is writable**, and re-keying a live object requires
  `MBObjectManager.Instance.UnregisterObject(obj)` → set `StringId` →
  `MBObjectManager.Instance.RegisterPresumedObject(obj)`; `RegisterPresumedObject` is the
  re-registration entry point for an object whose id is being asserted
  (`Payload/PregnancySync/PregnancySyncGuard.cs:413-420`).
- `Hero.SetName` takes **two** `TextObject` arguments (name, firstName) — passing the same object for
  both is how a first-name-only rename is done (`Payload/PregnancySync/PregnancySyncGuard.cs:410-411`).
- Appearance: `BodyProperties.FromString(string, out BodyProperties)` is a bool try-parse;
  `BodyProperties` exposes `.StaticProperties`, `Hero` exposes a writable `.StaticBodyProperties`, and
  `Hero.BodyProperties.ToString()` produces the round-trippable xml-ish string form
  (`Payload/PregnancySync/PregnancySyncGuard.cs:309,:403-406`).

### Marriage barter

`TaleWorlds.CampaignSystem.BarterSystem.BarterManager.ApplyAndFinalizePlayerBarter(Hero offererHero,
Hero otherHero, BarterData barterData)` applies **all** offered barterables in one loop — so a mod
that suppresses only one barterable leaves its siblings (e.g. the gold) applying natively. The barter
is only atomic if the whole call is cancelled.

`BarterData.GetOfferedBarterables()` returns `List<Barterable>` and **can be null**;
`TaleWorlds.CampaignSystem.BarterSystem.Barterables.MarriageBarterable` is a `Barterable` subtype
detectable with a plain `is` test.

Evidence: `Payload/MarriageBarterGuard.cs:5-6,:13-15,:35,:54,:63-74`.

### Pregnancy (IL-proven, 2026-08-30)

- Vanilla's conception roll is **daily**, in `PregnancyCampaignBehavior.RefreshSpouseVisit`, and fires
  only when `CheckAreNearby(hero, spouse)` passes: the couple is in the **same settlement** (waiting
  inside a castle counts — the party's `CurrentSettlement` *is* that castle) or in the **same party**.
  Clans other than the player's additionally pass a 20% abstract roll. Fertility window is ages
  **18–45**, with the chance falling with age and with the number of existing children.
- `PregnancyCampaignBehavior` lives in `TaleWorlds.CampaignSystem.CampaignBehaviors`, and
  `CheckAreNearby` is a **non-public** method taking `(Hero hero, Hero spouse)` and returning `bool` —
  reachable by `AccessTools.TypeByName` + `AccessTools.Method` and patchable with a postfix reading
  `__result`.
- `MakePregnantAction.Apply` is a **static** method taking a single `Hero`, in
  `TaleWorlds.CampaignSystem.Actions` — the single choke point for "this hero became pregnant".
- `CampaignEvents.OnGivenBirthEvent` has the signature
  `(Hero mother, List<Hero> aliveChildren, int stillbornCount)` and is subscribed with
  `AddNonSerializedListener(object owner, handler)` — the owner must be a **stable object reference**.
  **Twins are the ~3% path**, so a birth payload must be a list.
- `HeroCreator.DeliverOffSpring(Hero mother, Hero father, bool isFemale)` returns the new `Hero` and
  **can return null**. It **deterministically derives** clan, culture and birthday from the parents
  (identical on any machine given the same parents) while **randomizing** id, gender, name and
  appearance — which is exactly why only those four ever need to go on a wire. (`CampaignTime` has no
  public round-trippable form anyway, a second reason birthday is re-derived rather than sent.)
- A newborn is an **age-0 infant** that appears in Clan → Members and is **not visible on the campaign
  map until coming of age** — "I don't see the child" is expected UI behaviour, not a sync failure.

Vanilla already works; nothing here needed changing. BT's suppression is literally `return !IsClient`,
so the **host's** rolls run untouched and only the client's are suppressed — the gap was never
conception, only that the resulting birth never reached the other machine (see `docs/BT-INTERNALS.md`).

Evidence: `Payload/PregnancySync/PregnancySyncGuard.cs:129-153,:142,:164,:184-185,:88,:243,:75,:372-377`;
`Payload/PregnancySync/BirthPayloadData.cs:33-38,:105`; `tests/BirthPayloadTest/Program.cs:33-44`;
`README.md:123-125`; `CHANGELOG.md:174-181`.

### `CampaignEvents` must be wired per campaign

`CampaignEvents` resolves through `Campaign.Current`, which is **null at module load and is
per-campaign**. Any subscription must therefore be (re)wired at game start rather than at Harmony
patch time, and re-wired when a new campaign loads — keyed on
`ReferenceEquals(_subscribedCampaign, Campaign.Current)` so re-entry is a no-op
(`Payload/PregnancySync/PregnancySyncGuard.cs:59-62,:84-89`).

### Clans

`Clan` exposes `Heroes`, `Companions`, `WarPartyComponents`, `WarPartyLimit` (driven by clan tier),
`InitialHomeSettlement`, `MapFaction` and the static `Clan.PlayerClan`;
`Clan.PlayerClan.WarPartyComponents.Count` vs `Clan.PlayerClan.WarPartyLimit` is the live war-party
budget, and `Campaign.Current.Models.ClanFinanceModel.PartyGoldLowerThreshold` is the gold gate for
party creation (`Payload/ClanPartyCreationAdvisor.cs:19-27,:277-300`). `Hero.PartyBelongedTo` resolves
to the `MobileParty` a hero currently leads/belongs to, and `MobileParty` exposes the static
`MainParty`, plus `LeaderHero`, `IsActive` and `StringId`; a freshly created clan party is only "ours"
when `party != MobileParty.MainParty && party.LeaderHero == newLeader`
(`Payload/ClanPartyCreationAdvisor.cs:176-178,:221-225`).

The screen that surfaces all of this — and the rules it applies — is in
[§9 Clan → Parties → Create New Party](#clan--parties--create-new-party-il-proven-2026-09-01).

### Succession

`ChangePlayerCharacterAction.Apply(Hero)` is vanilla's supported mechanism for changing **who the
player is** — the same path death-succession uses — and it works on the campaign map, **outside a
mission**. Never call it inside one (`Payload/CoopHeroIdentityLock.cs:22-24,:167`, pinned by the
self-test at `:322`; `CHANGELOG.md:142-144`). Its co-op use is in [§11](#saveload).

---

## 7. Settlements, stash, inventory

### Settlements and the settlement value model

- `Settlement.CurrentSettlement` is a static "where the player is" accessor; `Settlement.All`
  enumerates every settlement; `Settlement.Stash` is an `ItemRoster` and **can be null**
  (`Payload/StashSync/StashSyncGuard.cs:158-159,:308-317`).
- `DefaultSettlementValueModel.FindMostSuitableHomeSettlement(Clan)` is reached from
  `CharacterCreationContent.ApplyCulture` → `Clan.ResetPlayerHomeAndFactionMidSettlement`, and
  internally calls `FindFarthestDistanceBetweenSettlementsInClan`, which dereferences
  `clan.MapFaction.FactionMidSettlement` and passes it to `MapDistanceModel.GetDistance` — **with no
  null check**, because in single-player the faction/settlement graph is always complete at that
  point. It is null while a client's faction/settlement graph is still replicating, so the crash lands
  during **culture selection** (`Payload/ClientHeroCreationGuard.cs:12-19`;
  `docs/UPSTREAM_CONTRIBUTION.md:30-40`).
- The method's **own** edge cases return `Clan.InitialHomeSettlement`, else the first settlement — so
  those are the shape-correct fallbacks a guard should substitute, and a null-guard fallback exists
  in-method (`Payload/ClientHeroCreationGuard.cs:21-24,:57-64`; members `Settlement.All`,
  `Settlement.All[0]`, `Settlement.All.Count`). The general class of "native code that assumes a
  fully-synced world" is described in [§8](#the-defendsettlement-half-sync-nre-2026-08-19).

### Inventory and the stash

- `InventoryLogic` (`TaleWorlds.CampaignSystem.Inventory`) has a **private** field `_inventoryMode`
  and a `DoneLogic` method returning `bool`. `DoneLogic` is the commit point — the close-the-screen
  hook, and the same method BT itself patches for the workshop warehouse
  (`Payload/StashSync/StashSyncGuard.cs:22-24,:71-77,:135`; `UPSTREAM_BUG_REPORT.md:169-173`).
- The mode enum is the **nested** type `Helpers.InventoryScreenHelper+InventoryMode`, and its member
  `Stash` currently has value **3** in the installed build — resolve it live by
  `TypeByName` + `Enum.Parse`, and keep 3 only as a labelled fallback
  (`Payload/StashSync/StashSyncGuard.cs:52-55,:99-108`).
- `Campaign.InventoryManager → .InventoryLogic` is the chain that answers "is an inventory screen open
  right now"; **both links can be null** (a null manager means no inventory session)
  (`Payload/StashSync/StashSyncGuard.cs:386-400`).

### Rosters

- `ItemRoster` exposes `Count`, `GetElementCopyAtIndex(int)` returning an `ItemRosterElement` (a
  **copy** — safe to hold while mutating), `Clear()`, and `AddToCounts(EquipmentElement, int)`.
  **`AddToCounts` is not count-validated** — a negative count reaches it unchallenged, which is why
  the parser rejects `Count <= 0` (`Payload/StashSync/StashSyncGuard.cs:185-187,:336-364`;
  `Payload/StashSync/StashPayloadData.cs:101-104`).
- `ItemRosterElement` exposes `.EquipmentElement` (with `.Item : ItemObject` and
  `.ItemModifier : ItemModifier`, both nullable) and `.Amount`. `EquipmentElement` has an
  `(ItemObject, ItemModifier)` constructor where a null modifier means "no modifier"
  (`Payload/StashSync/StashSyncGuard.cs:187-204,:357-359`).
- `TroopRoster` (`TaleWorlds.CampaignSystem.Roster`) has `RemoveIf(predicate over
  TroopRosterElement)`, returning a collection exposing `.Count` of the removed entries.
  **`TroopRosterElement` is a struct** — `default(TroopRosterElement)` is valid and has a null
  `Character` (`Payload/DeadHeroReactivationFix.cs:4,:77-78,:91-99,:163`).

### Which items exist on only one machine

- `ItemObject.IsCraftedByPlayer` exists in this build (`TaleWorlds.Core.dll` /
  `TaleWorlds.CampaignSystem.dll`) and is true **only** for genuinely player-crafted items — it is the
  correct machine-local test (`Payload/StashSync/StashSyncGuard.cs:213-216,:224`).
- **`ItemObject.WeaponDesign != null` is not that test.** `WeaponDesign` is non-null for *every* item
  defined as a `<CraftedItem>`: 260 in `SandBoxCore/ModuleData/items/weapons.xml` plus 23 in
  `tournament_weapons.xml` on Native v1.4.8 (~283 objects — most swords, axes, mauls, spears,
  polearms). Those have stable `StringId`s (e.g. `peasant_maul_t1`) and are ordinary
  `MBObjectManager` registrations that resolve identically on every machine
  (`Payload/StashSync/StashSyncGuard.cs:213-218`).
- Player-**crafted** items cannot be resolved by `StringId` on another machine — replicating them
  requires `WeaponDesign` serialization (`UPSTREAM_BUG_REPORT.md:174-177`).
- `MBObjectManager.Instance.GetObject<T>(string stringId)` resolves a registered object by id and
  returns **null** when the id is unknown on this machine; for a locally-registered object it returns
  the **same reference**, which makes
  `ReferenceEquals(GetObject<ItemObject>(item.StringId), item)` a valid "this id round-trips locally"
  test (`Payload/StashSync/StashSyncGuard.cs:228,:350-358`).

---

## 8. Party AI and behaviours

### The `DefendSettlement` half-sync NRE (2026-08-19)

`MobilePartyAi.GetBehaviors` is reached from `Campaign.PartiesThink` during the campaign tick. In the
installed build's IL at roughly offset `04B4`, the `DefendSettlement` branch reads
`_mobileParty.TargetSettlement` and, when that is null, falls back to `targetParty.TargetSettlement` —
**with both null it dereferences null**.

**Vanilla never produces `AiBehavior.DefendSettlement` with no target settlement *and* no target
party.** That combination is only reachable through externally-mutated (co-op half-synced) party
state, and the inconsistent state **self-heals** once sync completes — so the correct fix is to skip
the AI tick for that specific state, not to invent a target.
`EncounterManager.HandleEncounterForMobileParty` makes the same assumption
(see [§5](#the-campaign-tick-hot-stack-and-why-a-hang-needs-a-time-guard)); the settlement-model
sibling of this class of bug is in
[§7](#settlements-and-the-settlement-value-model).

Signature needed to bind a Harmony finalizer: `GetBehaviors` returns its results through **by-ref
parameters** — `ref AiBehavior bestAiBehavior, ref IInteractablePoint behaviorObject,
ref CampaignVec2 bestTargetPoint`. `CampaignVec2` lives in `TaleWorlds.CampaignSystem.Map` and is a
**value type**, so `default(CampaignVec2)` is a valid substitute.

The safe "hold at current position this tick" answer is `AiBehavior.Hold` with a null `behaviorObject`
and the party's own `Position` as the target point.

Evidence: `Payload/PartyAiCrashGuard.cs:5,:13-25,:101-102,:112-114`;
`docs/UPSTREAM_CONTRIBUTION.md:43-51`.

### Party AI member surface

`MobilePartyAi` has a private instance field `_mobileParty` holding the owning `MobileParty`, plus
`Tick` and `GetBehaviors` (`Payload/PartyAiCrashGuard.cs:37,:39,:45`).

`MobileParty` exposes `DefaultBehavior`, `TargetSettlement`, `TargetParty`, `ShortTermTargetParty`,
`Position` (a **`CampaignVec2` struct** — `default(CampaignVec2)` is valid), `StringId`, `IsMainParty`,
`CurrentSettlement`, `AttachedTo`, `Army`, `MapEvent`, `SiegeEvent`, `LeaderHero`, `IsActive`, `Party`
and the static `MainParty`
(`Payload/PartyAiCrashGuard.cs:86-89,:114,:116`; `Payload/TracePatches.cs:162`;
`Payload/MapIncidentCrashGuard.cs:189-197`; `Payload/ClanPartyCreationAdvisor.cs:221-225`).

`MobileParty.IsMainParty` paired with `PartyBase.MobileParty` is how you tell whether an encounter
argument involves the player's own party (`Payload/TracePatches.cs:114-118,:159-171`).

---

## 9. UI / Gauntlet / screens / character creation

### The clan screen can NRE on a half-synced graph

`SandBox.GauntletUI.GauntletClanScreen.CreateDataSource` builds a `ClanManagementVM` over the
clan/party graph; a half-synced or host-mirrored graph makes it throw an NRE when the screen opens
(`Payload/ClanScreenCrashGuard.cs:8-13,:26-27`).

`TaleWorlds.ScreenSystem.ScreenManager.PopScreen()` is a **static, parameterless** method (invoked
with `Invoke(null, null)`) and is the correct way to pop back to the map from inside a failed screen
build (`Payload/ClanScreenCrashGuard.cs:58-60`).

### Clan → Parties → Create New Party (IL-proven, 2026-09-01)

`TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanPartiesVM` drives the
tab, through `GetNewPartyLeaderCandidates` (the leader popup list), `GetCanCreateNewParty` (button
enable + a `TextObject disabledReason`) and `CreateNewClanParty(Hero newLeader)`
(`Payload/ClanPartyCreationAdvisor.cs:46,:72-88,:103,:171`).

Decoded from the installed build's IL (`Payload/ClanPartyCreationAdvisor.cs:19-28`;
`CHANGELOG.md:86-98`; `README.md:193-197`):

- The **leader popup** lists `Clan.Heroes` + `Clan.Companions` and **disables a card with a reason**
  when the hero is a prisoner / released / fugitive, a child, in someone else's party, already leading
  a party, a governor, in the `Disabled` state, at sea, or when
  `hero.Gold + Hero.MainHero.Gold` is under `ClanFinanceModel.PartyGoldLowerThreshold`.
- The **"Create New Party" button** is disabled for exactly four reasons: prisoner, no free war-party
  slot (`Clan.WarPartyLimit`, driven by clan tier), no available hero, and not enough gold.
- `CreateNewClanParty` creates the party with the **leader only** — the hero is removed from the main
  party, the party is spawned beside it and set to hold. **Vanilla has no troop step**; the player is
  expected to meet the party on the map and use "Manage troops". On a client the created party is
  provisional until the host confirms it.

Two shapes worth pinning:

- `GetNewPartyLeaderCandidates` is a C# **yield iterator** — it yields a fresh enumerator per
  `GetEnumerator()` call and has no side effects, so a postfix can enumerate it for logging without
  disturbing the VM's own `foreach` (`Payload/ClanPartyCreationAdvisor.cs:40-42,:119-121`).
- The card items — `TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.ClanCardSelectionItemInfo`
  — expose `Title` (`TextObject`), `IsDisabled` (`bool`) and `DisabledReason` (`TextObject`) as
  **public fields**, so a field lookup must be tried before a property lookup
  (`Payload/ClanPartyCreationAdvisor.cs:157-167,:332-336`).

### Pushing a campaign screen safely

`Helpers.PartyScreenHelper.OpenScreenAsManageTroops(MobileParty)` is vanilla's own manage-troops entry
point — the same call used by the "manage garrison" menu and the clan-member conversation — and
expects to be invoked **from the map state**. Opening it deferred one tick with the clan screen popped
makes it sit on the map exactly like those flows
(`Payload/ClanPartyCreationAdvisor.cs:32-38,:256,:337`; `CHANGELOG.md:95-98`).

`Game.Current.GameStateManager` exposes `ActiveState` and `PopState(int)`. The campaign screen states
involved are `TaleWorlds.CampaignSystem.GameState.PartyState`, `ClanState` and `MapState`, and
popping a `ClanState` with `PopState(0)` lands on the `MapState`
(`Payload/ClanPartyCreationAdvisor.cs:7,:239-255`). Combine with `Mission.Current != null` (the "inside
a mission" test) to refuse pushing a campaign screen over a mission — pushing over a mission or over
another party screen is the classic way a helper mod wedges the UI.

### Character creation

Character creation is a game state class
`TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState`, with the lifecycle
`OnInitialize` → `OnActivate` → `OnStageActivated(stage)` → `Refresh` →
`FinalizeCharacterCreationState`. `OnStageActivated`'s first argument is the **stage object**, whose
runtime type name identifies the stage
(`Payload/CharacterCreationTrace.cs:30,:41-45,:99-114`).

Note the scope: **only the state machine is instrumented**. The scene / agent-visuals / pose code that
actually renders the model is not patched, so a rendering defect (e.g. the 2026-09-04 sideways
banner-editor preview) is caught only if something throws inside a lifecycle call, or if the
session-wide first-chance observer picks it up. The culture-selection crash that *is* reachable from
this path is in [§7](#settlements-and-the-settlement-value-model).

### Player-facing messages and `TextObject`

On-screen player messaging is
`InformationManager.DisplayMessage(new InformationMessage(string, Color))`; `TaleWorlds.Library.Color`
has a float RGB constructor (the mod uses `new Color(1f, 0.75f, 0.3f)`, an amber). **The call can
throw** — e.g. too early in startup — and must be guarded (`Harness/Log.cs:3-4,:122-131`, called from
`Payload/PlayerIdentityGuard.cs:138`, `Payload/ClientHeroCreationGuard.cs:68`,
`Payload/ClientBootstrapFix.cs:182`, `Payload/BootstrapWatch.cs:87`,
`Payload/CoopHeroIdentityLock.cs:173`).

`TextObject` (`TaleWorlds.Localization`) is the type of every UI reason string in this area, and
`ToString()` renders it — but is wrapped in try/catch throughout, because it can throw on a
malformed/uninitialised object (`Payload/ClanPartyCreationAdvisor.cs:10,:302-312`).

---

## 10. Agents and visuals

### Agent identity and control

- Agent control is set through the `Agent.Controller` property (`set_Controller`) taking an
  `AgentControllerType`, whose `Player` value marks a takeover. Moving control between agents at
  runtime is just two assignments (`impostor.Controller = AI; mine.Controller = Player`). AI churn on
  this setter is high-frequency noise for a tracer
  (`Payload/ControlTrace.cs:32,:91-104`; `Payload/PlayerIdentityGuard.cs:95,:103`).
- **"Am I actually playing my own hero?"** is `Mission.MainAgent.Character` compared to
  `Hero.MainHero.CharacterObject` (`Payload/PlayerIdentityGuard.cs:62-72`).
- `Agent` exposes `Character` (with a `Name` that may be null), `Index`, `Team`, `IsMainAgent`,
  `IsHuman`, `Main` (static), `IsPlayerControlled` and `Origin` — enough to describe an agent without
  a decompiler (`Payload/ControlTrace.cs:281-297`; `Payload/CoopCommandSplit.cs:273,:277-278`).
- **`Agent.IsActive` is a method, not a property** (`Payload/StealthHideoutAdvisor.cs:88`).
- `Mission.Agents` is enumerable; an agent must be filtered by `IsHuman` and `IsActive()` to be a real
  live participant, and `Agent.Index` is a stable per-mission identifier usable as a fallback display
  name (`Payload/PlayerIdentityGuard.cs:75-82,:154`).

### Conversation camera

`SandBox.View.Missions.MissionConversationCameraView.MakeSpeakerLookToListener` dereferences the
speaker/listener conversation agents **with no null check**; if an agent is removed mid-conversation
the camera tick NREs and the exception escapes to a CTD.
`UpdateAgentLooksForConversation` is the sibling method on the same view
(`Payload/ConversationCameraCrashGuard.cs:7-11,:23,:31`).

### The action / animation catalog (IL- and reflection-proven, 2026-08-19)

- `ActionIndexCache` holds **static mirror fields of its own type**, one per named action, each with
  an `int Index` property. An **unprimed mirror reads `Index == -1`**. `ActionIndexCache.Create(string)`
  builds a fresh entry from the live native catalog, and the **mirror field's own name is the action
  name** to pass — so a stale mirror can be rebuilt by reflection. `act_none` is the sentinel field and
  must be excluded from probing and priming
  (`Payload/ClientBootstrapFix.cs:129-130,:220-247,:288-320`).
- The genuine "catalog is loaded" gate is `MBAnimation`: `GetNumActionCodes()`,
  `GetNumAnimations()`, `GetActionCodeWithName(string)` returning an `int` (negative = unknown), and
  `IsAnyAnimationLoadingFromDisk()` returning `bool` (`Payload/ClientBootstrapFix.cs:131-134,:252-284`).
- Four action names known to exist in a loaded catalog and usable as readiness probes:
  `act_inventory_idle_start`, `act_inventory_idle`, `act_command_leftstance`,
  `act_walk_idle_1h_with_shield_left_stance` (`Payload/ClientBootstrapFix.cs:267-270`).
- **Neither `ActionIndexCache` nor `MBAnimation` has a guaranteed defining assembly across game
  versions** — search `TaleWorlds.Core`, `TaleWorlds.Engine` and `TaleWorlds.MountAndBlade` by name
  (`Payload/ClientBootstrapFix.cs:109-124`).
- Observed magnitudes on game 1.4.8.119303: `actions=5167`, `animations=6170`, native action-cache
  sentinel index **4008** while the static mirror sentinel still read **-1**. A session can therefore
  have a fully loaded native catalog and entirely unprimed mirrors at the same time — that gap is what
  false-negatives BT's audit (`UPSTREAM_BUG_REPORT.md:10-14`;
  `docs/UPSTREAM_CONTRIBUTION.md:14-27`).

### Disguises

The hideout ambush re-dresses your own hero in the enemy's colours; see
[§1 Hideout "Sneak in"](#hideout-sneak-in-is-the-stealth-ambush-mission-il-proven-2026-09-01).

---

## 11. Save/load

### `MBSaveLoad` moves between assemblies

`MBSaveLoad` may live in **either** `TaleWorlds.Core` **or** `TaleWorlds.SaveSystem` depending on the
build — resolve `TaleWorlds.Core.MBSaveLoad` first and fall back. `LoadSaveGameData` is the in-game
save-load entry point (its first argument is the save name) and the right place to bracket anything a
mid-session load re-derives (`Payload/RoleTrace.cs:44-58`).

### A save stores exactly one player identity

A Bannerlord save stores **exactly one** player identity — whoever was `Hero.MainHero` when it was
written. **Nothing in the load path re-derives who the loading player is**, and there is no
per-machine identity in the save format. That is why passing a shared co-op save around makes the
loader play the previous host's hero (`Payload/CoopHeroIdentityLock.cs:12-16`, field-proven
2026-08-30; `CHANGELOG.md:136-138`).

The supported remedy is `ChangePlayerCharacterAction.Apply(Hero)` on the campaign map — see
[§6 Succession](#succession).

BT side: the identity registry (slot / steam / password claims) is consulted **only on the client join
flow**, and `SharedSaveMode` is a bare flag with no identity resolution of its own (`CHANGELOG.md:136-146`;
detail in `docs/BT-INTERNALS.md`).

### Campaign identity and persistence keys

- `Campaign.Current.UniqueGameId` is the campaign's stable identity string and the correct key for
  per-campaign persistence; **it can be null or empty**, in which case no campaign identity is
  available (`Payload/CoopHeroIdentityLock.cs:101-106`).
- Campaign age is readable as `Campaign.Current.Models.CampaignTimeModel.CampaignStartTime` compared
  against `CampaignTime.Now`, both via `.ToDays` (`Payload/CoopHeroIdentityLock.cs:216-219`).

---

## 12. Misc — module layout, assembly binding, reflection, environment

### Module layout

A Bannerlord module assembly lives at `…/Modules/<Module>/bin/Win64_Shipping_Client/<dll>`, i.e.
**exactly two directories below the module root**. Module-root files (`guardconfig.json`,
`hero-identity.json`, `.hotreload-dev`, `CrashGuard.log`) are therefore reached with
`Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assembly.Location), "..", ".."))` — never a
hardcoded path. The game's own engine assemblies live under `<Game>/bin/Win64_Shipping_Client/`.

Evidence: `Payload/BattleMode.cs:353-355`; `Harness/HotReload.cs:65-69`; `Harness/Log.cs:49-51`;
`Harness/GuardConfig.cs:21-22`; `Payload/TimeFlowPatch.cs:85-86`;
`Payload/ShareTimeControl.cs:194-195`; `install.cmd:42`; `Harness/BLTDeploymentCrashGuard.csproj:13-14`.

Related runtime paths: the mod's log lands at
`<Game>\Modules\BLTDeploymentCrashGuard\CrashGuard.log` and is rotated to `CrashGuard.log.1` (the
collector grabs both); runtime config is `<Game>\Modules\BLTDeploymentCrashGuard\guardconfig.json`
(`install.cmd:72`; `collect-diagnostics.cmd:33-35`). Bannerlord writes crash reports as `.html` files
into `%USERPROFILE%\Documents` with "crash" in the filename (`collect-diagnostics.cmd:40-43`).

### Module lifecycle and load order

- `MBSubModuleBase` is the module entry point. The harness overrides five members, and **the access
  modifiers differ in the base class**: `OnSubModuleLoad`, `OnBeforeInitialModuleScreenSetAsRoot`,
  `OnGameStart` and `OnApplicationTick` are `protected override`, while `OnMissionBehaviorInitialize`
  is `public override` (`Harness/SubModule.cs:16,24,33,42,51`).
- Ordering the harness relies on: `OnSubModuleLoad` (initial payload load) → 
  `OnBeforeInitialModuleScreenSetAsRoot` (module screen) → `OnGameStart`; `OnApplicationTick` drives
  the per-frame reload check and payload tick. The payload load is retried at the module-screen and
  game-start points (`Harness/SubModule.cs:16-58` with `Harness/HotReload.cs:117-131`).
- **Assembly load order across Bannerlord modules is not guaranteed** — a mod can be loaded whose
  companion mod's assembly is not yet in the AppDomain. Anything that needs another module must expose
  an idempotent `Apply` that is retried at `OnBeforeInitialModuleScreen` and latches once it succeeds
  (`Payload/PayloadEntry.cs:115-121`).
- Module **list order** is significant and is controlled by the launcher's list: this guard must be
  ticked **after** BannerlordTogether. `<DependedModule Optional="true">` alone does not enforce it
  (`install.cmd:71`; `SubModule.xml:13`).
- A module's entry point is named by `SubModuleClassType` — here `BLTDeploymentCrashGuard.SubModule`
  inside `BLTDeploymentCrashGuard.dll` — and the `<Assemblies />` element may be **empty**, meaning
  the launcher loads only the `DLLName` assembly and any other assembly must be loaded by the module
  itself at runtime (`SubModule.xml:16-21`).
- BannerlordTogether co-op runs off the **singleplayer** module list, not the multiplayer one — this
  companion declares `SingleplayerModule=true` / `MultiplayerModule=false`, and the installer tells the
  player to tick it "in the Singleplayer mods list" (`SubModule.xml:5-6`; `install.cmd:70-71`).
- `IsTWCompatible=false` — an unsigned third-party Harmony mod must declare itself not
  TaleWorlds-certified (`SubModule.xml:7`).
- Target game version: `Native`, `SandBoxCore` and `Sandbox` are all declared with
  `DependentVersion="v1.4.8"` (`SubModule.xml:10-12`).

### Assembly binding in a Bannerlord process (field-proven 2026-08-21 → 2026-09-01)

- Bannerlord loads module DLLs — this harness, `0Harmony` and BannerlordTogether — via
  `Assembly.LoadFrom`. **LoadFrom-context assemblies are invisible** to the default probing that
  resolves a byte-loaded assembly's references (`Harness/HotReload.cs:56-58`; field-hit 2026-08-21 15:14).
- A process can hold **two** copies of `0Harmony`: the game bin ships **2.4.2.0** in the app base, and
  `Bannerlord.Harmony` module-loads **2.3.6.0** — the copy a module actually binds
  (`Harness/HotReload.cs:146-148,283-287`; `CHANGELOG.md:216-221,274-278`; field-hit 2026-08-29 22:44).
  Harmony is **not** in the game's bin folder; it ships as its own module at
  `<Game>\Modules\Bannerlord.Harmony\bin\Win64_Shipping_Client\0Harmony.dll`, referenced with
  `Private=false` (`Harness/BLTDeploymentCrashGuard.csproj:30-33`;
  `Payload/BLTDeploymentCrashGuard.Payload.csproj:54-57`).
- `Assembly.Load(byte[])` resolves references via **default-context probing**, which *succeeds* by
  finding the game's own 0Harmony 2.4.2.0 in the app base and binds it silently.
  **`AppDomain.AssemblyResolve` never fires**, because probing succeeded — a resolver only helps when
  probing fails. The cure is to change the **load context** (`LoadFrom` from the module directory), not
  to add another resolver (`Harness/HotReload.cs:279-287`; field-hit 2026-08-30 16:00).
- A split assembly-type identity across an interface boundary manifests as
  `Method 'Apply' in PayloadEntry does not have an implementation` — the implementing method's
  parameter type is a **different copy** of the same-named type. **That message means a type-identity
  split, not a missing method** (`Harness/HotReload.cs:59-62,149-150,285-286`).
- `Assembly.LoadFrom` **locks the loaded file for the lifetime of the process**, and its dependency
  probing looks in the **directory of the loaded file** — so a shadow copy must live in the same
  directory as the canonical DLL to keep resolving against the harness
  (`Harness/HotReload.cs:298-302`).
- `LoadFrom` **dedups simple-named assemblies by NAME ONLY** — a unique `AssemblyVersion` is *not*
  enough to force a fresh load (field-proven 2026-09-01 17:37) — and it **caches path → assembly**, so
  re-using the same shadow path after a failed attempt returns the first attempt's assembly without
  ever reading the new file (field-proven 2026-09-01 17:43). A dedup is detectable by comparing the
  returned `Assembly.Location` to the requested full path (OrdinalIgnoreCase)
  (`Harness/HotReload.cs:288-293,307-318`).
- `Assembly.Location` is **empty for a byte-loaded assembly and can throw**; `Assembly.IsDynamic`
  identifies dynamic assemblies that have no usable `Location`
  (`Harness/HotReload.cs:171,209-212,235-245`; `Harness/PayloadCompiler.cs:67-69`).
- `[assembly: InternalsVisibleTo]` is matched by **exact** assembly name, so it cannot cover builds
  whose assembly name carries a per-build stamp (`Payload.b<stamp>`) — the harness API had to be made
  public instead (`Harness/AssemblyInfo.cs:3-9`).
- .NET Framework 4.8 **cannot unload** an assembly, but **can** load a new one via
  `Assembly.Load(bytes)`, and each newly-loaded generation gets **fresh statics** — the whole basis of
  the hot-reload design (`Harness/HotReload.cs:12-14`). Harnesses statics survive payload reloads
  because Bannerlord loads the harness assembly exactly once, which is why the session id, guard fire
  counts, log path/role tag and the shared-state bag live in the harness
  (`Harness/Diag.cs:8-11,32`; `Harness/SelfHealing.cs:28`; `Harness/Log.cs:8-10`;
  `Harness/Contracts.cs:25-30`).
- **Harmony keys patches by owner string**, so a new owner id can `UnpatchAll` a previous owner's
  hooks — that is what makes generational hot-swap of patches possible
  (`Harness/HotReload.cs:14-16,359-360,376`). **Harmony patching is not thread-safe from a background
  (FileSystemWatcher) thread** — it must happen on the game's main thread (`Harness/HotReload.cs:92-93`).
- **ButterLib** loads older `System.Collections.Immutable` / `System.Reflection.Metadata` into the
  process, which bind-conflict with Roslyn inside Bannerlord on .NET Framework 4.8 — Roslyn's `Emit`
  can throw for this reason, which is why the prebuilt-DLL path is primary
  (`Harness/HotReload.cs:21-25`; `Harness/PayloadCompiler.cs:21-23`; `HOTRELOAD.md:46-47`).
- Bannerlord **locks the module DLLs it has loaded** (write-blocked) while the game runs, but the NTFS
  **rename** of a locked-for-write file is still permitted — which is why the installer can update a
  running install by renaming the live DLLs to `.prev` (`install.cmd:49-50`).
- Runtime code loading inside a shipped module is a code-injection surface, hence the double gate
  (config flag **and** a marker file) before any watcher or Roslyn compilation is allowed
  (`Harness/HotReload.cs:27-29`; `Harness/GuardConfig.cs:111`).

### Target framework and the engine assembly split

Bannerlord 1.4.8 runs on **.NET Framework 4.7.2**; both mod assemblies target `net472`, and a mod DLL
must target `net472` to load (`Harness/BLTDeploymentCrashGuard.csproj:6`;
`Payload/BLTDeploymentCrashGuard.Payload.csproj:6`).

The engine assemblies this mod binds: `TaleWorlds.DotNet`, `TaleWorlds.Library`, `TaleWorlds.Core`,
`TaleWorlds.Localization`, `TaleWorlds.ObjectSystem`, `TaleWorlds.CampaignSystem`, `TaleWorlds.Engine`,
`TaleWorlds.MountAndBlade`. The harness needs only four of them (Library, Core, Engine, MountAndBlade)
— the campaign/localization/object-system surface is payload-only
(`Payload/BLTDeploymentCrashGuard.Payload.csproj:58-89`;
`Harness/BLTDeploymentCrashGuard.csproj:34-49`).

**SandBox *view* code is not in the game bin**: `SandBox.View.dll` lives in the SandBox **module**
folder (`<Game>/Modules/SandBox/bin/Win64_Shipping_Client/SandBox.View.dll`), while the
`TaleWorlds.*`, `SandBox.*` and `StoryMode.*` engine assemblies are in
`<Game>/bin/Win64_Shipping_Client` (`tools/il-probes/README.md:30-31`).

The SandBox assembly is **not a compile-time reference** for this mod, so every SandBox type
(`MissionConversationCameraView`, `GauntletClanScreen`, `HideoutAmbushMissionController`,
`SandBox.View.Map.MapScreen`) must be resolved by `AccessTools.TypeByName` with a fully-qualified
string (`Payload/StealthHideoutAdvisor.cs:27`; `Payload/ConversationCameraCrashGuard.cs:23`;
`Payload/ClanScreenCrashGuard.cs:26`).

### Enumerating patch targets by name

- The namespace split is real and matters for `TypeByName` lookups: deployment/mission/VM types live
  under `TaleWorlds.MountAndBlade(.ComponentInterfaces / .ViewModelCollection.OrderOfBattle)`, campaign
  models/behaviors under `TaleWorlds.CampaignSystem(.GameComponents / .MapEvents / .CampaignBehaviors)`,
  and sandbox overrides under `SandBox.GameComponents` / `SandBox.Missions.MissionLogics`
  (`Payload/BattleMode.cs:41-62`).
- `BattleMode.EnumerateTargets` yields **every** method matching a name rather than `First()`, because
  several battle types carry overloads; it filters out **abstract** declarations (an abstract
  declaration cannot be patched — which is why `BattleInitializationModel.CanPlayerSideDeployWithOrderOfBattle`
  is listed alongside its concrete override in `SandboxBattleInitializationModel`); and it uses
  `BindingFlags.DeclaredOnly`, so an implementation inherited and *not* overridden is not enumerated —
  list the declaring type explicitly when that matters
  (`Payload/BattleMode.cs:51-52,59-60,236-244`).
- Game code lives under the `SandBox`, `StoryMode` and `TaleWorlds` namespaces; **`TaleWorlds.Library`
  is framework churn** and must be excluded when picking the "first game frame" of a stack
  (`Payload/CharacterCreationTrace.cs:217-240`).
- **Harmony-patched callers appear in a live stack as dynamic methods with a null `DeclaringType`,
  named `DMD<Namespace.Type::Method>`.** Keeping those frames identifies the original patched method
  that made the call, so another mod's patch can be attributed
  (`Payload/TracePatches.cs:271-278`; same handling at `Payload/ControlTrace.cs:377-380`).

### Threading

`HeroCreator`, `MBObjectManager` and `ItemRoster` mutation are **main-game-thread only** — they must
never run on a network thread; work arriving on BT's network thread has to wait for the main-thread
tick (`Payload/PregnancySync/PregnancySyncGuard.cs:38-39,:327-328`;
`Payload/StashSync/StashSyncGuard.cs:249`). Harmony patching has the same constraint (see
[assembly binding](#assembly-binding-in-a-bannerlord-process-field-proven-2026-08-21--2026-09-01)).

### Strings, ids and networking from inside the game

- Bannerlord `StringId`s and hero names can carry **non-ASCII** characters (accents, CJK), and
  `BodyProperties` xml carries quotes and angle brackets — so any wire format for them must be UTF-8
  and **length-prefixed** rather than delimiter-based (`tests/BirthPayloadTest/Program.cs:46-55`;
  `tests/StashPayloadTest/Program.cs:30-35`).
- Bannerlord runs on .NET Framework-era networking defaults: **TLS 1.2 must be explicitly OR-ed into
  `ServicePointManager.SecurityProtocol`** before an HTTPS POST from inside the game process
  (`Payload/LogStreamer.cs:151`).

### This mod's own runtime contracts

- The dedicated co-op authority process is selected by a command-line contract: the launch argument
  `--coop-authority` (alias `--coop-dedicated-authority`) maps to
  `CoopAuthorityRole.DedicatedGraphicalHost` (`Payload/RoleTrace.cs:9-12,:112-129`).
- The mod's version identity is stamped by MSBuild from `<Version>` in `Directory.Build.props` into
  the assembly identity and read back at runtime as `Major.Minor.Build` — never hardcoded; the log
  banner is produced by `Diag` reading that assembly identity
  (`Harness/Diag.cs:15-30`; `Directory.Build.props:3-6`).
