# CLAUDE.md — BLT Deployment Crash Guard

Operating guide for an AI agent in this repo — pointers; the named docs hold the detail.

A companion mod for **Mount & Blade II: Bannerlord** fixing crashes and co-op bugs in the
**BannerlordTogether (BT)** mod, via Harmony patches and by-name reflection into game and BT.
Matthew Goluba's mod (`goobz22/BLTDeploymentCrashGuard`, branch `main`); he plays co-op with a
partner (Noah), his hero is **Thavin**.

## Two assemblies

- **Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — the stable module Bannerlord loads:
  lifecycle, `Log`, health/self-test, `GuardConfig`, hot-reload engine. Its DLL is **locked while the
  game runs**; changing it needs a restart.
- **Payload** (`Payload/` → `…Payload.dll`) — every guard, fix, tracer; hot-reloadable, where ~all
  iteration happens; `PayloadEntry.Apply` wires it.

`SubModule.xml` points at the harness, which loads the payload. Detail: `HOTRELOAD.md`.

## Version + release (do ALL of it — it is the release)

`Directory.Build.props` `<Version>` is the **single** source; a build stamps both assemblies and
pokes the **repo-root** `SubModule.xml` only — nothing writes `dist/SubModule.xml`, that copy is
manual and the easiest artifact to forget. Never version anything else.

**Pushing == releasing.** `install.cmd` pulls `dist/`; the README one-liners curl `install.cmd`,
`share-log.cmd` and `collect-diagnostics.cmd` from the **repo root of `main`** — release artifacts,
not tooling. Their Steam auto-detect path list is copy-pasted into all three (edit all three; the
collector's copy has drifted).

```bash
cd Harness && dotnet build -c Release && cd ../Payload && dotnet build -c Release
```

Deploy both DLLs + `SubModule.xml` to the game module (DLLs → `bin/Win64_Shipping_Client/`, XML →
module root) **and** `dist/`, then `md5sum` all three across build output, game module and `dist/`
in one pass — mandatory because **nothing cross-checks the harness/payload pair** (installer: three
independent curls; the payload's `AssemblyVersion` is a per-build wildcard), so a half-updated
`dist/` ships a mismatch silently. Game bin:
`C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client`.

**Docs ship with the binary**, same commit: `CHANGELOG.md` under the new `<Version>` (bug, root
cause, fix); a numbered README fix entry if player-visible — a crash the previous build still hits
**must** be listed, it is the only thing telling players to update; README config-table row for a new
key, tag row for a new log tag; `docs/ENGINE-NOTES.md` for a newly IL-proven fact; a
`docs/FIX-REFERENCE.md` row. Precedent: `MovementOrderTypeInitGuard` — a self-inflicted v1.3.0
regression that killed every battle for the process — shipped in v1.3.2 with neither.

**While the game runs**: deploy the payload only (hot-reloads via shadow copy, `[HOTRELOAD] gen2`).
Harness changes and load-time fixes need a **fresh launch**.

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** Where a symptom manifests is not the
root cause — find both. Playbook: `docs/DIAGNOSTICS.md`, from its crash-triage checklist.

- Static: the IL probes in `tools/il-probes/` (`NameSearch`, `Inspect`, `IlDump`, `Callers`,
  `VerCheck`) read the installed assemblies without a decompiler.
- Runtime: `"tracing": true` in `guardconfig.json`, reproduce, read `CrashGuard.log`. A session-wide
  first-chance capture logs swallowed/fatal exceptions with the inner chain, engine context, memory
  and the **live** stack. A failed type initializer is **cached** — a logged throw can be a re-throw
  whose context differs from the origin.

Engine facts → `docs/ENGINE-NOTES.md`; BT internals → `docs/BT-INTERNALS.md`; what already bit us and
what was reverted → `docs/MODDING-PITFALLS.md`. Read before diagnosing `MovementOrder`, mission load
order, siege command, time control or the BT command model. Add what you newly prove.

## Conventions for guards/fixes

- Static class, `Apply(Harmony)`, **idempotent** (`if (_applied) return;` — hot-reload re-applies).
  Reports `Diag.Report(component, ok, detail, critical: false)`; `critical: true` only for a
  load-bearing fix, as it also warns on the player's screen. Registers a self-test
  (`SelfHealing.RegisterTest`), owns a log **tag**; per-mission state resets in `OnMissionInit`.
- **Every `Apply` exit path reports**, "target type not found" included — a silent return is missing
  from `MOD HEALTH:` entirely, reading as *not built* rather than *not applicable*. A config-disabled
  guard logs the vanilla consequence and reports `(component, true, "disabled by config")`: off on
  purpose is healthy, not red. **Tracers are the deliberate exception** — a tracer's health report is
  its load line (`tracer active on N method(s)`, `type not found: X`), so print the resolved count:
  with by-name reflection a silent miss looks exactly like "the bug did not happen".
- **Fail open** — wrap every patch body; the catch returns the vanilla-preserving value (`return;` /
  `return true` / `return __exception`). `return null` from a finalizer is only for a deliberate
  suppressor. A depth counter decrements in a **finalizer**, never a postfix.
- Player-visible behaviour change: say so **once per mission** via `Log.Screen` beside the detailed
  `Log.Info`, else the mod is indistinguishable from a game bug.
- Main-thread follow-up goes in an `internal static void Tick()` pumped from `PayloadEntry.Tick` —
  never screen/UI work from inside the patched call stack.
- Reflect (`AccessTools`) into game/BT so an update degrades rather than crashes; a self-test pins
  the members and the decision logic.
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs:390`), never hand-rolled
  reflection: `IsClient()`, `AnyRemotePeerConnected()`, `ReadCoopStaticBool/String`, `FindCoopType`,
  `Snapshot()`. All tri-state — `null` = *could not read* — so **fail toward co-op**
  (`AnyRemotePeerConnected() != false`): a wrong "alone" sabotages a live session. A new battle
  chokepoint calls `BattleMode.DecideAndApply(PayloadEntry.Harmony, "<reason>")` — the current
  generation's Harmony, never a captured one.
- **`GuardConfig` caches the file for the session** (`_loaded` latch): a knob read through it changes
  only on restart, even with hot-reload on. A mid-session-flippable knob reads fresh from
  `GuardConfig.Path` as `PayloadEntry.FreshTracingFlag()` does — say so in its `_<key>` doc string.
- A **wire/sync** feature also: engine-free model + framing files linked into `tests/*PayloadTest` by
  `<Compile Include>` (never a copy); its own 4-byte magic on the shared `0x00` marker, with
  cross-feature discrimination proven headlessly *and* in an in-game loopback; self-test registered
  **before** the enabled check; BT types resolved via the candidate-name list, reporting DEGRADED
  rather than throwing; byte parsing only in the receive prefix, work queued for `Tick`;
  `CampaignEvents` subscribed in `OnGameStart` keyed on `Campaign.Current`, never in `Apply`.
- A load-time fix goes **first** in `PayloadEntry.Apply`, before `PatchAll`.
- Several shipped components predate these rules and report no health at all (the attribute-based
  deployment guards, `BattleMode`/`PeerDetection`, `PayloadEntry`, `PlayerIdentityGuard`,
  `BootstrapWatch`…). What each hides is listed in `docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does
  not cover* — read it before trusting the summary; wire new code up rather than widen the exception.

**Coordination hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes —
`TimeEnforcementGuard` (skip-original inside BT's enforcer when solo), `MapClickSpeedKeeper`
(skip-original for one map-click transition) and, with tracing, `TimeTrace` (log-only). Harmony runs
every prefix even when one returns false, so read all three before adding a fourth; the `[TIME]`
postfix's `SUPPRESSED/ALTERED by another patch` line shows which won.

## Working discipline (house rules, some hook-enforced)

- **Never guess a root cause** — prove it from IL/logs with a probe. A web result is a lead, not a
  diagnosis. Revert speculative changes the moment evidence contradicts them; keep the diagnostics.
- **Do not push mid-investigation** — a push releases to players. Iterate on a local deploy; push
  once the fix is proven. The owner says when to ship.
- **git**: never `reset` / `checkout -- <path>` / `restore` / `stash` / `clean` / `revert` (discard
  family). Use `env -u GIT_DIR git -C "<dir>"`; multi-line messages via `git commit -F <file>` (the
  destructive-actions hook greps commit bodies and false-positives on words like "stash" — reword).
  End commits with the required `Co-Authored-By` trailer.
- **No PowerShell** — bun/git/dotnet and the Bash tool only.
- **Never kill the owner's game or Chrome process.** Read logs; don't force-close.
- Scratch/probe work goes in the session scratchpad, not the repo — except durable tools (`tools/`).

## Map of the repo

- `Payload/*.cs` — guards/fixes/tracers (one concern per file; header explains bug + fix).
  `Harness/*.cs` — lifecycle, logging, config, reload engine, self-heal.
- `README.md` — player-facing: install, numbered fixes, config, tags, troubleshooting, known issues.
  `CHANGELOG.md` — per-version. `HOTRELOAD.md` — dev reload workflow.
- `docs/DIAGNOSTICS.md` — how to investigate. `docs/ENGINE-NOTES.md` — engine facts proven from IL.
  `docs/BT-INTERNALS.md` — BT internals from IL (unofficial reference).
- `docs/FIX-REFERENCE.md` — per-fix table (file/class/tag/config/scope/patched members/limitations/
  self-test) + indexes. `docs/MODDING-GUIDE.md` — public Bannerlord/BT techniques.
  `docs/MODDING-PITFALLS.md` — what bit us; reverted attempts and gotchas.
- `UPSTREAM_BUG_REPORT.md`, `docs/UPSTREAM_CONTRIBUTION.md` — BT-side issues and reports.
- `tools/il-probes/` — the IL/reflection probes + their README.
- `install.cmd`, `share-log.cmd`, `collect-diagnostics.cmd` — installer and log-sharing scripts,
  served live from `main`. `dist/` — the three shipped artifacts (tracked on purpose).
- `SubModule.xml` — module manifest, stamped by the build. `Directory.Build.props` — the one
  `<Version>` + stamp target. `NuGet.config` — nuget.org only.
- `tests/BirthPayloadTest`, `tests/StashPayloadTest` — headless wire-format suites.
- `.claude/rules/*.md` — path-scoped conventions auto-loading with the files they cover;
  `.claude/skills/investigate-crash/SKILL.md` — invoke on any crash, freeze, wrong behaviour, or a
  pasted `CrashGuard.log`.
