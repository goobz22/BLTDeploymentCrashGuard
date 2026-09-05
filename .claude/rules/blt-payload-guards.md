---
paths: ["Payload/**", "tests/**"]
---

# Payload guards — conventions

One concern per `Payload/*.cs`, header comment stating the bug, the IL evidence and the fix (model:
`Payload/SiegeCommandGuard.cs:14-54`). The payload hot-reloads whole — statics are fresh every
generation. The `tests/` suites **link** payload sources rather than copying them
(`tests/StashPayloadTest/StashPayloadTest.csproj:21-24`) — build them after editing a
`Payload/*Sync/*Data.cs` or `*WireFraming.cs`.

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
                Log.Info(Tag + " DISABLED (exampleGuard=false) — <vanilla consequence>");
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

    private static SelfHealing.TestResult SelfTest()  // re-resolve members AND pin the decision table
    {
        bool pass = AccessTools.Method(typeof(Foo), "Bar") != null
                    && WantSuppress(broken: true) && !WantSuppress(broken: false);
        return SelfHealing.TestResult.Of(Component + ".contract", pass, "<detail>");
    }
}
```

Wiring — the exact line, in the always-on block of `PayloadEntry.Apply` (`PayloadEntry.cs:49-73`):

```csharp
ExampleGuard.Apply(harmony);
```

plus `ExampleGuard.OnMissionInit();` in `PayloadEntry.OnMissionInit` (`:137-143`), and a retry in
`OnBeforeInitialModuleScreen` (`:117-126`) if BannerlordTogether can load late
(`SiegeCommandGuard.RetryBt`, `:142-155`); a load-time fix goes first, before `harmony.PatchAll`
(`:42,45`). `Apply` must not swallow — it rethrows so the harness keeps the previous generation
(`:110-114`). Tests clear per generation, fire counts do not
(`Harness/SelfHealing.cs:44-57,97-105`).

## Ids: one convention, one exception

kebab-case; the `Diag.Report` component id **is** the `SelfHealing.RecordFire` id; the self-test is
`"<component>.contract"` — only the pipeline suites deviate (`pregnancy-sync.loopback`,
`stash-sync.loopback`, `client-bootstrap-fix.wiring`). The exception: the two deployment finalizers
fire as `setup-teams-guard` / `finish-deployment-guard` (`DeploymentCrashGuards.cs:106,127`) under the
single `deployment-guards` component — two rows in `GUARD ACTIVITY:`, one in `MOD HEALTH:`. All three
ids get a row in `docs/FIX-REFERENCE.md` § *Index 5*.

## `critical: true` is earned

It puts a warning on the player's screen (`Harness/Diag.cs:71-99`) — use it only when the fix's
absence re-exposes a **crash-to-desktop** or makes **battles unplayable**, never for a degraded
feature. The complete set: `deployment-guards` (`DeploymentCrashGuards.cs:42,48`),
`movementorder-typeinit` (`MovementOrderTypeInitGuard.cs:65,78,85,92`), `client-bootstrap-fix`
(`ClientBootstrapFix.cs:71,78`), `bg-tick-budget-guard` on an unresolved `TryBackgroundCampaignTick`
(`BackgroundTickBudgetGuard.cs:66`), `battle-mode` when a chokepoint hook is missing or `Apply` throws
(`BattleMode.Apply`) — an unresolved lift target degrades and is **not** critical: it costs one
lifted method, not the player side. Adding or removing one updates the same list in `CLAUDE.md`
§ *Conventions for guards/fixes*, same commit.

## Health: no guard is exempt any more

`DeploymentCrashGuards` is no longer the anti-pattern: its attribute finalizers are installed by
`harmony.PatchAll`, which reports nothing, but `DeploymentCrashGuardHealth` runs straight after
(`PayloadEntry.cs:45-46`), verifies they really sit on `SetupTeams`/`FinishDeployment`, reports and
registers a self-test (`DeploymentCrashGuards.cs:26-49`). Attribute patching is fine — pair it with a
health class. Every guard, fix and advisor now reports health and registers a `.contract` test —
the 2026-09-04 pass added `battle-mode`, `encounter-loop-guard`, `deployment-guards`,
`party-ai-guard`, `hero-creation-guard` and `movementorder-typeinit`, then `map-click-speed`,
`time-flow`, `time-enforcement-guard`, `share-time-control`, `player-identity-guard` and
`bootstrap-watch`. A tick-driven component gets an `Apply()` that only pins members and registers
(`PlayerIdentityGuard`, `ShareTimeControl`, `BootstrapWatch`). A guard whose BT type is not loaded
yet reports **healthy as `inert — BannerlordTogether not loaded`**, never a silent return, and
because health is keyed by component the module-screen / game-start retry replaces that entry.
What still never reaches `MOD HEALTH:` — the tracers, `PayloadEntry`, `PeerDetection` — is listed
in `docs/DIAGNOSTICS.md` § *What `MOD HEALTH:` does not cover*; wire new code up, do not join it.

Likewise a decision point is hooked by its own guard, never by a tracer: `BattleMode`'s
`StartBattle`/`OpenNew` decisions and `EncounterLoopGuard`'s `Finish` stamp are always-on
(`BattleMode.Apply`, `EncounterLoopGuard.Apply`); `TracePatches` is log-only.

State that must outlive a payload generation goes in the harness `ISharedState` bag as BCL types
only — `object[]` records, strings, `MethodInfo`s — never a payload class, whose identity is fresh
every generation (`BattleMode.StashKey`, `PregnancySyncGuard` § `ListenerOwnerKey`).

## Logging

- **One tag per guard**, a `const string Tag` used on every line — eight files do it, e.g.
  `SiegeCommandGuard.cs:57`, `BattleMode.cs:46`.
- **`[GATE]` and `[IDENTITY]` are already shared by two components each** and must not grow — a grep
  on either mixes unrelated events, which the README warns players about (§ *Diagnostics &
  robustness*, item 27's grep-tag legend). Register a new tag there and in FIX-REFERENCE § *Index 1*.
- **High-frequency lines go through `TraceThrottle.Emit(key, msg)`** (`Payload/TraceThrottle.cs:38-84`),
  never `Log.Info`: repeats collapse to `[repeat] key ×N in Ys`. A per-tick tracer without it filled
  the 8 MB log in minutes and rotated the evidence away (`:7-12`). Put the *deciding* value in the
  dedup key — `[TIME]` keys on the vetoing prefix (`TimeTrace.cs:126-128`).

## Per-mission state, load order, co-op scoping

`OnMissionInit()` resets counters, depth flags and cached parties, called from
`Payload/PayloadEntry.cs:137-143` (`SiegeCommandGuard.cs:157-166`); reentrancy flags are
`[ThreadStatic]` (`:62-66`). `PlayerIdentityGuard` is the exception — it resets in `Tick` via
`ReferenceEquals(Mission.Current, _lastMission)` (`PlayerIdentityGuard.cs:29,49-51`).

`MovementOrderTypeInitGuard.ApplyEarly(harmony)` runs first, before `PatchAll` (`PayloadEntry.cs:38-45`).
Any fix that must run before the game touches a type goes there — patching `Formation`/`OrderController`
makes the CLR prepare the `beforefieldinit` `MovementOrder` struct, and a failed type initializer is
cached for the process (`MovementOrderTypeInitGuard.cs:14-34`); its self-test is the load-time exemplar,
pinning the premise, the ctor, the one transpiled site and the null-safe helper (`:152-166`). Load-time
fixes do **not** survive a hot-reload — `HOTRELOAD.md` § *What a reload cannot do (fresh launch
required)* lists the four cases.

Scope every behaviour change by role through `PeerDetection`, never hand-rolled reflection: the class
and its tri-state contract ("values only; nulls mean unknown") are at `Payload/BattleMode.cs:614-618`,
members `IsClient()` (`:755`), `AnyRemotePeerConnected()` (`:760`), `ReadCoopStaticBool/String`
(`:808,814`), `FindCoopType` (`:706`), `Snapshot()` (`:646`). `null` means unknown, so compare
`== true` and **fail toward co-op** (`AnyRemotePeerConnected() != false`): a wrong "alone" sabotages a
live session.

## Adding a fix — the checklist

1. New `Payload/<Name>.cs` on the skeleton, wired into `PayloadEntry.Apply`.
2. New config key: the key **with its `_key` explanation line** in `GuardConfig.DefaultJson`
   (`Harness/GuardConfig.cs:82-115`) *and* a row in the README `## Config` table.
3. `README.md` — a numbered item under § *Crash fixes*, § *Co-op & gameplay fixes* or
   § *Diagnostics & robustness*, plus the tag in that section's legend.
4. `docs/FIX-REFERENCE.md` — a full entry and a row in each index that applies, Index 5 included;
   `CHANGELOG.md` under the version being released. Details: `blt-docs-tools.md`; shipping it:
   `docs/RELEASE.md`.
5. Newly proven engine or BT behaviour → `docs/ENGINE-NOTES.md` / `docs/BT-INTERNALS.md` with evidence
   and date; a reverted attempt → MODDING-PITFALLS; a technique → MODDING-GUIDE.
