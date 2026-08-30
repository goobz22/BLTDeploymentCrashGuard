# Bug report for BannerlordTogether authors

## HEADLINE — Client sessions permanently half-loaded: BootstrapAborted every time, cache never regenerates

Environment: game 1.4.8.119303 (Steam), BannerlordTogether v0.5.0.1
(commit 035beead876d66fb1e91d7282cd98bc4f624430b), installed via Vortex/Nexus.

Every CLIENT-role session logs (bt-sync-client.txt):

```
[HARMONY] NativeActionCatalogReady source=application-tick actions=5167 animations=6170 ...
          diskLoad=False cachedSentinel=-1 cacheMatchesNative=False cacheMismatches=214
[HARMONY] BootstrapAborted reason=action-cache-mismatch cachedSentinel=-1 nativeSentinel=4008
          ... deferredPatchesApplied=False earlyLifecyclePatchesRemain=True restartRequired=True
```

Evidence that this is unrecoverable on this install:
- Reproduces IDENTICALLY with the shipped RuntimeDataCache .rdc present (2026-08-19
  20:46) and with it removed (21:41) — diskLoad=False and all-(-1) sentinels both ways,
  so the shipped cache (file dated 2026-06-30) never loads for this game build.
- No cache write/persist ever occurs (file date never changes; no persist log lines),
  so restartRequired=True never becomes a working next launch.
- Host/solo sessions do not run the audit and work; every co-op-as-client session ran
  with deferred patches unapplied. Downstream symptoms observed while half-loaded:
  no client hero selection, client sees host-style map shell, client join/encounter
  requests never registered on the authority, partner armies missing from battles,
  speed desync between machines.

Suggested fixes: regenerate/persist the action cache from the fresh catalog instead of
aborting (it is already computed at NativeActionCatalogReady), or ship no cache and
build on first run; at minimum surface the abort to the player in-game — it is
currently silent and the session plays on with broken sync.

Also: the dedicated-server flow's owner window binds port 47770 and the spawned
authority instance then fails `Host network FAILED to bind port=47770 attempt=1/5..5/5`
and self-destructs (2026-08-19 21:29:54) — the two components of the same flow contend
for one hardcoded port.

---

**Title:** Host (solo, LegacyPlayerHost): player-side troops never enter battle missions —
`DeploymentMissionController.SetupTeams` NullReferenceException on every siege; village-raid
battles start with all player formations empty (0/0)

## Environment

- Bannerlord v1.4.8 (Steam), BLSE 1.6.7.356, LauncherEx 1.25.6, Harmony 2.3.6.220, ButterLib 2.11.1.0
- BannerlordTogether v0.5.0.1
- Session: hosting co-op on an existing SP save, **zero clients connected** (solo host),
  `DefaultHostingTopology: LegacyPlayerHost`
- Reproduces 100% of the time on sieges; also reproduces on defended village raids

## Symptom 1 — guaranteed CTD on siege (vanilla-code crash, mod-induced state)

Community crash report points at `BannerlordTogether` /
`SpNativeDeploymentReadyGateTickPatch` on `DeploymentMissionController.OnMissionTick`:

```
System.NullReferenceException: Object reference not set to an instance of an object.
at TaleWorlds.MountAndBlade.DeploymentMissionController.SetupTeams()
at TaleWorlds.MountAndBlade.DeploymentMissionController.OnMissionTick_Patch1(...)
```

Native `SetupTeams()` dereferences `Mission.InitialPlayerAgent` without a null check.
`Mission._initialPlayerAgent` is only assigned when an agent is built with
`Controller == AgentControllerType.Player` — i.e. the player-side spawn during team setup
must produce the player agent. Under BannerlordTogether (host, solo) it does not (see
Symptom 2), so the vanilla line crashes. Vanilla itself can never hit this because the
native spawn path always creates the player agent during `OnSetupTeamsOfSide(PlayerSide)`.

## Symptom 2 — battle roster contains no player-side troops (the actual root cause)

Repro: host solo → village with defenders → Take a hostile action → Raid the village →
encounter menu → battle mission opens. Timeline captured with an external log-only Harmony
tracer on **native** methods (timestamps 2026-08-18):

```
23:03:04  GameMenu.SwitchToMenu(village_hostile_action)
23:03:06  GameMenu.SwitchToMenu(encounter); PlayerEncounter.StartBattle
23:03:08  MissionState.OpenNew("Battle", ...)          ← correct so far
23:03:11  Mission scene ready; Mission.InitialPlayerAgent == null
   ...    (external guard held team setup 90s waiting for a player agent — none ever spawned)
23:04:41  SetupTeams() ran → NullReferenceException (suppressed by external guard)
```

Result in-game: order-of-battle screen shows **every player formation 0/0 /
"Formation is currently empty"** — the player has 90+ healthy archers (~105-member party,
not wounded on the map screen). Raid loot ticks continue on the map layer meanwhile
("You plundered ..."), so the map-event side of the raid runs; only the mission-side
troop roster/spawn for the player side is empty.

Same underlying failure produces the Symptom-1 siege CTD (there the missing player agent
crashes `SetupTeams` directly).

`EnableVerboseLogging` has been enabled; the mod's own sync log for the repro session can
be provided on request.

## Expectation

When hosting with zero connected clients, battles should behave as vanilla: player +
party troops rostered and spawned during deployment team setup.

## Workaround in use

A local companion Harmony mod patches **native TaleWorlds methods only**: finalizers on
`DeploymentMissionController.SetupTeams` / `FinishDeployment` suppress the escaping NRE
(with best-effort completion of `FinishDeployment`'s tail), so the CTD is gone — but
battles remain unplayable solo because the player side still spawns empty.

---

## Siege state and map incidents on co-op peers (analysis from the 2026-08-30 incident CTD)

Game 1.4.8 added map incidents (`TaleWorlds.CampaignSystem.Incidents`). Their siege effects
run through `PlayerSiege.PlayerSiegeEvent`, which is DERIVED per-process from
`MobileParty.MainParty.SiegeEvent ?? MainParty.CurrentSettlement?.SiegeEvent` (verified by IL
inspection of the installed build). Two co-op consequences worth fixing in BT:

1. **Army-siege attach**: if a peer's `MainParty` rides in a besieging army without being
   attached to the army's `BesiegerCamp`, every `PlayerSiege`-derived code path on that peer
   reads null while the siege is live — vanilla's incident consequence then NREs (CTD; see
   crashreport1.html, 2026-08-30 15:04). Our mod v1.2.1 repairs this case at the effect site
   (applies the effect to the army's real siege), and logs
   `[INCIDENT-GUARD] REPAIRED … (co-op army attach gap)` whenever it happens — those log
   lines are the field evidence that the attach path needs a BT-side fix.
2. **Incidents are not synced**: incidents spawn and resolve locally per peer; an incident's
   world effects (e.g. siege progress) apply only on the confirming peer's process. Needs a
   sync/authority decision in BT like other campaign actions.

---

## TryBackgroundCampaignTick has no time budget — host freeze when a campaign tick gets expensive (2026-08-30)

Field hang 2026-08-30 ~15:24 (host, solo at that moment): the instant a battle mission
finished deployment, the game froze for 10+ minutes with all cores pegged; a third army was
joining the same map event. Live debugger attach (repeated managed stack samples of the main
thread) shows every sample inside:

```
BannerlordTogether.CoopSubModule.OnApplicationTick
  -> TryBackgroundCampaignTick -> (reflection) Campaign.RealTick / Campaign.Tick
     -> EncounterManager.HandleEncounters
        -> SuppressClientMirroredPartyHandleEncounterPatch.Prefix (String.Concat per call)
     -> BattleSyncBehavior.CanApplyEncounterHoldThirdPartyCooldownCandidate (via obfuscated wrappers)
     -> AiEngagePartyBehavior.AiHourlyTick -> FactionManager.IsAtWarAgainstFaction
```

`ShouldBackgroundTick` enables this whenever a MapState is in the stack under a non-map
active state (i.e., the host is in a mission), and `TryBackgroundCampaignTick` runs a full
`Campaign.RealTick + Tick` per application tick with no time budget. When one campaign tick
becomes multi-second (here: encounter-hold re-evaluation for an army joining the mission's
own map event, plus hourly-AI catch-up), every frame drowns and the game appears frozen —
the exception/cooldown machinery never helps because nothing throws.

Suggested fix: bound the per-frame cost (budget + backoff, or move the background tick off
the render-critical path); also `SuppressClientMirroredPartyHandleEncounterPatch.Prefix`
builds a log string on every invocation — under this churn that alone is significant.

Our mod v1.2.2 ships an equal-time throttle on `TryBackgroundCampaignTick` (100 ms budget,
pause == elapsed, capped 10 s) and logs `[TICK-GUARD]` with the measured cost each time it
fires — those lines are field evidence of how often/expensive this gets.
