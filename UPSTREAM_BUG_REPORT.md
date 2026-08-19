# Bug report for BannerlordTogether authors

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
