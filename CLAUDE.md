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

## Version + release (do ALL of it — it is the release)

`Directory.Build.props` `<Version>` is the **single** source — never version anything else. A build
stamps both assemblies and the **repo-root** `SubModule.xml` only; nothing writes
`dist/SubModule.xml`, so that copy is manual — the easiest to forget.

**Pushing == releasing**: `install.cmd` pulls `dist/`, and the README one-liners curl `install.cmd`,
`share-log.cmd`, `collect-diagnostics.cmd` from the **repo root of `main`** — release artifacts, not
tooling. Edit **all three**: each has its own Steam-path list and they have drifted (11 entries in
`install.cmd:15-25` and `share-log.cmd:14-24`, 6 in `collect-diagnostics.cmd:14-19`).

```bash
cd Harness && dotnet build -c Release && cd ../Payload && dotnet build -c Release
```

Deploy both DLLs + `SubModule.xml` to the game module — `<Steam>/…/Mount & Blade II
Bannerlord/Modules/BLTDeploymentCrashGuard/` (`install.cmd:41-42`), DLLs in its
`bin/Win64_Shipping_Client/`, XML at its root — **and** to `dist/`, then `md5sum` all three. The
game's own `bin/Win64_Shipping_Client` is only the csprojs' `GameBin` reference dir, never a deploy
target. **Nothing cross-checks the harness/payload pair** (the installer curls each file separately;
the payload's `AssemblyVersion` is a per-build wildcard), so a half-updated `dist/` ships a mismatch.

**Docs ship with the binary**, same commit: a `CHANGELOG.md` entry under the new `<Version>` (bug,
cause, fix); a numbered README fix entry if player-visible — a crash the previous build still hits
**must** be listed, the only thing telling players to update; README rows for a new config key and
log tag; `docs/ENGINE-NOTES.md` for a newly IL-proven fact; a `docs/FIX-REFERENCE.md` row.
`MovementOrderTypeInitGuard` shipped in v1.3.2 with no `CHANGELOG.md` entry (README #9, `[MO-INIT]`,
ENGINE-NOTES and FIX-REFERENCE all landed) — the changelog gets forgotten.

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
  reporting `Diag.Report(component, ok, detail, critical: false)` (`critical: true` only for a
  load-bearing fix — it warns on-screen too), registering `SelfHealing.RegisterTest`, owning a log
  **tag**; per-mission state resets in `OnMissionInit`.
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
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs:390`), never hand-rolled
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
- A load-time fix goes **first** in `PayloadEntry.Apply`, before `PatchAll`. Older components report
  no health at all (attribute-based deployment guards, `BattleMode`/`PeerDetection`, `PayloadEntry`,
  `PlayerIdentityGuard`, `BootstrapWatch`…) — `docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does not
  cover*; wire new code up instead. Top gap left: a self-test resolving each `BattleMode` target type
  and reporting the count (`EnumerateTargets` skips an unresolvable one with a bare `continue`,
  `Payload/BattleMode.cs:227-234`). `PlayerIdentityGuard` is the known reset-convention exception:
  no `OnMissionInit`, it resets in `Tick` via `ReferenceEquals(Mission.Current, _lastMission)`
  (`Payload/PlayerIdentityGuard.cs:29,49-51`).
- **Hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes (`TimeEnforcementGuard`,
  `MapClickSpeedKeeper`, `TimeTrace` when tracing) and Harmony runs every one even when another
  returns false — read all three before adding a fourth. `[TIME]` reports whether the write survived
  and the resulting mode (`Payload/TimeTrace.cs:113-119`), never which prefix won — identify that
  from their own tags (`[TIME-GUARD]`, `[CLICK-SPEED]`).

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
`tools/il-probes/` IL/reflection probes · `dist/` the three shipped artifacts.

Docs: `README.md` (player-facing), `CHANGELOG.md`, `HOTRELOAD.md`, and in `docs/`:
`DIAGNOSTICS.md`, `ENGINE-NOTES.md`, `BT-INTERNALS.md`, `FIX-REFERENCE.md`, `MODDING-GUIDE.md`,
`MODDING-PITFALLS.md`, `SPEC-pregnancy-coop-sync.md`, `UPSTREAM_CONTRIBUTION.md`, plus root
`UPSTREAM_BUG_REPORT.md` — which fact goes where is the DOC_MAP table in
`.claude/rules/blt-docs-tools.md`, one of the path-scoped rules auto-loading with their files.
`.claude/skills/investigate-crash/SKILL.md`: any crash, freeze or pasted log.
