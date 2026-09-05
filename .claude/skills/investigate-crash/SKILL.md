---
name: investigate-crash
description: Use for any Bannerlord or BannerlordTogether crash, freeze, or wrong behaviour with this mod installed, or when the user pastes a CrashGuard.log, an rgl_log dump, or a screenshot of a game bug.
---

# investigate-crash

The rule this enforces: **prove the root cause from IL and logs before changing code.** Where a
symptom manifests is almost never the root-cause location — find both (`docs/DIAGNOSTICS.md:3-5`).
A vanilla symptom found on the web is a lead, not a diagnosis (`docs/DIAGNOSTICS.md` § 5
*Discipline*). That playbook grows with every investigation, so the citations below name its
section headings rather than line numbers.

## 0. Read before deriving

`docs/ENGINE-NOTES.md`, `docs/BT-INTERNALS.md` (pinned to BT v0.5.0.1 / game 1.4.8.119303),
`docs/MODDING-PITFALLS.md` (what bit us and was reverted), and the **six** `docs/FIX-REFERENCE.md`
indexes (§ *Index 0* … § *Index 5*): co-op scope, log tag → file, config key → file,
patched member → fix, on-screen message → file, `MOD HEALTH` / `SELFTEST` component id → file. A
tag, key, member or health id from the log resolves to a file through them.

## 1. Reproduce with tracing on

In `<Game>/Modules/BLTDeploymentCrashGuard/guardconfig.json`:

```json
{ "tracing": true, "selfTest": true }
```

`tracing` turns on the tracer bundle plus `RuntimeDiagnostics`. `selfTest` runs the decision-logic
test of every guard **that registered one** via `SelfHealing.RegisterTest` and logs PASS/FAIL, which
proves that guard's wiring survived the last game/BT update (the `selfTest` block of
`PayloadEntry.Apply`, `SelfHealing.RunSelfTests`). A guard that registers no test produces no line,
so **absence is not a pass**: 25 payload files register a test (every crash guard does since
2026-09-04), while the wired-in `MapClickSpeedKeeper`, `TimeEnforcementGuard`, `TimeFlowPatch`,
`ShareTimeControl`, `PlayerIdentityGuard` and `BootstrapWatch` register none, and no tracer does —
the roster is `docs/FIX-REFERENCE.md` § *Index 5*. The tracing flag is read **fresh from disk** on each
payload apply, so with hot-reload on you can flip it and drop a rebuilt payload to trace mid-session
without losing the repro (`PayloadEntry.FreshTracingFlag`, `Payload/PayloadEntry.cs:219`). Log:
`<Game>/Modules/BLTDeploymentCrashGuard/CrashGuard.log` (history in `.log.1` … `.log.6`).

## 2. Read the log by tag

Grep targets: `MOD HEALTH`, `[SELFTEST]`, `[HOTRELOAD]`, `[BATTLE-MODE]`, `[PEER-DETECT]`, then the
per-fix tags — `[SIEGE-CMD]`, `[COOP-CMD]`, `[IDENTITY]`, `[TIME-GUARD]`, `[MO-INIT]`, `[CHARGEN]`,
`[DIAG]` (`docs/DIAGNOSTICS.md` § 2 *Log tags (grep targets)*, `README.md` § *Diagnostics & robustness*, the grep-tag legend). In this order:

- `MOD HEALTH` — "NOT resolved" means a member was renamed and that fix is silently inert
  (`Harness/Diag.cs:87-99`); with `[SELFTEST] FAIL` (broken logic or a pinned member) that alone can
  be the whole explanation. **Silence there is ambiguous, not clean:** `MOD HEALTH:` is built only
  from components that called `Diag.Report`, and some shipped ones never do — `PayloadEntry`,
  `PlayerIdentityGuard`, `BootstrapWatch`, `MapClickSpeedKeeper`, `TimeEnforcementGuard`,
  `TimeFlowPatch`, `ShareTimeControl` and every tracer (the deployment guards and `BattleMode`
  report since 2026-09-04). Check `GUARD ACTIVITY:` and the fix's own tag before concluding it did
  not apply (`docs/DIAGNOSTICS.md` § 2 *What `MOD HEALTH:` does not cover*).
- `[PEER-DETECT]` — a BannerlordTogether type lookup that failed; the README calls it the earliest
  warning of a BT update (`README.md` § *Diagnostics & robustness*, the `[PEER-DETECT]` legend row). It is logged only when `PeerDetection.FindCoopType`
  **throws** (`PeerDetection.FindCoopType`, `Payload/BattleMode.cs:737`); a type that is simply
  absent returns `null` with no line at all (`:732,739`), so a quiet `[PEER-DETECT]` does not clear
  a rename either.
- `[DIAG]` — memory + engine-state heartbeat every ~15 s and at each mission transition; shows a
  leak or state change building *before* the symptom.
- `[repeat] key ×N in Ys` — `TraceThrottle` coalescing; the full line with its stack is the first
  occurrence, earlier in the file (`Payload/TraceThrottle.cs:34-37`).

## 3. Read the first-chance capture

`CharacterCreationTrace` arms an `AppDomain.FirstChanceException` observer for the session, so even a
swallowed exception is logged (`docs/DIAGNOSTICS.md` § 2 *Reading a first-chance exception line*).
Per throw, capture:

- the full **inner chain** (`<- INNER:` lines) — a `TypeInitializationException`'s real cause is
  always its inner exception;
- **`CONTEXT:`** — engine state at the throw (is `Mission.Current` null, which game state) and the
  memory line;
- the **`LIVE …` frames** — the executing stack, i.e. *who triggered it*. The exception's own stack
  is truncated to the throw point and hides the trigger; the live stack does not.

**A failed type initializer is cached.** .NET runs a `.cctor` once; if it throws, every later access
re-throws the same exception without re-running it. So a logged type-init throw may be a **re-throw**
whose `CONTEXT` differs from the origin — and if a ctor probe never fires while the crash still
happens, the type was poisoned before the probe existed. Look **earlier** (load time), not at the
manifested frame (`docs/DIAGNOSTICS.md` § 2 *Reading a first-chance exception line*, closing
paragraph).

Also on disk: `C:/ProgramData/Mount and Blade II Bannerlord/logs/` (`rgl_log_*`, `rgl_log_errors_*`,
`watchdog_*`); `Unhandled Exception Code 0xE0434352` there is a managed exception that killed the
process. ButterLib does not catch every fatal — the mod's own first-chance capture is the backstop
(`docs/DIAGNOSTICS.md` § 4 *Crash reports on disk*).

## 4. Locate the code with the IL probes

Read the **installed** assemblies, no decompiler. Build each tool once with
`cd tools/il-probes/<Tool> && dotnet build -c Release` (`tools/il-probes/README.md`):

```
NameSearch.exe <dll> <term>                 # concept -> exact names
Inspect.exe    <dll> <FullTypeName>         # members, signatures, enum values
IlDump.exe     <dll> "<Ns.Type>::<Method>"  # IL; supports .cctor and .ctor
Callers.exe    <dll> <memberName>           # callers -> ordering, trigger paths
VerCheck.exe   <dll>                        # assembly identity
```

Targets: `<Game>/bin/Win64_Shipping_Client/` and
`<Game>/Modules/{SandBox,BannerlordTogether}/bin/Win64_Shipping_Client/`. Look for null-deref sites
(`callvirt`/`ldfld` on a nullable getter), `beforefieldinit` types whose `.cctor` depends on runtime
state, and ordering — dump the caller and read the call sequence (`docs/DIAGNOSTICS.md` § 1
*Static analysis*, "What to look for").

## 5. Prove the root cause

State the mechanism in one paragraph with the IL member names that prove it, naming the manifested
frame **and** the cause. Worked example: the crash surfaced at `Formation.ResetAux`; the cause was the
`beforefieldinit` `MovementOrder` struct whose `.cctor` ran while `Mission.Current` was null, proven by
`IlDump` on `MovementOrder::.cctor` and `::.ctor` (`Payload/MovementOrderTypeInitGuard.cs:13-31`).
Revert speculative changes the moment evidence contradicts them; keep the diagnostics
(`docs/DIAGNOSTICS.md` § 5 *Discipline*).

## 6. Write the fix

A new guard in `Payload/<Name>.cs` per `.claude/rules/blt-payload-guards.md`: header (bug + evidence
+ fix), config gate, members resolved by name with self-disable, `Diag.Report`, its own log tag, a
`SelfHealing.RegisterTest` self-test pinning the members **and** the decision logic, `RecordFire`,
`OnMissionInit` reset, wiring in `PayloadEntry.Apply` (load-time fixes first). Run with
`selfTest=true` and confirm `[SELFTEST] PASS`.

## 7. Deploy and verify

```
cd Harness  && dotnet build -c Release
cd ../Payload && dotnet build -c Release
```

Or, in one step, `tools/release.sh` (`--no-build` redeploys the existing output): it copies **all
three** files — `BLTDeploymentCrashGuard.dll`, `BLTDeploymentCrashGuard.Payload.dll`,
`SubModule.xml` — to the game module (DLLs in `bin/Win64_Shipping_Client/`, XML in the module root)
**and** to `dist/`, writes `dist/manifest.txt` and SHA256-verifies every copy; "release-ready" is
the only acceptable last line (`docs/RELEASE.md`). Never copy into `dist/` by hand — `install.cmd`
re-verifies the manifest on the player's machine and treats a mismatch as a corrupt release.

**Fresh launch vs hot-reload:** while the game runs the harness DLL is locked, so deploy the payload
alone — it reloads via shadow copy (`[HOTRELOAD] gen2 applied`, `HOTRELOAD.md` § *A) Build-and-drop*). Harness
changes and load-time fixes need a **fresh launch** (`CLAUDE.md` § *Version + release*); the ones
that cannot be reloaded — `MovementOrderTypeInitGuard`, `ClientBootstrapFix`, `ClanModeSoloFix` —
are listed at `HOTRELOAD.md` § *What a reload cannot do (fresh launch required)*.

## 8. Do not push until it is proven

`install.cmd` pulls from `dist/` on `main`, so **pushing releases to players**. Deploy locally and
iterate; push only once the fix is proven, and the owner says when to ship (`CLAUDE.md` § *Working
discipline*). Never kill the owner's running game to collect a log.

## 9. Write it down

`README.md` (numbered item, tag, config row) · `docs/FIX-REFERENCE.md` (entry + a row in each of the
**six** indexes that applies: co-op scope, log tag, config key, patched member, on-screen message,
health/self-test component id) ·
`docs/ENGINE-NOTES.md` or `docs/BT-INTERNALS.md` (the proven fact, evidence + date) · `CHANGELOG.md`
(symptom, cause, fix) · `docs/MODDING-PITFALLS.md` for anything tried and reverted. Details:
`.claude/rules/blt-docs-tools.md`.
