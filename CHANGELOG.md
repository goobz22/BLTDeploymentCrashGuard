# Changelog

## v1.1.0 — robustness & troubleshooting

- **Version stamping**: the DLL now carries a real version + build timestamp, logged at
  startup (`===== BLT Deployment Crash Guard v1.1.0 (build …) session=… =====`) so any
  crash report or log identifies exactly which build is running.
- **Startup health summary**: because every hook is by-name reflection, a
  BannerlordTogether update can silently break our patches. Each load-bearing fix now
  reports resolved/missing, and startup logs `MOD HEALTH: N active, …` — with an
  on-screen warning if a core fix failed to resolve (i.e. BT changed and we need updating).
- **Tracers off by default**: the verbose diagnostic tracers (mission/menu/control/time/
  coop-battle/role) now only load with `tracing: true` in guardconfig.json. Normal play is
  lightweight; troubleshooting turns them on.
- **Safe mode**: `safeMode: true` disables every guard/fix/tracer — for isolating whether
  this mod or BT is the cause of an issue.
- **Log rotation**: CrashGuard.log rolls to `.1` past 8 MB (it hit 12 MB in a long session
  and broke streaming). Per-launch session id stamped for separating runs.
- **Fully-documented guardconfig.json**: auto-written with every key explained.
- **collect-diagnostics.cmd**: one command bundles CrashGuard.log + BT's bt-sync-*.txt +
  the newest crash report into a zip and uploads it (link to clipboard).

## v1.0.x — fixes & guards (initial)

- **Siege deployment crash guard**: suppress the `DeploymentMissionController.SetupTeams` /
  `FinishDeployment` NRE that CTD'd every co-op siege.
- **Client bootstrap fix**: BT's client action-cache audit false-negatives on a stale
  static mirror and permanently aborts, leaving the whole client session with sync patches
  unapplied (invisible armies, joins not registering, speed desync). We prime the mirror
  from the live catalog and let verification pass — the root fix for co-op-as-client.
- **BootstrapWatch**: detects BT's silent `BootstrapAborted` and auto-clears the stale
  RuntimeDataCache so the next launch loads cleanly; warns on screen to restart.
- **Client hero-creation guard**: suppress the `FindMostSuitableHomeSettlement` NRE on a
  half-synced clan/faction during culture selection.
- **Party-AI crash guards**: suppress `MobilePartyAi.GetBehaviors` /
  `EncounterManager.HandleEncounterForMobileParty` NREs on half-synced parties (join races).
- **Encounter-loop breaker**: break the infinite encounter-meeting re-open loop.
- **Player-identity guard**: correct the co-op spawn identity swap (playing the other hero).
- **Auto battle mode**: vanilla battles when hosting solo, co-op battle sync when a peer is
  connected — auto-detected, fail-safe toward co-op.
- **Time fixes**: keep fast-forward through map clicks; don't auto-pause on idle
  (`timeAlwaysFlows`); auto-grant shared client time control (`shareTimeControl`) so either
  player controls speed; neutralize stale co-op speed enforcement after an in-game load.
- **Log streaming**: optional auto-upload of the log for remote debugging.

## Known open items (see UPSTREAM_BUG_REPORT.md)

- Dedicated-server role drops to player-host on in-game save load (tracer in place).
- Two clients on a dedicated server form separate battles (per-client-ghost encounters);
  shared-battle lease formation not yet fixed.
- Reactive vs root: player-identity and some crash guards are safety nets, not root fixes.
