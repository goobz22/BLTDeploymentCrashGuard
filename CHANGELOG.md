# Changelog

## v1.2.x — fixes added on top of the harness/payload split

New crash guards and root-cause fixes (all self-disabling; health-reported; self-tested):

- **Dead-hero reactivation (issue quests)** — issue "alternative solution" troops returning
  reactivated dead companions (vanilla had no IsAlive check) → NRE on the character-development
  event. Root fixes: strip dead heroes from the returning roster, and block any dead→Active hero
  transition globally.
- **Conversation-camera crash** — `MissionConversationCameraView.MakeSpeakerLookToListener` NRE
  when a conversation agent is removed mid-dialog (e.g. a marriage applying) → finalizer skips the
  camera frame.
- **Marriage — solo clan-mode block** — BT gates marriage on clan-mode sync that never completes
  when hosting alone; report the correct solo clan-mode so marriage works, inert once a peer joins.
- **Marriage — atomic dowry** — BT let the gold apply natively while routing the marriage to host
  validation; a rejected marriage still took your money. The barter now cancels before any gold moves.
- **No death by sickness** (`noSickness`, default on) — block the local hero's old-age illness death
  and cure an in-progress illness; stands down if the standalone NoSickness mod is present.
- **Co-op pregnancy / birth sync** (`pregnancySync`, default off) — host broadcasts births over BT's
  channel; client reconstructs the identical child. Wire format proven by a headless test suite
  (`tests/BirthPayloadTest`); off until validated live with a second player.
- **Loader hardening** — first payload generation loads via `Assembly.LoadFrom` at the canonical
  path (fixes an assembly-identity split that made the payload fail to load in-game); a loud
  on-screen "CRASH GUARD NOT ACTIVE" warning if the payload ever fails to load; log rotation now
  re-checks periodically instead of once per launch.
- **NoSickness module fix (external)** — corrected the standalone NoSickness mod's `SubModule.xml`
  version string that blocked it from loading.

## v1.2.0 — hot-reload architecture (harness/payload split)

- **No-restart iteration (dev only).** Split into a stable **harness** (`BLTDeploymentCrashGuard.dll`,
  the module Bannerlord loads — lifecycle + reload engine) and a hot-reloadable **payload**
  (`BLTDeploymentCrashGuard.Payload.dll` — all guards/fixes/tracers). The engine loads each payload
  generation via `Assembly.Load(bytes)` (fresh statics) under a per-generation Harmony owner id,
  applies the new generation, then `UnpatchAll`'s the previous one — a failed reload keeps the
  previous generation, so the game is never left unpatched.
- Two reload sources: **build-and-drop** the payload DLL (default, dependency-free) or **Roslyn
  edit-.cs** auto-recompile (opt-in `-p:Roslyn=true`, falls back to the DLL if Roslyn bind-conflicts
  on net472). See HOTRELOAD.md.
- Hard-gated dev-only: requires `hotReload: true` AND a `.hotreload-dev` marker file. Players load
  the prebuilt payload once (no watcher, no Roslyn).
- Cross-reload state (guard fire counts, launch session id) persists in a harness-owned shared
  store; per-generation health/self-test lists are reset each reload to avoid duplicates.
- Known gap (Phase B): `BattleMode`'s foreign-patch stash doesn't yet survive a reload; reloading
  in `battleMode=solo` can leave BT battle patches lifted (coop unaffected).

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
