# CLAUDE.md — BLT Deployment Crash Guard

Operating guide for an AI agent in this repo — pointers; the named docs hold the detail.

A companion mod for **Mount & Blade II: Bannerlord** fixing crashes and co-op bugs in the
**BannerlordTogether (BT)** mod, via Harmony patches and by-name reflection into game and BT.
Matthew Goluba's mod (`goobz22/BLTDeploymentCrashGuard`, branch `main`); he plays co-op with a
partner (Noah), his hero is **Thavin**.

## Two assemblies

**Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — the stable module Bannerlord loads:
lifecycle, `Log`, health/self-test, `GuardConfig`, hot-reload engine. Its DLL is **locked while the
game runs**; changing it needs a restart. **Payload** (`Payload/` → `…Payload.dll`) — every guard,
fix and tracer, hot-reloadable and wired by `PayloadEntry.Apply`; ~all iteration happens there.
`SubModule.xml` points at the harness, which loads the payload (`HOTRELOAD.md`).

## Version + release (do ALL of it — it is the release)

`Directory.Build.props` `<Version>` is the **single** source; a build stamps both assemblies and
pokes the **repo-root** `SubModule.xml` only — nothing writes `dist/SubModule.xml`, that copy is
manual and the easiest artifact to forget. Never version anything else.

**Pushing == releasing**: `install.cmd` pulls `dist/`, and the README one-liners curl `install.cmd`,
`share-log.cmd`, `collect-diagnostics.cmd` from the **repo root of `main`** — release artifacts, not
tooling (their Steam path list is copy-pasted into all three; the collector's copy has drifted).

```bash
cd Harness && dotnet build -c Release && cd ../Payload && dotnet build -c Release
```

Deploy both DLLs + `SubModule.xml` to the game module (DLLs → `bin/Win64_Shipping_Client/`, XML →
module root) **and** `dist/`, then `md5sum` all three across build output, game module and `dist/`.
**Nothing cross-checks the harness/payload pair** (three independent curls; the payload's
`AssemblyVersion` is a per-build wildcard), so a half-updated `dist/` ships a mismatch silently.
Game: `C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client`.

**Docs ship with the binary**, same commit: a `CHANGELOG.md` entry under the new `<Version>` (bug,
cause, fix); a numbered README fix entry if player-visible — a crash the previous build still hits
**must** be listed, it is the only thing telling players to update; README rows for a new config key
and log tag; `docs/ENGINE-NOTES.md` for a newly IL-proven fact; a `docs/FIX-REFERENCE.md` row.
`MovementOrderTypeInitGuard` (a v1.3.0 self-inflicted regression that killed every battle for the
process) shipped in v1.3.2 with none of them.

**While the game runs**: deploy the payload only (`[HOTRELOAD] gen2`); harness changes and load-time
fixes need a **fresh launch** (`HOTRELOAD.md`).

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** Where a symptom manifests is not the
root cause — find both. Playbook: `docs/DIAGNOSTICS.md`, from its crash-triage checklist.

- Static: the IL probes in `tools/il-probes/` (`NameSearch`, `Inspect`, `IlDump`, `Callers`,
  `VerCheck`) read the installed assemblies without a decompiler.
- Runtime: `"tracing": true` in `guardconfig.json`, reproduce, read `CrashGuard.log` — the
  session-wide first-chance capture logs even swallowed exceptions with the inner chain and the
  **live** stack. A failed type initializer is **cached**, so a logged throw can be a re-throw whose
  context differs from the origin.

Engine facts → `docs/ENGINE-NOTES.md`; BT internals → `docs/BT-INTERNALS.md`; what bit us and what
was reverted → `docs/MODDING-PITFALLS.md`. Read those before diagnosing `MovementOrder`, mission
load order, siege command, time control or the BT model; add what you newly prove.

## Conventions for guards/fixes

- Static class, `Apply(Harmony)`, **idempotent** (`if (_applied) return;` — hot-reload re-applies).
  Reports `Diag.Report(component, ok, detail, critical: false)` — `critical: true` only for a
  load-bearing fix, as it also warns on the player's screen. Registers a self-test
  (`SelfHealing.RegisterTest`), owns a log **tag**; per-mission state resets in `OnMissionInit`.
- **Every `Apply` exit path reports**, "target type not found" included — a silent return is missing
  from `MOD HEALTH:` entirely, reading as *not built* rather than *not applicable*. A config-disabled
  guard logs the vanilla consequence and reports `(component, true, "disabled by config")`: off on
  purpose is healthy, not red. Tracers are the deliberate exception — their health report is their
  load line, which must print the resolved method count (`docs/DIAGNOSTICS.md`).
- **Fail open** — wrap every patch body; the catch returns the vanilla-preserving value (`return;` /
  `return true` / `return __exception`); `return null` from a finalizer only for a deliberate
  suppressor. A depth counter decrements in a **finalizer**, never a postfix.
- Player-visible change: `Log.Screen` **once per mission** beside the detailed `Log.Info`, else the
  mod is indistinguishable from a game bug. Main-thread follow-up goes in an
  `internal static void Tick()` pumped from `PayloadEntry.Tick` — never UI work from inside the
  patched call stack. Reflect (`AccessTools`) so a game/BT update degrades rather than crashes.
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs:390`), never hand-rolled
  reflection: `IsClient()`, `AnyRemotePeerConnected()`, `ReadCoopStaticBool/String`, `FindCoopType`,
  `Snapshot()` — all tri-state, `null` = *could not read*, so **fail toward co-op**
  (`AnyRemotePeerConnected() != false`): a wrong "alone" sabotages a live session. A new battle
  chokepoint calls `BattleMode.DecideAndApply(PayloadEntry.Harmony, "<reason>")` — the current
  generation's Harmony, never a captured one.
- **A `GuardConfig` knob is a launch-time snapshot** (whole file cached behind a `_loaded` latch), so
  it cannot be hot-reloaded. A knob that must flip mid-session reads fresh from `GuardConfig.Path` as
  `PayloadEntry.FreshTracingFlag()` does — say so in its `_<key>` doc string (`HOTRELOAD.md`).
- A **wire/sync** feature also: engine-free model/framing files linked into `tests/*PayloadTest` via
  `<Compile Include>` (never copied); own 4-byte magic on the shared `0x00` marker, discrimination
  proven headlessly *and* in-game; self-test registered before the enabled check; BT types via the
  candidate-name list → DEGRADED, never thrown; receive prefix parses bytes only and queues for
  `Tick`; `CampaignEvents` subscribed in `OnGameStart`, keyed on `Campaign.Current`.
- A load-time fix goes **first** in `PayloadEntry.Apply`, before `PatchAll`. Several older components
  report no health at all (attribute-based deployment guards, `BattleMode`/`PeerDetection`,
  `PayloadEntry`, `PlayerIdentityGuard`, `BootstrapWatch`…) — see `docs/DIAGNOSTICS.md` § *What
  `MOD HEALTH:` does not cover*; wire new code up instead of widening that.
- **Hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes (`TimeEnforcementGuard`,
  `MapClickSpeedKeeper`, `TimeTrace` when tracing) and Harmony runs every prefix even when one
  returns false — read all three before adding a fourth; the `[TIME]` postfix says which won.

## Working discipline (house rules, some hook-enforced)

- **Never guess a root cause** — prove it from IL/logs with a probe. A web result is a lead, not a
  diagnosis. Revert speculative changes the moment evidence contradicts them; keep the diagnostics.
- **Do not push mid-investigation** — a push releases to players. Iterate on a local deploy and push
  once the fix is proven; the owner says when to ship.
- **git**: never `reset` / `checkout -- <path>` / `restore` / `stash` / `clean` / `revert` (discard
  family); `env -u GIT_DIR git -C "<dir>"`; multi-line messages via `git commit -F <file>` (the
  destructive-actions hook greps commit bodies, false-positiving on words like "stash"); end commits
  with the required `Co-Authored-By` trailer.
- **No PowerShell** (bun/git/dotnet and the Bash tool only). **Never kill the owner's game or Chrome
  process** — read logs, don't force-close. Scratch/probe work stays in the session scratchpad, not
  the repo — except durable tools (`tools/`).

## Map of the repo

- `Payload/*.cs` — guards/fixes/tracers (one concern per file; header explains bug + fix).
  `Harness/*.cs` — lifecycle, logging, config, reload engine, self-heal.
- `README.md` — player-facing: install, numbered fixes, config, tags, troubleshooting, known issues.
  `CHANGELOG.md` — per-version. `HOTRELOAD.md` — dev reload workflow.
- `docs/`: `DIAGNOSTICS.md` (investigate) · `ENGINE-NOTES.md` (engine facts proven from IL) ·
  `BT-INTERNALS.md` (BT internals from IL, unofficial) · `FIX-REFERENCE.md` (per-fix table:
  file/class/tag/config/scope/patched members/limitations/self-test) · `MODDING-GUIDE.md` (public
  Bannerlord/BT techniques) · `MODDING-PITFALLS.md` (what bit us, reverted attempts) ·
  `UPSTREAM_CONTRIBUTION.md` + root `UPSTREAM_BUG_REPORT.md` (BT-side reports).
- `tools/il-probes/` — the IL/reflection probes + their README. `tests/BirthPayloadTest`,
  `tests/StashPayloadTest` — headless wire-format suites.
- `install.cmd`, `share-log.cmd`, `collect-diagnostics.cmd` — installer and log-sharing scripts,
  served live from `main`. `dist/` — the three shipped artifacts (tracked on purpose).
- `SubModule.xml` (module manifest, stamped by the build), `Directory.Build.props` (the one
  `<Version>` + stamp target), `NuGet.config` (nuget.org only).
- `.claude/rules/*.md` — path-scoped conventions auto-loading with the files they cover;
  `.claude/skills/investigate-crash/SKILL.md` — invoke on any crash, freeze, wrong behaviour, or a
  pasted `CrashGuard.log`.
