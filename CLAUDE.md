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
hash-matches; `install.cmd` re-verifies those hashes on the player's machine and restores the previous
files on a mismatch. **The build never touches `dist/`** — `dist/` changes only as one atomic set via
the script, and `tools/lint-scripts.sh` fails if `dist/` disagrees with its manifest or the three
player `.cmd` scripts drift apart. **Pushing `dist/` == releasing.** Docs ship in the same commit
(CHANGELOG, README item, FIX-REFERENCE rows); `docs/RELEASE.md` enumerates them.

**While the game runs**: deploy the payload only (`[HOTRELOAD] gen2`); harness and load-time fixes
need a fresh launch.

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** Where a symptom manifests is not the
root cause — find both. Playbook: `docs/DIAGNOSTICS.md`, from its triage checklist (it has branches
for a freeze and for someone else's log). Static: the IL probes in `tools/il-probes/` (`NameSearch`,
`Inspect`, `IlDump`, `Callers`, `VerCheck`) read installed assemblies without a decompiler; a probe's
`NOT FOUND` is inconclusive until the dependency closure resolves. Runtime: `"tracing": true` in
`guardconfig.json`, reproduce, read `CrashGuard.log` — the session-wide first-chance capture logs even
swallowed exceptions with the inner chain and the **live** stack. A failed type initializer is
**cached**: a logged throw can be a re-throw. Read `docs/ENGINE-NOTES.md` / `docs/BT-INTERNALS.md` /
`docs/MODDING-PITFALLS.md` first; add what you prove.

## Conventions for guards/fixes

The conforming skeleton, the id convention and the full `critical:` call-site list live in
`.claude/rules/blt-payload-guards.md` (auto-loads with `Payload/**`). The rules in one breath:

- Static class, `Apply(Harmony)`, **idempotent** (`if (_applied) return;` — hot-reload re-applies),
  reporting `Diag.Report(component, ok, detail)`, registering `SelfHealing.RegisterTest`, owning one
  log **tag**; per-mission state resets in `OnMissionInit`.
- **`critical: true` is earned**: only when the fix's absence re-exposes a **crash-to-desktop** or
  makes **battles unplayable** — it puts a warning on the player's screen.
- **Ids**: kebab-case; the `Diag.Report` component id **is** the `SelfHealing.RecordFire` id; the
  self-test is `"<component>.contract"`.
- **A decision point is hooked by the guard that owns it, never by a tracer.** `TracePatches` is
  log-only; behaviour must not depend on `"tracing"`. This was a real bug twice (2026-09-04): with
  tracing off, `BattleMode`'s `StartBattle`/`OpenNew` decisions and `EncounterLoopGuard`'s `Finish`
  stamp never ran.
- **Every `Apply` exit path reports**, "target type not found" included: a silent return is missing
  from `MOD HEALTH:`, reading as *not built*. A config-disabled guard reports
  `(component, true, "disabled by config")` — off on purpose is healthy. Tracers are exempt: their
  load line *is* their health report, so print the resolved count. What still never reaches
  `MOD HEALTH:` is the maintained table in `docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does not cover*.
- **Fail open** — wrap every patch body; the catch returns the vanilla-preserving value; `return null`
  from a finalizer only for a deliberate suppressor; a depth counter decrements in a **finalizer**,
  never a postfix; reflect (`AccessTools`) so a game/BT update degrades, not crashes.
- Player-visible change: `Log.Screen` **once per mission** beside the detailed `Log.Info`;
  main-thread follow-up goes in a `Tick()` pumped from `PayloadEntry.Tick` — never UI work from the
  patched call stack.
- **Session state comes from `PeerDetection`** (`Payload/BattleMode.cs`), never hand-rolled
  reflection — tri-state, `null` = *could not read*, so **fail toward co-op**
  (`AnyRemotePeerConnected() != false`): a wrong "alone" sabotages a live session. A battle chokepoint
  calls `BattleMode.DecideAndApply(PayloadEntry.Harmony, "<reason>")` — the live generation's Harmony.
- **A `GuardConfig` knob is a launch-time snapshot** (cached behind a `_loaded` latch); one that must
  flip mid-session reads fresh from `GuardConfig.Path` like `PayloadEntry.FreshTracingFlag()`.
- A **wire/sync** feature mirrors `Payload/StashSync/` and `Payload/PregnancySync/` (engine-free model
  files linked into `tests/*PayloadTest`, own 4-byte magic on the shared `0x00` marker, parse-only
  receive prefix feeding a `Tick` queue, BT types via a candidate-name list, `DEGRADED` never throw).
- A load-time fix goes **first** in `PayloadEntry.Apply`, before `PatchAll` (`MovementOrderTypeInitGuard`).
- **Hazard:** `Campaign.set_TimeControlMode` carries three of our prefixes — two bool vetoers
  (`TimeEnforcementGuard`, `MapClickSpeedKeeper`) and the void `TimeTrace` prefix. Harmony skips the
  remaining **bool** prefixes once one has vetoed (so at most one vetoer fires per write) but runs
  every **void** prefix regardless. `[TIME]` names the vetoer because each vetoing prefix calls
  `TimeVeto.Note(...)` on `set_TimeControlMode` only — a fourth veto prefix must do the same.

## Working discipline (house rules, some hook-enforced)

**Do not push mid-investigation** — a push releases to players; iterate on a local deploy; push
when the fix is proven and the owner says so. **git**: never `reset` / `checkout -- <path>` /
`restore` / `stash` / `clean` / `revert` (discard family); `env -u GIT_DIR git -C "<dir>"`;
multi-line messages via `git commit -F <file>` (the hook greps commit bodies — reword prose
containing "stash"); keep the `Co-Authored-By` trailer. **No PowerShell** (bun/git/dotnet and the
Bash tool only). **Never kill the owner's game or Chrome process** — read logs, don't force-close.
Scratch goes in the session scratchpad, not the repo (`tools/` excepted). Cite other documents by
**section heading**, never by line number.

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
