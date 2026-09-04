# Engine notes (hard-won facts)

Facts about Mount & Blade II: Bannerlord and BannerlordTogether (BT) proven from IL during
investigations, so we never re-derive them. Each entry says how it was proven and the date. When
you prove a new one, add it here in the same shape.

---

## MovementOrder is a `beforefieldinit` struct whose init needs a live Mission (2026-09-04)

`TaleWorlds.MountAndBlade.MovementOrder`:

- is a **struct** (`IsValueType == true`, base `System.ValueType`) and **`beforefieldinit`**;
- its `.cctor` builds six template orders (`MovementOrderNull/Charge/Retreat/Stop/Advance/FallBack`)
  by calling `MovementOrder..ctor(MovementOrderEnum)`;
- that instance ctor's one null-capable line is `Mission.Current.CurrentTime`
  (`call Mission::get_Current; callvirt Mission::get_CurrentTime` — `Mission.get_CurrentTime` just
  returns the cached field `_cachedMissionTime`, so the NRE is the `callvirt` on a null
  `Mission.Current`).

Consequence: because the type is `beforefieldinit`, the CLR may run the `.cctor` at **any** point
before first static-field access — including early type preparation triggered by JIT-compiling or
**Harmony-patching a method that merely references the type** (`Formation`, `OrderController`). If
that happens before a mission exists, `Mission.Current` is null → NRE → the type initializer fails
→ .NET **permanently caches the failure**, and every battle for the rest of the process dies at
`Formation.ResetAux` with a `TypeInitializationException`.

This mod's Formation/OrderController patches (added v1.3.0) are what caused the early preparation.

**Fix** (`Payload/MovementOrderTypeInitGuard.cs`, applied *first* in `PayloadEntry.Apply`): a
transpiler rewrites the `Mission.Current.CurrentTime` read to a null-safe helper (returns 0 when no
mission), then forces the `.cctor` to run immediately under the safe ctor, so the type initializes
successfully and is cached good for the whole process. Reusable class: *a beforefieldinit type
whose static init depends on runtime state can be poisoned by our own patching; initialize it
safely at load.*

## Mission load order (2026-09-04)

`MissionState.FinishMissionLoading` calls, in order: `Mission.Tick` → `OnMissionAfterStarting` →
`Mission.AfterStart`. `Mission._current` is set by `Mission.Initialize` (via `Mission.set_Current`),
which runs earlier in `MissionState.OpenNew`. So by the time `AfterStart` adds teams
(`MissionCombatantsLogic.AddPlayerTeam` → `Team.Initialize` → `Formation.Reset` →
`Formation.ResetAux`), `Mission.Current` is already live. This is why a type-init throw logged at
`ResetAux` with `Mission=live` is a cached re-throw, not the origin.

## Time control in co-op (pre-2026-09-04)

BT's `CoopCampaignBehavior.EnforcePlaySpeed` (IL): if host and not paused, it forces
`UnstoppablePlay`/`UnstoppableFastForward` by calling `Campaign.SetTimeControlModeLock(0)` then
`set_TimeControlMode`, **every application tick**. `TimeEnforcementGuard` neutralizes those writes
while no remote peer is connected (solo host). When our guard blocks the write, the mode never
changes, so BT retries every tick — which, with the `[TIME]` tracer on, floods the log unless
coalesced (now handled by `TraceThrottle`). `Mission.get_CurrentTime` returns `_cachedMissionTime`.

> Scoping the solo time-neutralizer to the campaign map (a 2026-09-04 hypothesis for the
> sideways-character bug) was tried and **reverted** — it did not affect that bug. Do not re-add it
> without evidence. The sideways/folded character is a separate, likely GPU-side, vanilla issue.

## Siege defense (IL-proven, 2026-09-03)

Vanilla siege default is **AI control ON**: `BattleDeploymentHandler.SetDefaultFormationOrders`
ends with `SetOrder(IsSiegeBattle||IsSallyOutBattle ? AIControlOn : AIControlOff)`;
`Team.SetPlayerRole` sets all formations `SetControlledByAI(!IsPlayerGeneral)`;
`Formation.set_PlayerOwner(v)` ⇒ `SetControlledByAI(v == null)`; `Formation.RemoveUnit` re-AIs an
emptied formation; the castle-defence tactic marches AI formations and re-shuffles troops via
`Formation.TransferUnits`/`Split`. `FormationClass` 0–7 regular, 8 general, 9 bodyguard.
`SiegeCommandGuard` counters these so placed formations hold. Detail in that file's header.

## BT command model in battle (IL-proven, 2026-09-03)

Host approves a formation for the client only when it holds the client's troops alone
(`IsClientFormationCommandApproved`); approved formations form the client's `AllowedFormationMask`;
the client reports its troops' formations once a second (`SendFormationMembershipSnapshot`) and the
host mirrors them (`ApplyClientFormationMembership` → `ResolveFormationByClass`). Vanilla mixes both
parties' troops by class, so the mask is empty and the client commands nothing. `CoopCommandSplit`
folds host party into formations I–IV and client into V–VIII so each block is pure. Remote player
hero id via BT's session ghost-hero string id.
