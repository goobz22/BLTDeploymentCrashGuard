# Offering fixes upstream to BannerlordTogether

Several fixes in this companion mod are clean, general, and would benefit every
BannerlordTogether player — not just this setup. They are worth offering to the BT authors
as actual code changes (or, at minimum, precise bug reports with the fix described). This
mod fixes them from the outside via Harmony; inside BT they would be small direct edits.

Ranked by value and how self-contained the fix is.

## 1. Client bootstrap action-cache false-negative (highest value)

**Where:** `CoopSubModule.TryVerifyNativeActionCacheWhenCampaignMapReady`.

**Bug:** The audit compares the engine's *static* `ActionIndexCache` mirror fields against
fresh native lookups. On a client session those mirrors are unprimed (index -1), so the
audit reports a mismatch, logs `BootstrapAborted reason=action-cache-mismatch … restartRequired`,
and sets `_harmonyPatchBootstrapAttempted = true` — which permanently blocks retry. The
whole session then runs with the deferred sync patches unapplied (invisible partner armies,
joins not registering, speed desync). The native catalog is fully loaded at this point
(`actions=5167`, all probe action codes valid, `diskLoad=False`) — only the mirror is stale.
This is a false negative that affects **every client**, and no fresh cache is ever persisted
so `restartRequired` never resolves.

**Fix:** Once the native catalog is confirmed ready (the checks already in this method), prime
the static `ActionIndexCache` mirrors from the live catalog (`ActionIndexCache.Create(name)`
per static field) before/instead of aborting, so the audit passes and the deferred patches
apply. Equivalent to "regenerate the cache from the fresh catalog instead of aborting."

## 2. Client hero-creation home-settlement NRE

**Where:** `DefaultSettlementValueModel.FindMostSuitableHomeSettlement` /
`FindFarthestDistanceBetweenSettlementsInClan`, reached via
`Clan.ResetPlayerHomeAndFactionMidSettlement` during `CharacterCreationContent.ApplyCulture`.

**Bug:** On a client whose faction/settlement graph is still replicating, this dereferences
a null `MapFaction.FactionMidSettlement` and CTDs during culture selection.

**Fix:** Null-guard the mid-settlement/faction access and fall back to a valid home settlement
(the method already returns `InitialHomeSettlement` / first settlement in its own edge cases).

## 3. Half-synced party-AI NREs

**Where:** `MobilePartyAi.GetBehaviors` (DefendSettlement branch) and
`EncounterManager.HandleEncounterForMobileParty`, in the campaign tick.

**Bug:** A party synced piecemeal during a join can briefly hold `DefaultBehavior ==
DefendSettlement` with no target settlement and no target party, which the native code
assumes impossible → NRE → CTD on join.

**Fix:** Skip the AI tick for a party in that specific inconsistent state until sync
completes (it self-heals), and guard the encounter-handling tick.

## 4. Siege deployment CTD

**Where:** `DeploymentMissionController.SetupTeams` / `FinishDeployment` dereference
`Mission.InitialPlayerAgent` with no null check; a co-op battle that starts before the player
agent spawns CTDs. This is the vanilla method, but BT's flow is what reaches it in that state.

**Fix (for BT):** ensure the player agent exists before team setup, or upstream a null guard
to TaleWorlds. The companion mod suppresses the escaping exception as a stopgap.

## Smaller quality-of-life

- **Shared time control default**: `AllowClientTimeControl` ships off; many players expect
  either-player time control (as in 2-player mode). Consider defaulting it on for a single
  gameplay client, or surfacing the toggle more prominently.
- **Surface BootstrapAborted to the player**: it is currently only written to the sync log;
  a player whose session half-loaded has no on-screen signal. A visible "restart required"
  message would save a lot of confused sessions.
- **Dedicated-server port**: the owner window and the spawned authority both bind a hardcoded
  47770; make it configurable so a graphical host + dedicated authority can coexist.

Each item above is reproducible with the logs this companion mod and BT itself produce
(`CrashGuard.log` + `bt-sync-*.txt`), which are ideal to attach to a report.
