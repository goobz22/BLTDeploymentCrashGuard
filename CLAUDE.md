# CLAUDE.md — BLT Deployment Crash Guard

Operating guide for an AI agent here; the named docs hold the detail.

A companion mod for **Mount & Blade II: Bannerlord** fixing crashes and co-op bugs in the
**BannerlordTogether (BT)** mod, via Harmony patches and by-name reflection into game and BT.
Matthew Goluba's mod (`goobz22/BLTDeploymentCrashGuard`, branch `main`); he plays co-op with Noah,
his hero is **Thavin**.

## Two assemblies

**Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — the stable module Bannerlord loads:
lifecycle, `Log`, health/self-test, `GuardConfig`, hot-reload engine; **locked while the game runs**,
so changes need a restart. **Payload** (`Payload/` → `…Payload.dll`) — every guard, fix and tracer,
hot-reloadable, wired by `PayloadEntry.Apply`; ~all iteration is here. `SubModule.xml` points at
the harness, which loads the payload (`HOTRELOAD.md`).

## Version + release (do ALL of it — it is the release)

`Directory.Build.props` `<Version>` is the **single** source — never version anything else. A build
stamps both assemblies and pokes the **repo-root** `SubModule.xml` only; nothing writes
`dist/SubModule.xml`, so that copy is manual and the easiest artifact to forget.

**Pushing == releasing**: `install.cmd` pulls `dist/`, and the README one-liners curl `install.cmd`,
`share-log.cmd`, `collect-diagnostics.cmd` from the **repo root of `main`** — release artifacts, not
tooling. Each carries its own Steam-path list and they have drifted (11 entries in
`install.cmd:15-25` and `share-log.cmd:14-24`, 6 in `collect-diagnostics.cmd:14-19`) — edit all three.

```bash
cd Harness && dotnet build -c Release && cd ../Payload && dotnet build -c Release
```

Deploy both DLLs + `SubModule.xml` to the game module (DLLs → `bin/Win64_Shipping_Client/`, XML →
module root) **and** `dist/`, then `md5sum` all three across build output, game module and `dist/`.
**Nothing cross-checks the harness/payload pair** (the installer curls each file separately; the
payload's `AssemblyVersion` is a per-build wildcard), so a half-updated `dist/` ships a mismatch.
The module is `<Steam>/…/Mount & Blade II Bannerlord/Modules/BLTDeploymentCrashGuard/`
(`install.cmd:41-42`); the game's own `bin/Win64_Shipping_Client` is only the csprojs' `GameBin`
reference dir, never a deploy target.

**Docs ship with the binary**, same commit: a `CHANGELOG.md` entry under the new `<Version>` (bug,
cause, fix); a numbered README fix entry if player-visible — a crash the previous build still hits
**must** be listed, the only thing telling players to update; README rows for a new config key and
log tag; `docs/ENGINE-NOTES.md` for a newly IL-proven fact; a `docs/FIX-REFERENCE.md` row.
`MovementOrderTypeInitGuard` shipped in v1.3.2 with no `CHANGELOG.md` entry — README #9, the
`[MO-INIT]` row, the ENGINE-NOTES fact and the FIX-REFERENCE row all landed; the changelog is the
artifact that gets forgotten.

**While the game runs**: deploy the payload only (`[HOTRELOAD] gen2`); harness and load-time fixes
need a fresh launch.

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** Where a symptom manifests is not the
root cause — find both. Playbook: `docs/DIAGNOSTICS.md`, from its triage checklist.

- Static: the IL probes in `tools/il-probes/` (`NameSearch`, `Inspect`, `IlDump`, `Callers`,
  `VerCheck`) read the installed assemblies without a decompiler.
- Runtime: `"tracing": true` in `guardconfig.json`, reproduce, read `CrashGuard.log` — the
  session-wide first-chance capture logs even swallowed exceptions with the inner chain and the
  **live** stack. A failed type initializer is **cached**: a logged throw can be a re-throw.

Engine facts → `docs/ENGINE-NOTES.md`; BT internals → `docs/BT-INTERNALS.md`; what bit us and what
was reverted → `docs/MODDING-PITFALLS.md`. Read them first; add what you prove.

## Conventions for guards/fixes

- Static class, `Apply(Harmony)`, **idempotent** (`if (_applied) return;` — hot-reload re-applies),
  reporting `Diag.Report(component, ok, detail, critical: false)` (`critical: true` only for a
  load-bearing fix — it also warns on-screen), registering `SelfHealing.RegisterTest` and owning a
  log **tag**; per-mission state resets in `OnMissionInit`.
- **Every `Apply` exit path reports**, "target type not found" included: a silent return is missing
  from `MOD HEALTH:`, reading as *not built*. A guard disabled by config logs the vanilla
  consequence and reports `(component, true, "disabled by config")` — off on purpose is healthy.
  Tracers are exempt: their load line *is* their health report, so print the resolved count.
- **Fail open** — wrap every patch body; the catch returns the vanilla-preserving value (`return;` /
  `return true` / `return __exception`), `return null` from a finalizer only for a deliberate
  suppressor. A depth counter decrements in a **finalizer**, never a postfix. Reflect (`AccessTools`)
  so a game/BT update degrades rather than crashes.
- Player-visible change: `Log.Screen` **once per mission** beside the detailed `Log.Info`.
  Main-thread follow-up goes in an `internal static void Tick()` pumped from `PayloadEntry.Tick` —
  never UI work from the patched call stack.
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs:390`), never hand-rolled
  reflection: `IsClient`, `AnyRemotePeerConnected`, `ReadCoopStaticBool/String`, `FindCoopType`,
  `Snapshot` — all tri-state, `null` = *could not read*, so **fail toward co-op**
  (`AnyRemotePeerConnected() != false`): a wrong "alone" sabotages a live session. A battle chokepoint
  calls `BattleMode.DecideAndApply(PayloadEntry.Harmony, "<reason>")` — the live generation's
  Harmony, never a captured one.
- **A `GuardConfig` knob is a launch-time snapshot** (the file is cached behind a `_loaded` latch)
  and cannot be hot-reloaded; one that must flip mid-session reads fresh from `GuardConfig.Path` like
  `PayloadEntry.FreshTracingFlag()` — say so in its `_<key>` doc string.
- A **wire/sync** feature mirrors `Payload/StashSync/` and `Payload/PregnancySync/`: engine-free
  model files linked into `tests/*PayloadTest` (never copied), own 4-byte magic on the shared `0x00`
  marker, self-test registered before the enabled check, parse-only receive prefix feeding a `Tick`
  queue, `CampaignEvents` in `OnGameStart`, and BT types resolved through a candidate-name list
  (`BannerlordTogether.Network.*` then legacy — `StashSync/StashSyncGuard.cs:120`) that reports
  `DEGRADED` rather than throwing.
- A load-time fix goes **first** in `PayloadEntry.Apply`, before `PatchAll`. Older components report
  no health at all (attribute-based deployment guards, `BattleMode`/`PeerDetection`, `PayloadEntry`,
  `PlayerIdentityGuard`, `BootstrapWatch`…) — `docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does not
  cover*; wire new code up instead. The top instrumentation gap is a self-test resolving each
  `BattleMode` target type and reporting the count — it has no `Diag.Report` and no
  `SelfHealing.RegisterTest`, and `EnumerateTargets` skips an unresolvable type with a bare
  `continue` (`Payload/BattleMode.cs:227-234`). `PlayerIdentityGuard` is the known exception to the
  reset convention: no `OnMissionInit`, it resets in `Tick` via
  `ReferenceEquals(Mission.Current, _lastMission)` (`Payload/PlayerIdentityGuard.cs:29,49-51`).
- **Hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes (`TimeEnforcementGuard`,
  `MapClickSpeedKeeper`, `TimeTrace` when tracing) and Harmony runs every one even when another
  returns false — read all three before adding a fourth. `[TIME]` reports whether the write survived
  and the mode that resulted (`Payload/TimeTrace.cs:113-119`), never which prefix won — identify
  that from their own tags (`[TIME-GUARD]`, `[CLICK-SPEED]`).

## Working discipline (house rules, some hook-enforced)

- **Never guess a root cause** — prove it from IL/logs with a probe; a web result is a lead, not a
  diagnosis. Revert speculative changes when evidence contradicts them; keep the diagnostics.
- **Do not push mid-investigation** — a push releases to players; iterate on a local deploy and push
  once the fix is proven. The owner says when to ship.
- **git**: never `reset` / `checkout -- <path>` / `restore` / `stash` / `clean` / `revert` (discard
  family); `env -u GIT_DIR git -C "<dir>"`; multi-line messages via `git commit -F <file>` (the
  hook greps commit bodies — reword prose containing "stash"); keep the `Co-Authored-By` trailer.
- **No PowerShell** (bun/git/dotnet and the Bash tool only). **Never kill the owner's game or Chrome
  process** — read logs, don't force-close. Scratch stays in the session scratchpad, not the repo
  (durable tools excepted: `tools/`).

## Map of the repo

- `Payload/*.cs` — guards/fixes/tracers (one per file; the header explains bug + fix).
  `Harness/*.cs` — lifecycle, logging, config, reload engine, self-heal.
- `README.md` — player-facing: install, numbered fixes, config, tags, troubleshooting, known issues.
  `CHANGELOG.md` — per-version. `HOTRELOAD.md` — the reload workflow.
- `docs/`: `DIAGNOSTICS.md` (investigate) · `ENGINE-NOTES.md` (engine facts from IL) ·
  `BT-INTERNALS.md` (BT internals from IL) · `FIX-REFERENCE.md` (per-fix table:
  tag/config/scope/patched members/limitations/self-test) · `MODDING-GUIDE.md` (public techniques) ·
  `MODDING-PITFALLS.md` (what bit us; reverted attempts) · `SPEC-pregnancy-coop-sync.md` (the
  birth-sync design spec) · `UPSTREAM_CONTRIBUTION.md` + root `UPSTREAM_BUG_REPORT.md` (BT-side).
- `tools/il-probes/` — IL/reflection probes + README. `tests/BirthPayloadTest`,
  `tests/StashPayloadTest` — headless wire-format suites. `dist/` — the three shipped artifacts.
- `install.cmd`, `share-log.cmd`, `collect-diagnostics.cmd` — installer/log-sharing, served live
  from `main`. `SubModule.xml` (manifest, build-stamped), `Directory.Build.props` (the one
  `<Version>` + stamp target), `NuGet.config` (nuget.org only).
- `.claude/rules/*.md` — path-scoped conventions that auto-load with the files they cover;
  `.claude/skills/investigate-crash/SKILL.md` — invoke on any crash, freeze or pasted log.
