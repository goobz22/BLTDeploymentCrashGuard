---
paths: ["Payload/**", "tests/**"]
---

# Payload guards — conventions

One concern per `Payload/*.cs`, header comment stating the bug, the IL evidence and the fix (models:
`Payload/SiegeCommandGuard.cs:14-54`, `Payload/MovementOrderTypeInitGuard.cs:11-43`). The payload
hot-reloads as a whole assembly — statics are fresh every generation. The `tests/` suites **link**
payload sources rather than copying them (`tests/BirthPayloadTest/BirthPayloadTest.csproj:17-18`,
`tests/StashPayloadTest/StashPayloadTest.csproj:21-24`), so editing a `Payload/*Sync/*Data.cs` or
`*WireFraming.cs` changes what they compile — build them after touching one.

## The conforming skeleton

Every numbered comment is a rule.

```csharp
// Payload/ExampleGuard.cs — header: the bug, the IL evidence, the fix.
internal static class ExampleGuard
{
    internal const string Component = "example-guard";  // kebab-case; ALSO the RecordFire id
    private const string Tag = "[EXAMPLE]";             // this guard's only log tag
    private static bool _applied;

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;         // 1. latch — Apply is retried
        try
        {
            if (!GuardConfig.Bool("exampleGuard", true)) // 2. config gate FIRST
            {
                Log.Info(Tag + " DISABLED (guardconfig exampleGuard=false) — <vanilla consequence>");
                Diag.Report(Component, true, "disabled by config");  // off on purpose is HEALTHY
                return;
            }
            MethodInfo target = AccessTools.Method(typeof(Foo), "Bar");  // 3. resolve BY NAME
            if (target == null)                                          // 4. self-disable, no throw
            {
                Log.Info(Tag + " inactive — Foo.Bar not resolved (game update?)");
                Diag.Report(Component, false, "members not resolved");
                return;
            }
            harmony.Patch(target, new HarmonyMethod(typeof(ExampleGuard), nameof(Prefix)));
            _applied = true;
            Log.Info(Tag + " active — <what the player gets>");
            Diag.Report(Component, true, "");            // 5. EVERY exit path reports
            SelfHealing.RegisterTest(SelfTest);          // 6. members AND decision logic
        }
        catch (Exception ex)
        {
            Log.Info(Tag + " apply failed: " + ex.Message);
            Diag.Report(Component, false, ex.Message);
        }
    }

    private static bool Prefix()
    {
        try { /* … */ SelfHealing.RecordFire(Component); return false; }  // 7. count each fire
        catch (Exception ex) { Log.Info(Tag + " " + ex.Message); return true; }  // fail open → vanilla
    }

    internal static void OnMissionInit()   // 8. per-battle counters, depth flags, cached parties
    { _blocked = 0; _announced = false; _cachedParty = null; }

    private static SelfHealing.TestResult SelfTest()
    {
        bool pass = AccessTools.Method(typeof(Foo), "Bar") != null  // re-resolve the members
                    && WantSuppress(broken: true) && !WantSuppress(broken: false);  // pin the table
        return SelfHealing.TestResult.Of(Component + ".contract", pass, "<detail>");
    }
}
```

Wiring — the exact line, in the always-on block of `PayloadEntry.Apply` (`Payload/PayloadEntry.cs:49-73`):

```csharp
ExampleGuard.Apply(harmony);
```

plus `ExampleGuard.OnMissionInit();` in `PayloadEntry.OnMissionInit` (`:137-143`), and a retry in
`OnBeforeInitialModuleScreen` (`:117-126`) if BannerlordTogether can load late
(`SiegeCommandGuard.RetryBt`, `:142-155`); a load-time fix goes first, before `harmony.PatchAll`
(`:42,45`). `Apply` must not swallow — it rethrows so the harness keeps the previous generation
(`:110-114`). Live examples: `SiegeCommandGuard.cs:87-109`, `CoopCommandSplit.cs:204,259,416-441`.
Tests clear per generation, fire counts do not (`Harness/SelfHealing.cs:44-57,97-105`).

## Ids: one convention, one documented exception

kebab-case; the `Diag.Report` component id **is** the `SelfHealing.RecordFire` id; the self-test is
`"<component>.contract"` — only the pipeline suites deviate (`pregnancy-sync.loopback`,
`stash-sync.loopback`, `client-bootstrap-fix.wiring`). The exception: the two deployment finalizers
fire as `setup-teams-guard` / `finish-deployment-guard` (`Payload/DeploymentCrashGuards.cs:106,127`)
under the single `deployment-guards` component — two rows in `GUARD ACTIVITY:`, one in `MOD HEALTH:`.
All three ids get a row in `docs/FIX-REFERENCE.md` § *Index 5: MOD HEALTH / SELFTEST component id →
file / README item*.

## `critical: true` is earned

It puts a warning on the player's screen (`Harness/Diag.cs:71-99`) — use it only when the fix's
absence re-exposes a **crash-to-desktop** or makes **battles unplayable**, never for a degraded
feature. The complete set: `deployment-guards` (`Payload/DeploymentCrashGuards.cs:42,48`),
`movementorder-typeinit` (`Payload/MovementOrderTypeInitGuard.cs:65,78,85,92`),
`client-bootstrap-fix` (`Payload/ClientBootstrapFix.cs:71,78`), `bg-tick-budget-guard` on an
unresolved `TryBackgroundCampaignTick` (`Payload/BackgroundTickBudgetGuard.cs:66`), and `battle-mode`
when a chokepoint hook is missing or `Apply` throws (`Payload/BattleMode.cs:130,136`) — an unresolved
lift target degrades and is **not** critical: it costs one lifted method, not the player side. Adding
or removing one updates the same list in `CLAUDE.md` § *Conventions for guards/fixes*, same commit.

## Health: no guard is exempt any more

`DeploymentCrashGuards` is no longer the anti-pattern: its attribute finalizers are installed by
`harmony.PatchAll`, which reports nothing, but `DeploymentCrashGuardHealth` runs straight after
(`Payload/PayloadEntry.cs:45-46`), verifies they really sit on `SetupTeams`/`FinishDeployment`, reports
and registers a self-test (`Payload/DeploymentCrashGuards.cs:26-49`). Attribute patching is fine —
pair it with a health class. `battle-mode`, `encounter-loop-guard`, `deployment-guards`,
`party-ai-guard`, `hero-creation-guard` and `movementorder-typeinit` now all report health and register
a `.contract` test; the maintained list of what still never reaches `MOD HEALTH:` is
`docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does not cover* — wire new code up, do not join it.

**A decision point is hooked by its guard, never by a tracer.** `TracePatches` is log-only, literally
so since v1.3.2 (`Payload/TracePatches.cs:14-21`). `BattleMode`'s `StartBattle`/`OpenNew` decisions and
`EncounterLoopGuard`'s `Finish` stamp used to live in it, so under the default `tracing=false` the
first solo battle ran with the player side stripped and the breaker could never trip
(`Payload/BattleMode.cs:110-137`, `Payload/EncounterLoopGuard.cs:70-73,136`).

## Logging

- **One tag per guard**, a `const string Tag` used on every line — eight files do it
  (`SiegeCommandGuard.cs:57`, `CoopCommandSplit.cs:46`, `BattleMode.cs:46`,
  `DeploymentCrashGuards.cs:23`, `EncounterLoopGuard.cs:31`, `MovementOrderTypeInitGuard.cs:47`,
  `PartyAiCrashGuard.cs:33`, `ClientHeroCreationGuard.cs:30`).
- **`[GATE]` and `[IDENTITY]` are already shared by two components each** (`CivilianGateCloseFix` +
  `SiegeGatePromptFix`; `CoopHeroIdentityLock` + `PlayerIdentityGuard`) and must not grow — a grep on
  either mixes unrelated events, which the README warns players about (§ *Diagnostics & robustness*,
  item 27's grep-tag legend). Register a new tag there and in `docs/FIX-REFERENCE.md` § *Index 1*.
- **High-frequency lines go through `TraceThrottle.Emit(key, msg)`** (`Payload/TraceThrottle.cs:38-84`),
  never `Log.Info`: repeats collapse to `[repeat] key ×N in Ys`. A per-tick tracer without it filled
  the 8 MB log in minutes and rotated the evidence away (`:7-12`). Put the *deciding* value in the
  dedup key — `[TIME]` keys on the vetoing prefix (`Payload/TimeTrace.cs:126-128`).

## Per-mission state, load order, co-op scoping

`OnMissionInit()` resets counters, depth flags and cached parties, called from
`Payload/PayloadEntry.cs:137-143` (`SiegeCommandGuard.cs:157-166`, `CoopCommandSplit.cs:108-121`);
reentrancy flags are `[ThreadStatic]` (`SiegeCommandGuard.cs:62-66`). `PlayerIdentityGuard` is the
exception — it resets in `Tick` via `ReferenceEquals(Mission.Current, _lastMission)`
(`Payload/PlayerIdentityGuard.cs:29,49-51`).

`MovementOrderTypeInitGuard.ApplyEarly(harmony)` runs first, before `PatchAll` (`PayloadEntry.cs:38-45`).
Any fix that must run before the game touches a type goes there — patching `Formation`/`OrderController`
makes the CLR prepare the `beforefieldinit` `MovementOrder` struct, and a failed type initializer is
cached for the process (`Payload/MovementOrderTypeInitGuard.cs:14-34`); its self-test is the load-time
exemplar, pinning the premise, the ctor, the one transpiled site and the null-safe helper (`:152-166`).
Load-time fixes do **not** survive a hot-reload — `HOTRELOAD.md` § *What a reload cannot do (fresh
launch required)* lists the four cases.

Scope every behaviour change by role through `PeerDetection`, never hand-rolled reflection: the class
and its tri-state contract ("values only; nulls mean unknown") are at `Payload/BattleMode.cs:614-618`,
members `IsClient()` (`:755`), `AnyRemotePeerConnected()` (`:760`), `ReadCoopStaticBool/String`
(`:808,814`), `FindCoopType` (`:706`), `Snapshot()` (`:646`). `null` means unknown, so compare
`== true` (`SiegeCommandGuard.cs:234`) and **fail toward co-op** (`AnyRemotePeerConnected() != false`):
a wrong "alone" sabotages a live session.

## Adding a fix — the checklist

1. New `Payload/<Name>.cs` on the skeleton; wire into `PayloadEntry.Apply` (and
   `OnMissionInit`/`Tick`/the module-screen retry if needed).
2. New config key: the key **with its `_key` explanation line** in `GuardConfig.DefaultJson`
   (`Harness/GuardConfig.cs:82-115`) *and* a row in the README `## Config` table. That template is
   written only when `guardconfig.json` is absent, so an existing install keeps its old text.
3. `README.md` — a numbered item under § *Crash fixes*, § *Co-op & gameplay fixes* or
   § *Diagnostics & robustness*, plus the tag in that section's legend.
4. `docs/FIX-REFERENCE.md` — a full entry and a row in each index that applies, Index 5 included;
   `CHANGELOG.md` under the version being released. Details: `.claude/rules/blt-docs-tools.md`;
   shipping it: `docs/RELEASE.md`.
5. Newly proven engine or BT behaviour → `docs/ENGINE-NOTES.md` / `docs/BT-INTERNALS.md` with evidence
   and date; a reverted attempt → `docs/MODDING-PITFALLS.md`; a technique → `docs/MODDING-GUIDE.md`.
