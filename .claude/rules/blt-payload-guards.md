---
paths: ["Payload/**", "tests/**"]
---

# Payload guards — conventions

One concern per `Payload/*.cs` file, with a header comment stating the bug, the IL evidence and
the fix (models: `Payload/SiegeCommandGuard.cs:14-54`, `Payload/CoopCommandSplit.cs:14-43`,
`Payload/MovementOrderTypeInitGuard.cs:11-40`). The payload hot-reloads as a whole assembly, so
statics are fresh every generation — never assume state survives a reload.

The wire-format test suites under `tests/` **link** payload sources rather than copying them
(`tests/BirthPayloadTest/BirthPayloadTest.csproj:17-18`,
`tests/StashPayloadTest/StashPayloadTest.csproj:21-24`), so editing a `Payload/*Sync/*Data.cs` or
`*WireFraming.cs` file changes what those projects compile — build them after touching one.

## Shape of a guard

Static class with one `Apply(Harmony harmony)` wired from `Payload/PayloadEntry.cs:47-77`:

1. **Latch.** `if (_applied) return;` at the top; set `_applied = true` only after the patches
   install (`Payload/SiegeCommandGuard.cs:81-84,128`). `Apply` is deliberately called again on the
   module screen and at game start so a late-loading BannerlordTogether assembly still gets hooked
   (module screen `Payload/PayloadEntry.cs:115-124`, game start `Payload/PayloadEntry.cs:126-133`;
   `SiegeCommandGuard.RetryBt`, `SiegeCommandGuard.cs:142-155`).
2. **Config gate first.** Read the guard's own key and return early when off, reporting *healthy*:
   `GuardConfig.Bool("siegeCommandAll", true)` → `Diag.Report(Component, true, "disabled by config")`
   (`Payload/SiegeCommandGuard.cs:87-91`, `Payload/CoopCommandSplit.cs:72-76`).
3. **Resolve every game/BT member by NAME** through `AccessTools` — `Method`, `Property`, `Field`,
   `Constructor`, `TypeByName` (`SiegeCommandGuard.cs:93-102`). Never a compile-time reference to BT;
   `SubModule.xml` declares it `Optional="true"`.
4. **Self-disable on an unresolved member.** If any required member is null, log the tag line, call
   `Diag.Report(Component, false, "members not resolved")` and return without patching
   (`SiegeCommandGuard.cs:104-109`, `CoopCommandSplit.cs:90-94`). Degrading to inert is the contract:
   a game/BT rename must unhook a feature, never crash. `critical: true` escalates to an on-screen
   warning (`Harness/Diag.cs:71-99`).
5. **Wrap `Apply` in try/catch** → `Diag.Report(Component, false, ex.Message)` (`SiegeCommandGuard.cs:135-139`).
   Do not swallow inside `PayloadEntry.Apply` itself: it rethrows so the harness keeps the previous
   generation (`Payload/PayloadEntry.cs:108-112`).
6. **Register a self-test** — `SelfHealing.RegisterTest(SelfTest)` (`SiegeCommandGuard.cs:133`,
   `CoopCommandSplit.cs:99`). It must pin both the *members* (re-resolve them by name) and the
   *decision logic* against known inputs, returning `SelfHealing.TestResult.Of(name, pass, detail)` —
   see `CoopCommandSplit.cs:416-443`. Runs at startup under `selfTest=true`
   (`Harness/SelfHealing.cs:108-141`). Tests are cleared per generation; fire counts are not.
7. **Record fires.** `SelfHealing.RecordFire(Component)` each time the guard actually suppresses a
   crash or corrects state (`CoopCommandSplit.cs:204,259`). A permanently-inert guard is evidence the
   upstream bug is gone (`Harness/SelfHealing.cs:6-25`).

## Logging

- **One tag per guard.** New guards declare a `private const string Tag` and use it on every line;
  today only two files do (`SiegeCommandGuard.cs:57`, `CoopCommandSplit.cs:46`) — older guards still
  write the tag inline (`MovementOrderTypeInitGuard.cs:53,64,69,76`), so treat the constant as the
  convention for new code, not a description of the tree. Tags that hold the one-guard invariant:
  `[SIEGE-CMD]`, `[COOP-CMD]`, `[MO-INIT]`, `[TIME-GUARD]`.
- **Two tags are already shared and must not grow.** `[GATE]` is emitted by both
  `Payload/CivilianGateCloseFix.cs` and `Payload/SiegeGatePromptFix.cs`; `[IDENTITY]` by both
  `Payload/CoopHeroIdentityLock.cs` and `Payload/PlayerIdentityGuard.cs`. A grep on either mixes
  unrelated events, which the README warns players about (`README.md:468-472`). Do not add a third
  component to either — a new gate or identity fix takes its own tag.
- The tag is the grep handle; register it in the README tag legend (`README.md:461-490`) and in
  `docs/FIX-REFERENCE.md`'s log-tag index (`docs/FIX-REFERENCE.md:4059`).
- **High-frequency lines go through `TraceThrottle.Emit(key, msg)`** (`Payload/TraceThrottle.cs:38-84`),
  never `Log.Info` — the first occurrence logs in full, repeats collapse to
  `[repeat] key ×N in Ys`. A per-tick tracer without it filled the 8 MB log in minutes and rotated the
  evidence away (`Payload/TraceThrottle.cs:7-12`). It lives in the payload on purpose so the fix can
  hot-reload.

## Per-mission and per-generation state

Every guard with battle state exposes `OnMissionInit()` that resets counters, depth flags and cached
parties, called from `Payload/PayloadEntry.cs:135-142` (`SiegeCommandGuard.cs:157-166`,
`CoopCommandSplit.cs:108-121`). Reentrancy flags are `[ThreadStatic]` (`SiegeCommandGuard.cs:62-66`).

## Load order

`MovementOrderTypeInitGuard.ApplyEarly(harmony)` runs **first** in `PayloadEntry.Apply`, before
`harmony.PatchAll` and every other guard (`Payload/PayloadEntry.cs:38-45`). Any fix that must run
before the game touches a type goes in that early slot — patching `Formation`/`OrderController` makes
the CLR prepare the `beforefieldinit` `MovementOrder` struct, and a failed type initializer is cached
for the process (`Payload/MovementOrderTypeInitGuard.cs:13-31`). Load-time fixes do **not** take effect
on a hot-reload; they need a fresh launch — `HOTRELOAD.md:139-147` lists the four cases
(harness changes, `MovementOrderTypeInitGuard`, `ClientBootstrapFix`, `ClanModeSoloFix`).

`MovementOrderTypeInitGuard` is the exemplar of the **early slot only** — copy its ordering, not the
rest of it. Its exits log `[MO-INIT]` but never call `Diag.Report`
(`MovementOrderTypeInitGuard.cs:53-54,74-77`; the file contains no `Diag.Report`) and it registers
no self-test (no `SelfHealing.RegisterTest`), so it is absent from `MOD HEALTH:` and untested under
`selfTest=true` — exactly what steps 4 and 6 above exist to prevent. Those steps still apply to a
new early-slot fix.

## Co-op scoping

Scope every behaviour change by role through `PeerDetection`. The class and its tri-state contract
("values only; nulls mean unknown") are at `Payload/BattleMode.cs:386-390`; the members are
`IsClient()` (`BattleMode.cs:506`), `AnyRemotePeerConnected()` (`BattleMode.cs:511`) and
`ReadCoopStaticString(...)` (`BattleMode.cs:565`). The first two are
tri-state: `null` means unknown, so compare `== true` (`SiegeCommandGuard.cs:230-236`,
`CoopCommandSplit.cs:341-342`). Typical scopes: solo+host act, client stands down
(`SiegeCommandGuard.cs:207-221`); or inert outside a live session. The role tag on each log line
(`S`/`H`/`C`) comes from the same source (`PayloadEntry.cs:161-183`).

## Adding a fix — the checklist

1. New `Payload/<Name>.cs` with the header, tag, `Component`, config gate, self-test; wire into
   `PayloadEntry.Apply` (and `OnMissionInit`/`Tick` if it needs them).
2. New config key: add it **with its `_key` explanation line** to `GuardConfig.DefaultJson`
   (`Harness/GuardConfig.cs:82-115`) *and* a row in the README `## Config` table.
3. `README.md` — a numbered item under Crash fixes (`README.md:77`), Co-op & gameplay fixes
   (`README.md:172`) or Diagnostics & robustness (`README.md:444`), plus the tag in the legend
   (`README.md:461-490`).
4. `docs/FIX-REFERENCE.md` — a full entry (README item · Source · Class · Tag · Config · Scope, then
   Mechanism / Patched members / Limitations / Self-test) and a row in each of the **five** indexes
   that applies (`docs/FIX-REFERENCE.md:4044,4059,4118,4149,4266`; see `.claude/rules/blt-docs-tools.md`).
5. `CHANGELOG.md` — an entry under the version being released.
6. Newly proven engine or BT behaviour → `docs/ENGINE-NOTES.md` / `docs/BT-INTERNALS.md` with evidence
   and date; a reverted attempt or gotcha → `docs/MODDING-PITFALLS.md`; a reusable technique →
   `docs/MODDING-GUIDE.md`.
