# CLAUDE.md — BLT Deployment Crash Guard

Operating guide for an AI agent working in this repo. Read this first. It exists so we stop
re-deriving the same things every session.

## What this is

A companion mod for **Mount & Blade II: Bannerlord** that fixes crashes and co-op bugs in the
**BannerlordTogether (BT)** multiplayer mod, via Harmony patches and by-name reflection into the
game and BT. It is Matthew Goluba's mod (GitHub `goobz22/BLTDeploymentCrashGuard`, branch `main`).

The owner plays co-op with a partner (Noah). The owner's hero is **Thavin**.

## Two-assembly architecture

- **Harness** (`Harness/` → `BLTDeploymentCrashGuard.dll`) — small, stable module Bannerlord
  loads. Lifecycle, logging (`Log.cs`), health/self-test, config (`GuardConfig.cs`), and the
  hot-reload engine. Changing it needs a game restart. Its DLL is **locked while the game runs**.
- **Payload** (`Payload/` → `BLTDeploymentCrashGuard.Payload.dll`) — every guard, fix, and tracer.
  Hot-reloadable; this is where ~all iteration happens. `PayloadEntry.Apply` wires everything.

`SubModule.xml` points at the harness, which loads the payload. Detail: `HOTRELOAD.md`.

## The one version source

`Directory.Build.props` `<Version>` is the **single** source of truth. A build stamps both
assemblies and pokes `SubModule.xml`. Never write a version anywhere else. Bump it for a release.

## Build & deploy (do ALL of this — it is the release)

`install.cmd` downloads from `dist/`, so **pushing to GitHub == releasing**. Deploy means updating
three files in **both** the live game module and `dist/`, then hash-verifying.

```bash
cd Harness  && dotnet build -c Release
cd ../Payload && dotnet build -c Release
```

Deploy targets (both DLLs + `SubModule.xml`):

- Game module: `<Game>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/` (DLLs) and the
  module root (`SubModule.xml`).
- Repo `dist/`.

Then `md5sum` the three files across build output, game module, and `dist/` — they must match.
Game bin: `C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client`.

**While the game is running**: the harness DLL is locked — deploy only the payload (it hot-reloads
via shadow copy, `[HOTRELOAD] gen2` in the log). Harness changes and the load-time fixes
(e.g. `MovementOrderTypeInitGuard`) need a **fresh launch**, not a hot-reload.

## How to investigate (never guess)

**Prove the root cause from IL and logs before changing code.** The manifested location is not the
root-cause location — find both. Full playbook: `docs/DIAGNOSTICS.md`. In short:

- Static: the IL probes in `tools/il-probes/` (`NameSearch`, `Inspect`, `IlDump`, `Callers`,
  `VerCheck`) read the installed assemblies without a decompiler.
- Runtime: set `"tracing": true` in `guardconfig.json`, reproduce, read `CrashGuard.log`. A
  session-wide first-chance exception capture logs swallowed/fatal exceptions with the full inner
  chain, engine-state context, memory, and the **live** stack (who triggered it). Remember a failed
  type initializer is **cached** — a logged throw can be a re-throw whose context differs from the
  origin.

Hard-won engine facts live in `docs/ENGINE-NOTES.md`. Read it before diagnosing anything about
`MovementOrder`, mission load order, siege command, time control, or the BT command model. Add to
it when you prove something new.

## Conventions for guards/fixes

- Each guard is a static class with `Apply(Harmony)`, reports via `Diag.Report(component, ok, detail)`,
  registers a self-test via `SelfHealing.RegisterTest`, and logs under its own **tag** (e.g.
  `[SIEGE-CMD]`). Per-mission state resets in `OnMissionInit`.
- High-frequency tracer lines go through `TraceThrottle.Emit(key, msg)` so they can't flood the log.
- Resolve game/BT members by reflection (`AccessTools`) so a game/BT update degrades gracefully
  rather than crashing; a self-test pins the members and the decision logic.
- A fix that must run before the game touches a type (load-time, like `MovementOrderTypeInitGuard`)
  goes **first** in `PayloadEntry.Apply`, before `PatchAll` and the other guards.

## Working discipline (house rules, some hook-enforced)

- **Never guess a root cause.** Prove it from IL/logs with a probe. A web result is a lead, not a
  diagnosis; prove it in *these* logs. Revert speculative changes the moment evidence contradicts them.
- **Do not push mid-investigation** — pushing releases to players. Deploy locally, iterate, and
  commit+push only once a fix is proven. The owner will say when to ship.
- **git**: never `git reset` / `checkout -- <path>` / `restore` / `stash` / `clean` / `revert`
  (discard family). Use `env -u GIT_DIR git -C "<dir>"`. Multi-line commit messages via
  `git commit -F <file>` (a destructive-actions hook greps commit bodies and false-positives on
  words like "stash" — reword such prose). End commits with the required `Co-Authored-By` trailer.
- **No PowerShell** — bun/git/dotnet and the Bash tool only.
- **Never kill the owner's game or Chrome process.** Read logs; don't force-close.
- Scratch/probe work goes in the session scratchpad, not the repo — except durable tools, which
  belong in `tools/`.

## Map of the repo

- `Payload/*.cs` — guards/fixes/tracers (one concern per file, header explains the bug + fix).
- `Harness/*.cs` — lifecycle, logging, config, reload engine, self-heal.
- `docs/DIAGNOSTICS.md` — how to investigate. `docs/ENGINE-NOTES.md` — proven engine facts.
- `tools/il-probes/` — the IL/reflection probes + their README.
- `README.md` — player-facing. `CHANGELOG.md` — per-version. `HOTRELOAD.md` — dev reload workflow.
- `UPSTREAM_BUG_REPORT.md`, `docs/UPSTREAM_CONTRIBUTION.md` — BT-side issues and reports.
