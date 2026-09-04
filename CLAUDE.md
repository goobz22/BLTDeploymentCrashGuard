# CLAUDE.md — BLT Deployment Crash Guard

Operating guide for an AI agent in this repo. Read first. Pointers only — the named docs hold detail.

## What this is

A companion mod for **Mount & Blade II: Bannerlord** fixing crashes and co-op bugs in the
**BannerlordTogether (BT)** mod, via Harmony patches and by-name reflection into the game and BT.
Matthew Goluba's mod (GitHub `goobz22/BLTDeploymentCrashGuard`, branch `main`). He plays co-op with
a partner (Noah); his hero is **Thavin**.

## Two-assembly architecture

- **Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — small stable module Bannerlord loads:
  lifecycle, logging (`Log.cs`), health/self-test, config (`GuardConfig.cs`), hot-reload engine.
  Changing it needs a game restart; its DLL is **locked while the game runs**.
- **Payload** (`Payload/` → `BLTDeploymentCrashGuard.Payload.dll`) — every guard, fix, tracer.
  Hot-reloadable; ~all iteration happens here. `PayloadEntry.Apply` wires everything.

`SubModule.xml` points at the harness, which loads the payload. Detail: `HOTRELOAD.md`.

## The one version source

`Directory.Build.props` `<Version>` is the **single** source of truth. A build stamps both assemblies
and pokes the **repo-root** `SubModule.xml` only (`StampSubModuleVersion`,
`XmlInputPath="$(MSBuildThisFileDirectory)SubModule.xml"`). Nothing writes `dist/SubModule.xml` —
that copy is manual and the easiest of the three artifacts to forget. Never version anything else.

## Build & deploy (do ALL of this — it is the release)

`install.cmd` downloads from `dist/`, so **pushing to GitHub == releasing**. `dist/` is not the only
live surface: the README one-liners curl `install.cmd`, `share-log.cmd` and `collect-diagnostics.cmd`
from the **repo root of `main`**, so a push touching those three ships instantly too — release
artifacts, not tooling.

```bash
cd Harness  && dotnet build -c Release
cd ../Payload && dotnet build -c Release
```

Deploy both DLLs + `SubModule.xml` to the game module (DLLs in
`<Game>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/`, XML in the module root) **and**
to `dist/`; then `md5sum` all three across build output, game module and `dist/` in one pass.
Mandatory, because **nothing cross-checks the harness/payload pair** — not the installer (three
independent curls), not load time (the payload's `AssemblyVersion` is a per-build wildcard, so the
two share no comparable identity). Updating one DLL and not the other ships a mismatch with no error.
Game bin: `C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client`.

**Docs are part of the release** — same commit: `CHANGELOG.md` under the new `<Version>` (bug, root
cause, fix); a numbered README fix entry if player-visible — a crash the previous build still hits
**must** be listed, it is the only thing telling players to update; README config-table row for a new
key, tag row for a new log tag; `docs/ENGINE-NOTES.md` for a newly IL-proven fact; a
`docs/FIX-REFERENCE.md` row. Precedent: `MovementOrderTypeInitGuard` (a self-inflicted v1.3.0
regression that killed every battle for the process) shipped in v1.3.2 with neither.

Editing Steam auto-detection means editing **all three** scripts — the path list is copy-pasted into
each and the collector's copy has already drifted.

**While the game runs**: the harness DLL is locked — deploy only the payload (hot-reloads via shadow
copy, `[HOTRELOAD] gen2` in the log). Harness changes and load-time fixes (e.g.
`MovementOrderTypeInitGuard`) need a **fresh launch**.

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** Where a symptom manifests is not the
root cause — find both. Playbook: `docs/DIAGNOSTICS.md` (start at its crash-triage checklist).

- Static: the IL probes in `tools/il-probes/` (`NameSearch`, `Inspect`, `IlDump`, `Callers`,
  `VerCheck`) read the installed assemblies without a decompiler.
- Runtime: `"tracing": true` in `guardconfig.json`, reproduce, read `CrashGuard.log`. A session-wide
  first-chance capture logs swallowed/fatal exceptions with the inner chain, engine-state context,
  memory, and the **live** stack (who triggered it). A failed type initializer is **cached** — a
  logged throw can be a re-throw whose context differs from the origin.

Proven engine facts: `docs/ENGINE-NOTES.md`; BT internals: `docs/BT-INTERNALS.md`; what already bit
us and what was reverted: `docs/MODDING-PITFALLS.md`. Read before diagnosing `MovementOrder`, mission
load order, siege command, time control or the BT command model. Add what you newly prove.

## Conventions for guards/fixes

- Static class with `Apply(Harmony)`, **idempotent** (`if (_applied) return;` — hot-reload
  re-applies). Reports `Diag.Report(component, ok, detail, critical: false)` — `critical: true` only
  for a load-bearing fix, since it also puts a warning on the player's screen. Registers a self-test
  (`SelfHealing.RegisterTest`), logs under its own **tag** (`[SIEGE-CMD]`). Per-mission state resets
  in `OnMissionInit`.
- **Every `Apply` exit path reports**, including "target type not found, nothing to do" — a silent
  return is absent from `MOD HEALTH:` entirely, reading as *not built* rather than *not applicable*.
  A config-disabled guard logs the vanilla consequence and reports
  `Diag.Report(component, true, "disabled by config")`: intentionally off is healthy, not red.
- **Fail open** — wrap every patch body; the catch returns the vanilla-preserving value (`return;` /
  `return true` / `return __exception`). `return null` from a finalizer is only for a deliberate
  suppressor. The decrementing half of a depth counter lives in a **finalizer**, never a postfix.
- A guard that changes what the player sees says so **once per mission** via `Log.Screen` beside the
  detailed `Log.Info`, or the mod is indistinguishable from a game bug.
- Main-thread follow-up goes in an `internal static void Tick()` pumped from `PayloadEntry.Tick`;
  never screen/UI work from inside the patched call stack.
- Resolve game/BT members by reflection (`AccessTools`) so an update degrades rather than crashes; a
  self-test pins the members and the decision logic.
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs:390`), never hand-rolled
  reflection: `IsClient()`, `AnyRemotePeerConnected()`, `ReadCoopStaticBool/String(name)`,
  `FindCoopType(simpleName)`, `Snapshot()`. All tri-state — `null` = *could not read* — so **fail
  toward co-op**: `AnyRemotePeerConnected() != false`, because a wrong "alone" sabotages a live
  session. A new battle chokepoint calls `BattleMode.DecideAndApply(PayloadEntry.Harmony, "<reason>")`
  — always the current generation's Harmony, never a captured one.
- **`GuardConfig` caches the file for the session** (`_loaded` latch), so a knob read through it
  changes only on restart, even with hot-reload on. A mid-session-flippable knob reads fresh from
  `GuardConfig.Path` in the payload as `PayloadEntry.FreshTracingFlag()` does — say so in its
  `_<key>` doc string.
- A **wire/sync** feature also: keeps model + framing in engine-free files linked into
  `tests/*PayloadTest` by `<Compile Include>` (never a copy); takes its own 4-byte magic on the
  shared `0x00` marker and asserts cross-feature discrimination headlessly *and* in an in-game
  loopback; registers that self-test **before** the enabled check so disabled wiring still proves
  out; resolves BT types via the candidate-name list, reporting DEGRADED rather than throwing; does
  only byte parsing in the receive prefix, queueing work for `Tick`; subscribes `CampaignEvents` in
  `OnGameStart` keyed on `Campaign.Current` identity, never in `Apply`.
- A load-time fix (must run before the game touches a type, like `MovementOrderTypeInitGuard`) goes
  **first** in `PayloadEntry.Apply`, before `PatchAll` and the other guards.

**Known exceptions — `MOD HEALTH:` does not cover everything.** `Payload/DeploymentCrashGuards.cs` is
attribute-based (`[HarmonyPatch]` classes via `harmony.PatchAll`) with no `Apply`, report, self-test
or tag, so fix #1 shows only in `GUARD ACTIVITY:`. `BattleMode`/`PeerDetection` and `PayloadEntry`
register neither health nor self-test — nothing pins the `BattleTargets` members, so a BT rename is a
silent `continue`. `PlayerIdentityGuard` and `BootstrapWatch` report nothing, `ClientHeroCreationGuard`
only `RecordFire`, `StealthHideoutAdvisor` returns silently when its type is missing. **Tracers are
deliberately exempt**: a tracer's health report is its load line (`tracer active on N method(s)`,
`type not found: X`) — print the resolved count, since with by-name reflection a silent hook miss is
indistinguishable from "the bug did not happen". Reserve the attribute form for stable native
targets; wire the rest up rather than widen the exception, and keep this list current.

**Coordination hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes —
`TimeEnforcementGuard` (skip-original inside BT's enforcer when solo), `MapClickSpeedKeeper`
(skip-original for one map-click transition) and, with tracing, `TimeTrace` (log-only). Harmony runs
every prefix even when one returns false, so read all three before adding a fourth; a blanket veto
silently defeats the others. The `[TIME]` postfix's `SUPPRESSED/ALTERED by another patch` line shows
which won.

## Working discipline (house rules, some hook-enforced)

- **Never guess a root cause.** Prove it from IL/logs with a probe. A web result is a lead, not a
  diagnosis. Revert speculative changes the moment evidence contradicts them; keep the diagnostics.
- **Do not push mid-investigation** — a push releases to players. Deploy locally, iterate, push once
  a fix is proven. The owner says when to ship.
- **git**: never `git reset` / `checkout -- <path>` / `restore` / `stash` / `clean` / `revert`
  (discard family). Use `env -u GIT_DIR git -C "<dir>"`; multi-line messages via
  `git commit -F <file>` (the destructive-actions hook greps commit bodies and false-positives on
  words like "stash" — reword such prose). End commits with the required `Co-Authored-By` trailer.
- **No PowerShell** — bun/git/dotnet and the Bash tool only.
- **Never kill the owner's game or Chrome process.** Read logs; don't force-close.
- Scratch/probe work goes in the session scratchpad, not the repo — except durable tools (`tools/`).

## Map of the repo

- `Payload/*.cs` — guards/fixes/tracers (one concern per file; header explains bug + fix).
- `Harness/*.cs` — lifecycle, logging, config, reload engine, self-heal.
- `README.md` — player-facing: install, numbered fixes, config, tags, troubleshooting, known issues.
  `CHANGELOG.md` — per-version. `HOTRELOAD.md` — dev reload workflow.
- `docs/DIAGNOSTICS.md` — how to investigate. `docs/ENGINE-NOTES.md` — engine facts proven from IL.
  `docs/BT-INTERNALS.md` — BT internals from IL (unofficial reference).
- `docs/FIX-REFERENCE.md` — per-fix developer table (file/class/tag/config/scope/patched
  members/limitations/self-test) + indexes. `docs/MODDING-GUIDE.md` — public Bannerlord/BT modding
  techniques. `docs/MODDING-PITFALLS.md` — what bit us; reverted attempts and gotchas.
- `UPSTREAM_BUG_REPORT.md`, `docs/UPSTREAM_CONTRIBUTION.md` — BT-side issues and reports.
- `tools/il-probes/` — the IL/reflection probes + their README.
- `install.cmd`, `share-log.cmd`, `collect-diagnostics.cmd` — player-facing installer and log-sharing
  scripts, served live from `main`. `dist/` — the three shipped artifacts (tracked on purpose).
- `SubModule.xml` — module manifest (dependencies, load target), stamped by the build.
  `Directory.Build.props` — the one `<Version>` + stamp target. `NuGet.config` — nuget.org only.
- `tests/BirthPayloadTest`, `tests/StashPayloadTest` — headless wire-format suites.
- `.claude/rules/*.md` — path-scoped conventions auto-loading with the files they cover;
  `.claude/skills/investigate-crash/SKILL.md` — invoke on any crash, freeze, wrong behaviour, or a
  pasted `CrashGuard.log`.
