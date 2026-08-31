# Changelog

## v1.2.7 — shared-save host handoff: you always load as YOUR hero (2026-08-30)

- **Field report: "when Noah saves as host and I load our co-op, it loads me as his hero"**
  — a save stores exactly one player identity (MainHero at save time), and BT's identity
  registry (slots/steam/password claims) is only consulted on the CLIENT join flow —
  nothing fixes the identity of the person LOADING the save (verified by assembly scan;
  SharedSaveMode is a bare flag). New `CoopHeroIdentityLock`: a per-machine
  campaign→hero map (`hero-identity.json`); on load as host/solo the player is switched
  back to this machine's hero via vanilla's `ChangePlayerCharacterAction` (the
  succession mechanism), with an on-screen note naming who the save was last played as.
  New campaigns record automatically; existing shared campaigns are claimed once with
  `"myHero": "YourHeroName"` in guardconfig.json; the record follows death-succession.
  Never runs as a BT client (BT assigns that hero). Hosting can now pass back and forth
  on one save with each player always resuming their own hero.

## v1.2.6 — gates get their F Close/Open back, in sieges and settlement visits (2026-08-30)

- **SIEGE: "defending, the gate is open, no F to close it"** (the field report) —
  root-caused in the installed build's IL: `CastleGate.ServerTick` activates the gate's
  standing points only when the door's animation parameter is EXACTLY >= 1.0; anything
  less deactivates every point. Vanilla itself parks a closed gate at a FROZEN 0.99
  (`SetInitialStateOfGate`), and an opened door can settle a float-hair under 1.0 — in
  both cases the gate is visually at rest but permanently un-interactable. Fix: when the
  parameter is in [0.98, 1.0), apply vanilla's own direction rule (open gate -> close
  points active, closed gate -> open points active). Mid-swing doors and machine-level
  deactivation stay vanilla. A ram-DESTROYED gate is untouched — broken gates cannot be
  closed by design — but with tracing on the log now says so explicitly.
- **SETTLEMENT VISITS**: civilian (walk-around) missions call
  `CastleGate.OpenDoorAndDisableGateForCivilianMission`; `SetInitialStateOfGate` then
  force-opens the door and `SetDisabled(true)`s the entire gate machine (every standing
  point with it — no prompt, and `CloseDoor()` itself early-outs on `IsDisabled`), and
  the usable team is set to `Mission.DefenderTeam`, which never equals the player's
  team in a civilian mission (`StandingPointWithTeamLimit` requires equality). Fix:
  postfix on `AfterMissionStart` for civilian gates only — re-enable the gate and its
  standing points and set the usable team to the player's team. Closing/opening runs
  vanilla's own `CloseDoor`/`OpenDoor` (animation, nav-mesh, colliders). Siege/battle
  gates untouched; a tick finalizer insures against any siege-only assumption now that
  civilian gates tick.

## v1.2.5 — pregnancy works while waiting with your spouse; sync on by default (2026-08-30)

- **Verified against the installed build's IL** (operator ask: "make sure waiting at the
  castle with my wife can get her pregnant, vanilla and co-op"): vanilla's daily roll
  (`PregnancyCampaignBehavior.RefreshSpouseVisit`) fires when `CheckAreNearby` passes —
  same settlement (waiting inside the castle counts; the party's `CurrentSettlement` is
  that castle) or same party; ages 18–45; chance falls with age and existing children.
  In co-op, BT's suppression is literally `return !IsClient` — the HOST's rolls run
  untouched. No behavior change was needed; what was missing:
- **`pregnancySync` now defaults ON** so the resulting birth reaches the other machine
  (it was off pending live validation; the wire format and loopback are proven, the
  two-machine hop gets validated the first time it fires).
- **Conception is now observable**: every conception logs `[PREG] conception: …`, the
  player clan's gets an on-screen note, and with `tracing` on, each daily
  spouse-proximity check for the player clan logs whether the couple counted as
  together and where each of them was — so "did waiting next to her count?" is
  answerable from the log instead of by vibes.

## v1.2.4 — co-op shared settlement stash (2026-08-30)

- **Settlement stashes are now shared between co-op players** (`stashSync`, default on).
  BannerlordTogether has no stash sync at all (assembly scan: zero stash-named members —
  it syncs the workshop warehouse but not the stash), so a deposit existed only on the
  machine that made it and a client's deposits silently diverged from the authoritative
  host. Now closing a stash screen broadcasts that settlement's full stash roster over
  BT's own channel (the same transport pregnancy-sync uses; new "BTCS" frame, provably
  non-colliding with births and with all 255 BT packet types); every other machine
  applies it, the host relays client updates so all peers converge, applying defers
  while that stash is open locally, and the last-closed screen wins a simultaneous
  edit. Same-clan players therefore share one stash — deposit on one machine, withdraw
  on the other. Wire format proven by a headless suite (`tests/StashPayloadTest`).
  Player-crafted items can't be expressed on the wire, so crafted stacks are
  machine-local: excluded from snapshots AND preserved through applies — never deleted
  (the commit review caught that a naive snapshot-apply would silently wipe a crafted
  item the peer's snapshot structurally couldn't mention; crafted replication is
  recorded as an upstream item). Corrupt packets (zero/negative counts, empty ids)
  are rejected on parse.

## v1.2.3 — hot-reload engine: LoadFrom every generation (2026-08-30)

- **Mid-session payload reload fixed at the root** (field-failed again 2026-08-30 16:00 —
  thanks to the new DIAG output the failure finally told the whole story): byte-loading a
  generation (`Assembly.Load(bytes)`) resolves its references via DEFAULT-context probing,
  which finds the game's own `0Harmony 2.4.2.0` in the app base and binds it silently —
  `AssemblyResolve` never fires (probing succeeds), so the 2026-08-30 resolver pin could
  never help — and the `Harmony` type identity splits across `IPayload.Apply`
  (`Method 'Apply' … does not have an implementation`). Gen1 always worked because
  `LoadFrom`-context probing sees the module-loaded `0Harmony 2.3.6.0` the harness is bound
  to. Now EVERY generation loads via `LoadFrom` on a per-process, per-generation shadow
  copy; `LoadFrom`'s identity dedup (which byte-load existed to dodge) is defeated by
  stamping each payload BUILD with a unique `AssemblyVersion` revision (verified: two
  consecutive builds stamp distinct revisions), and a dedup, should one still happen, is
  detected by location mismatch and falls back loudly instead of re-applying old code.

## v1.2.2 — background-tick freeze guard (2026-08-30)

- **Whole-game freeze during host battles fixed** (field hang 2026-08-30 15:24: a third army
  joined the player's battle as the mission started; the game froze with all 16 cores pegged
  for 10+ minutes; root-caused by live debugger attach — repeated stack samples all landed
  inside BT's `CoopSubModule.TryBackgroundCampaignTick` → `Campaign.RealTick/Tick`).
  BT ticks the campaign on EVERY application tick while the host is in a mission, with no
  time budget; a pathologically expensive campaign tick (encounter-hold churn for the
  joining army + hourly-AI catch-up) therefore starves rendering, input, and the mission
  itself, indefinitely. New `[TICK-GUARD]`: equal-time throttle — a background tick that
  blows the 100 ms budget pauses background ticking for as long as it took (capped 10 s), so
  the foreground always keeps ~half of wall time. Not a disable: the co-op background world
  keeps running, just never at the game's expense. Inert under normal load; fires are
  counted and logged for the upstream report.

## v1.2.1 — map-incident siege crash + single-source version (2026-08-30)

- **Map-incident siege-progress CTD fixed at the root** (field crash 2026-08-30 15:04,
  crashreport1.html): confirming an incident option NREs inside vanilla's
  `IncidentEffect.SiegeProgressChange` consequence lambda — it dereferences
  `PlayerSiege.PlayerSiegeEvent…SiegePreparations` with no null check. Probing the installed
  build showed `PlayerSiegeEvent` is a computed getter over `MainParty.SiegeEvent` /
  `CurrentSettlement.SiegeEvent`, so a null means the player's own party isn't attached to any
  siege. Two-branch fix, no feature loss in either mode:
  - **co-op army attach gap** (party rides a besieging army but was never attached to the
    besieger camp): the guard finds the army's live siege and applies the exact vanilla
    effect to it — same `SetProgress`, same report text — so co-op keeps the full incident;
  - **siege genuinely over** (pure-vanilla repro: popup open while the siege ends): the
    effect reports "The siege has already ended." instead of crashing.
  Patch targeting is by IL inspection (only lambdas that call `get_PlayerSiegeEvent`), so the
  harmless preview lambda stays untouched and compiler renumbering can't break it. Class
  safety nets on `IncidentEffect.Consequence` and `Incident.InvokeOption` turn any OTHER
  stale-state incident throw into a logged skip (each fire is a root-fix candidate).
- **One source of truth for the version**: `Directory.Build.props` holds the only version
  number — MSBuild stamps both assemblies from it, the log banner reads the assembly
  identity at runtime, and a build target pokes `SubModule.xml` (which had drifted to
  v1.0.0) in lockstep.

## v1.2.x — installer + hot-reload fixes (2026-08-30)

- **Installer shipped a pre-split build** — `dist/` still held the v1.1 monolithic DLL and
  `install.cmd` only downloaded the harness, so anyone installing from the README one-liner got a
  build without any v1.2.x fix and, on the harness alone, no payload at all. `dist/` now carries
  both assemblies and the installer downloads both (each moved aside as `.prev` if the game holds
  a lock).
- **Hot-reload rejected a mid-session payload** (`TypeLoadException: Method 'Apply' … does not
  have an implementation`, field-hit 2026-08-29 22:44 while trying to enable tracing on a live
  stuck-battle repro). The assembly resolver returned the *first* loaded `0Harmony` copy, and a
  process can hold two (game bin + Bannerlord.Harmony), splitting the `Harmony` type identity that
  `IPayload.Apply(Harmony …)` crosses. The resolver now pins `0Harmony` and the harness to the
  copies the harness itself is bound to and flags any other ambiguous simple name; the failure
  diagnostics print the harness-bound `0Harmony` identity.
- **`tracing` can be enabled by hot-reload** — the payload reads the flag fresh from
  `guardconfig.json` on each generation (the harness caches the file per session), so a
  live repro can be traced without restarting the game.

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
- **Join-hold pause escape** — a joining player's save sync pauses the host for the whole
  download/load/hero-creation, the host's unpause is silently swallowed (it can't clear the
  SaveSync/HeroCreation pause reasons), and a joiner stuck in a retry loop froze the host forever
  (field log 2026-08-22 23:43). Now a swallowed unpause explains the hold on screen; pressing
  pause again within 6 s cancels the stuck join through BT's own transfer-cancel routine.
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
