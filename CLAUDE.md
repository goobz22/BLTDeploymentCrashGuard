# CLAUDE.md — BLT Deployment Crash Guard

Operating guide; the named docs hold detail.

Matthew Goluba's companion mod (`goobz22/BLTDeploymentCrashGuard`, branch `main`) for **Mount &
Blade II: Bannerlord**, fixing crashes and co-op bugs in the **BannerlordTogether (BT)** mod via
Harmony patches and by-name reflection into game and BT. He plays co-op with Noah as **Thavin**.

## Two assemblies

**Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — the module Bannerlord loads: lifecycle,
`Log`, health/self-test, `GuardConfig`, hot-reload engine; **locked while the game runs**: changes
need a restart. **Payload** (`Payload/` → `…Payload.dll`) — every guard, fix and tracer,
hot-reloadable, wired by `PayloadEntry.Apply`; ~all iteration is here. `SubModule.xml` points at the
harness, which loads the payload (`HOTRELOAD.md`).

## Version + release

**The checklist lives in `docs/RELEASE.md` — follow it, do not improvise one here.** In short:
`Directory.Build.props` `<Version>` is the **single** version source; `tools/release.sh` builds both
assemblies, deploys the three files to the game module **and** `dist/`, writes `dist/manifest.txt`
(version + a SHA256 per file) and refuses to call the tree release-ready unless every copy
hash-matches; `install.cmd` re-verifies those hashes on the player's machine with `certutil`, so a
half-updated `dist/` is caught instead of shipping a mismatched pair. `tools/lint-scripts.sh` guards
the three player-facing `.cmd` scripts against drift. **Pushing `dist/` == releasing.** Docs ship in
the same commit (CHANGELOG, README item, FIX-REFERENCE row); `docs/RELEASE.md` enumerates them.

**While the game runs**: deploy the payload only (`[HOTRELOAD] gen2`); harness and load-time fixes
need a fresh launch.

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** Where a symptom manifests is not the
root cause — find both. Playbook: `docs/DIAGNOSTICS.md`, from its triage checklist. Static: the IL
probes in `tools/il-probes/` (`NameSearch`, `Inspect`, `IlDump`, `Callers`, `VerCheck`) read
installed assemblies without a decompiler. Runtime: `"tracing": true` in `guardconfig.json`,
reproduce, read `CrashGuard.log` — the session-wide first-chance capture logs even swallowed
exceptions with the inner chain and the **live** stack. A failed type initializer is **cached**: a
logged throw can be a re-throw. Read `ENGINE-NOTES.md` / `BT-INTERNALS.md` / `MODDING-PITFALLS.md`
first; add what you prove.

## Conventions for guards/fixes

- Static class, `Apply(Harmony)`, **idempotent** (`if (_applied) return;` — hot-reload re-applies),
  reporting `Diag.Report(component, ok, detail, critical: false)`, registering
  `SelfHealing.RegisterTest`, owning a log **tag**; per-mission state resets in `OnMissionInit`.
- **`critical: true` is earned, not decorative.** Use it only when the fix's absence re-exposes a
  **crash-to-desktop** or makes **battles unplayable** — it puts a warning on the player's screen,
  so a merely degraded feature must not claim it. The complete current set of `critical:` call sites:
  `deployment-guards` (`Payload/DeploymentCrashGuards.cs:42,48`), `movementorder-typeinit`
  (`Payload/MovementOrderTypeInitGuard.cs:65,78,85,92`), `client-bootstrap-fix`
  (`Payload/ClientBootstrapFix.cs:71,78`), `bg-tick-budget-guard` when
  `TryBackgroundCampaignTick` is unresolved (`Payload/BackgroundTickBudgetGuard.cs:66` — a BT rename
  there re-exposes the co-op background-tick freeze), and `battle-mode` when a chokepoint hook is
  missing or `Apply` itself throws (`Payload/BattleMode.cs:130,136`; an unresolved lift target
  degrades and is not critical). Adding or removing one updates this list in the same commit.
- **Id naming**: kebab-case; the `Diag.Report` component id **is** the `SelfHealing.RecordFire` id;
  the self-test is `"<component>.contract"` (`.loopback` / `.wiring` for the pipeline suites). One
  documented exception — the two deployment finalizers fire as `setup-teams-guard` /
  `finish-deployment-guard` under the single `deployment-guards` component.
- **A decision point is hooked by the guard that owns it, never by a tracer.** `TracePatches` is
  log-only (`Payload/TracePatches.cs:88`); behaviour must not depend on `"tracing"`. This was a real
  bug twice: with tracing off, `BattleMode`'s `StartBattle`/`OpenNew` decisions and
  `EncounterLoopGuard`'s `Finish` stamp never ran (`Payload/EncounterLoopGuard.cs:24-26`).
- **Every `Apply` exit path reports**, "target type not found" included: a silent return is missing
  from `MOD HEALTH:`, reading as *not built*. A config-disabled guard logs the vanilla consequence
  and reports `(component, true, "disabled by config")` — off on purpose is healthy. Tracers are
  exempt: their load line *is* their health report, so print the resolved count.
- **Fail open** — wrap every patch body; the catch returns the vanilla-preserving value (`return;` /
  `return true` / `return __exception`), `return null` from a finalizer only for a deliberate
  suppressor. A depth counter decrements in a **finalizer**, never a postfix. Reflect (`AccessTools`)
  so a game/BT update degrades, not crashes.
- Player-visible change: `Log.Screen` **once per mission** beside the detailed `Log.Info`;
  main-thread follow-up goes in an `internal static void Tick()` pumped from `PayloadEntry.Tick` —
  never UI work from the patched call stack.
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs:618`), never hand-rolled
  reflection: `IsClient`, `AnyRemotePeerConnected`, `ReadCoopStaticBool/String`, `FindCoopType`,
  `Snapshot` — all tri-state, `null` = *could not read*, so **fail toward co-op**
  (`AnyRemotePeerConnected() != false`): a wrong "alone" sabotages a live session. A battle
  chokepoint calls `BattleMode.DecideAndApply(PayloadEntry.Harmony, "<reason>")` — the live
  generation's Harmony, never a captured one.
- **A `GuardConfig` knob is a launch-time snapshot** (the file is cached behind a `_loaded` latch)
  and cannot be hot-reloaded; one that must flip mid-session reads fresh from `GuardConfig.Path` like
  `PayloadEntry.FreshTracingFlag()` — say so in its `_<key>` doc string.
- A **wire/sync** feature mirrors `Payload/StashSync/` and `Payload/PregnancySync/`: engine-free
  model files linked into `tests/*PayloadTest` (never copied), own 4-byte magic on the shared `0x00`
  marker, self-test registered before the enabled check, parse-only receive prefix feeding a `Tick`
  queue, `CampaignEvents` in `OnGameStart`, BT types resolved through a candidate-name list
  (`BannerlordTogether.Network.*`, then legacy — `StashSync/StashSyncGuard.cs:120`), reporting
  `DEGRADED`, never throwing.
- A load-time fix goes **first** in `PayloadEntry.Apply`, before `PatchAll`. Some components still
  report no health — `PlayerIdentityGuard`, `BootstrapWatch`, the time guards, `PeerDetection`,
  `PayloadEntry` never do; others go absent *conditionally*, on the early return that precedes their
  `Diag.Report` (`Payload/BackgroundTickBudgetGuard.cs:57-61`,
  `Payload/JoinSyncPauseEscape.cs:69-73` when BT is not loaded;
  `Payload/StealthHideoutAdvisor.cs:37-40` on an older game build). The table in
  `docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does not cover* is the maintained list; wire new code
  up instead of joining it. `PlayerIdentityGuard` is the known reset-convention exception: no
  `OnMissionInit`, it resets in `Tick` via `ReferenceEquals(Mission.Current, _lastMission)`
  (`Payload/PlayerIdentityGuard.cs:29,49-51`).
- **Hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes (`TimeEnforcementGuard`,
  `MapClickSpeedKeeper`, `TimeTrace` when tracing) and Harmony runs every one even when another
  returns false — read all three before adding a fourth. `[TIME]` now **names** the vetoer
  (`change SUPPRESSED/ALTERED by [TIME-GUARD]` / `[CLICK-SPEED]` / `another patch (not one of ours)`,
  `Payload/TimeTrace.cs:118-124`) because each vetoing prefix calls `TimeVeto.Note(...)`. A fourth
  prefix must call it too, or that line will misattribute the veto.

## Working discipline (house rules, some hook-enforced)

**Do not push mid-investigation** — a push releases to players; iterate on a local deploy; push
when the fix is proven and the owner says so. **git**: never `reset` / `checkout -- <path>` /
`restore` / `stash` / `clean` / `revert` (discard family); `env -u GIT_DIR git -C "<dir>"`;
multi-line messages via `git commit -F <file>` (the hook greps commit bodies — reword prose
containing "stash"); keep the `Co-Authored-By` trailer. **No PowerShell** (bun/git/dotnet and the
Bash tool only). **Never kill the owner's game or Chrome process** — read logs, don't force-close.
Scratch goes in the session scratchpad, not the repo (`tools/` excepted).

## Map of the repo

One guard/fix/tracer per `Payload/*.cs` file, header explaining bug + fix ·
`tests/BirthPayloadTest`, `tests/StashPayloadTest` headless wire-format suites ·
`tools/il-probes/` IL/reflection probes, `tools/release.sh` + `tools/lint-scripts.sh` ·
`dist/` the three shipped artifacts + `manifest.txt`.

Docs: `README.md` (player-facing), `CHANGELOG.md`, `HOTRELOAD.md`, and in `docs/`: `RELEASE.md`,
`DIAGNOSTICS.md`, `ENGINE-NOTES.md`, `BT-INTERNALS.md`, `FIX-REFERENCE.md`, `MODDING-GUIDE.md`,
`MODDING-PITFALLS.md`, `SPEC-pregnancy-coop-sync.md`, `UPSTREAM_CONTRIBUTION.md`, plus root
`UPSTREAM_BUG_REPORT.md` — which fact goes where is the DOC_MAP table in
`.claude/rules/blt-docs-tools.md`, one of the path-scoped rules auto-loading with their files.
`.claude/skills/investigate-crash/SKILL.md`: any crash, freeze or pasted log.
