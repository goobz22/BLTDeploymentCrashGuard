# What bit us: pitfalls, reverted attempts, and gotchas (Bannerlord + BannerlordTogether modding)

Companion to `docs/MODDING-GUIDE.md`. The guide says how to do things; this file says what went wrong
when we did them, what we tried and reverted, and what is guarding each mistake today.

Everything here was paid for in a real session: a crash, a freeze, a silently disabled fix, a wiped
item, a lost log. Entries cite the file and line where the lesson is now encoded, and the CHANGELOG
date where there is one.

**How to read an entry**

- **Pitfall** — the mistake, in one line.
- **What happened** — the concrete failure and its evidence.
- **Lesson** — the rule that came out of it.
- **Now** — where that rule is enforced or documented in this repo.

Boxes marked **Good to know** are not mistakes; they are facts or techniques worth having before you
start, placed next to the pitfall they prevent.

**Related documents**

| Document | What it holds |
|---|---|
| `docs/MODDING-GUIDE.md` | The techniques themselves, presented positively. |
| `docs/ENGINE-NOTES.md` | Engine facts proven from IL, with the proof and the date. |
| `docs/BT-INTERNALS.md` | BannerlordTogether internals as observed from IL. |
| `docs/DIAGNOSTICS.md` | How to investigate without guessing. |
| `docs/FIX-REFERENCE.md` | Per-fix table: file, class, tag, config key, patched members, limits. |
| `tools/il-probes/README.md` | The IL/reflection probes used to prove most of this. |
| `HOTRELOAD.md` | The dev hot-reload workflow and its known gaps. |
| `CHANGELOG.md`, `UPSTREAM_BUG_REPORT.md` | Dated history and the BT-side defects. |

**The five shapes that account for most of this file**

1. **Silence.** By-name reflection, `catch {}`, config off-switches, whitelist tracers and caps all
   fail by producing nothing. Nothing looks exactly like "the bug did not happen".
2. **Identity.** Which assembly, which generation, which enum value, which overload, which of two
   objects the peer means. Almost every "the patch applied and nothing changed" is an identity bug.
3. **Ordering.** Load order, patch order, apply-before-unpatch, prefix-before-postfix, tail replay.
   A correct fix applied second still loses.
4. **The manifested location is not the root-cause location.** Every crash in this repo whose guard
   was written at the throw site turned out to have its defect somewhere else.
5. **Suppression is not repair.** A caught exception, a skipped method, a cleared cache and a
   blocked write each leave state behind that somebody still has to think about.

**Sections**

| # | Section | Entries |
|---|---|---|
| 0 | [Reverted attempts and dead ends](#0-reverted-attempts-and-dead-ends) | table |
| 1 | [Harmony](#1-harmony) | H1–H25 |
| 2 | [.NET Framework and the CLR](#2-net-framework-and-the-clr) | N1–N26 |
| 3 | [Engine — mission lifecycle and deployment](#3-engine--mission-lifecycle-and-deployment) | E1–E7 |
| 4 | [Engine — formations, orders, and command authority](#4-engine--formations-orders-and-command-authority) | E8–E15 |
| 5 | [Engine — time control](#5-engine--time-control) | E16–E28 |
| 6 | [Engine — encounters and the campaign map](#6-engine--encounters-and-the-campaign-map) | E29–E34 |
| 7 | [Engine — heroes, clans, and campaign actions](#7-engine--heroes-clans-and-campaign-actions) | E35–E46 |
| 8 | [Engine — settlements and scene objects](#8-engine--settlements-and-scene-objects) | E47–E53 |
| 9 | [Engine — UI, screens, and view models](#9-engine--ui-screens-and-view-models) | E54–E58 |
| 10 | [BannerlordTogether — interoperating with a peer mod](#10-bannerlordtogether--interoperating-with-a-peer-mod) | B1–B20 |
| 11 | [Co-op sync — wire protocols and shared state](#11-co-op-sync--wire-protocols-and-shared-state) | S1–S17 |
| 12 | [Tooling, build and deploy](#12-tooling-build-and-deploy) | T1–T24 |
| 13 | [Process and diagnosis discipline](#13-process-and-diagnosis-discipline) | P1–P17 |

---

## 0. Reverted attempts and dead ends

Things that were built, shipped or drafted and then taken back out. Do not re-try these without new
evidence; each row cost a session.

| Attempt | Why it failed | What replaced it |
|---|---|---|
| Encounter loop breaker as a **pure rate limiter** (N applications in a window) | Would eat a partner's legitimate join storm — join requests look like a burst | Signature gate: only applications within 4 s of a local `PlayerEncounter.Finish` count (`Payload/EncounterLoopGuard.cs:37-41,109-112`) |
| Suppress `FinishDeployment`'s exception and stop there | Battle permanently frozen: `AllowAiTicking` still false, `DisableDying` still true, player agent non-detachable | Finalizer that replays the method's tail step by step (`Payload/DeploymentCrashGuards.cs:29-33,45-77`) |
| Replace `ClanPartiesVM.GetNewPartyLeaderCandidates`'s `__result` with a non-generic `ArrayList` | Installs cleanly, then crashes the leader popup on the caller's generic `foreach` — a crash guard introducing a crash | Enumerate for logging only; the result is **never** replaced (`Payload/ClanPartyCreationAdvisor.cs:119-121`; CHANGELOG.md:101-103) |
| Time enforcement v1: **prefix-skip** BT's `EnforcePlaySpeed` (`return false`) | BT's internal time state machine went stale; plausibly produced the stuck shared pause seen 2026-08-19 00:32–00:35 | Run-but-neutralize: let it run, block only its setter writes inside its own execution window (`Payload/TimeEnforcementGuard.cs:14-21`) |
| Scope the solo time neutralizer to the campaign map (2026-09-04 hypothesis for the sideways/folded character) | Did not move the symptom at all; the sideways character is a separate, likely GPU-side vanilla issue | Reverted. `docs/ENGINE-NOTES.md` §4 "Time control in co-op (pre-2026-09-04)" records it as a dead end |
| Three quieter Harmony approaches (postfix among them) to rewrite `ClanModeSyncBehavior.CurrentMode` | All installed cleanly and did **nothing** — a postfix cannot rewrite a value-typed result of a foreign *internal* enum | Transpiler injecting a preamble, proven in a purpose-built rig (`scratchpad/HarmonyEnumTest`); see `Payload/ClanModeSoloFix.cs:22-26` |
| Naive full-roster snapshot apply for stash sync (`Clear()` then re-add what the payload names) | Silently deleted items the sender structurally could not mention — a player-crafted sword, irrecoverably, with no log line | Preserve-then-clear-then-reapply, keyed against a `HashSet` of ids the payload names (`Payload/StashSync/StashSyncGuard.cs:324-345,362-365`) |
| `item.WeaponDesign != null` as the "player-crafted" test | True for **every** `<CraftedItem>` definition — ~283 ordinary vanilla weapons on Native v1.4.8 stopped syncing, permanently, with a log line worded as if that were expected | `ItemObject.IsCraftedByPlayer`, plus a StringId round-trip as a second clause (`Payload/StashSync/StashSyncGuard.cs:213-234`) |
| Defeat `Assembly.LoadFrom` dedup with a unique **AssemblyVersion** (shipped in v1.2.3) | LoadFrom dedups by simple **name** only; the version never mattered. Field-proven 2026-09-01 17:37: `LoadFrom deduped to already-loaded 1.2.7.42191` | A unique assembly **name** per build (`Payload/BLTDeploymentCrashGuard.Payload.csproj:11-18`; CHANGELOG.md:119-124) |
| Fix the split 0Harmony identity with an `AssemblyResolve` pin (2026-08-30) | `Assembly.Load(bytes)` resolved via default-context probing, which **succeeded** against the game's own 0Harmony 2.4.2.0 — so the resolver never fired | Change the load **context**: LoadFrom a shadow copy in the module directory (`Harness/HotReload.cs:279-287`; CHANGELOG.md:213-220) |
| Re-using a per-**generation** shadow path (`.genN`) after a failed attempt | LoadFrom caches path → assembly, so the retry returned the first attempt's assembly without reading the new file. Field-proven 2026-09-01 17:43 | Unique path per **attempt**: pid + gen + `UtcNow.Ticks` (`Harness/HotReload.cs:307-312`) |
| Clearing BT's `RuntimeDataCache` as the cure for `BootstrapAborted` | Reproduces identically with the `.rdc` present (2026-08-19 20:46) and removed (21:41) — `diskLoad=False`, all-`(-1)` sentinels both ways, and no cache is ever written | `BootstrapWatch` is a detector plus hygiene; the root fix is `ClientBootstrapFix` priming the static mirrors (`UPSTREAM_BUG_REPORT.md:16-22` vs `Payload/BootstrapWatch.cs:97-99`) |
| Guard the perk/character-development code where the issue-quest NRE manifests | The defect is that a **dead hero is reactivated and re-added to the party at all**; guarding `HeroDeveloper` would hide it forever | Block `dead → Active` in `Hero.ChangeState` and fix the `IsAlive`-less loop in `IssueManager` (`Payload/DeadHeroReactivationFix.cs:9-27,108-147`) |
| "Fix" the hideout sneak-in spawn so you are not a soldier | IL decode proved it is vanilla design — your own hero re-dressed in `Hero.StealthEquipment` with enemy colours, orders withheld until the stealth→battle transition. Changing it would break the designed mission | An on-screen explainer plus a command-ownership repair (`Payload/StealthHideoutAdvisor.cs:8-26`; CHANGELOG.md:107-116) |
| Load every verbose tracer unconditionally (v1.0.x) | Mission / menu / control / time / coop-battle / role tracers on in normal play | All gated behind `"tracing": true` (CHANGELOG.md:342-344) |
| Single-slot log rotation (`log` → `.1` overwrite) | One burst discarded the exact session being chased (2026-09-04) | Rolling window of segments (`Harness/Log.cs:78-87`; CHANGELOG.md:13-16) |
| Check the log size once per launch | The check ran while the file was still small; `CrashGuard.log` reached **283 MB**. The earlier, milder case had already shown the second cost: at 12 MB it broke log streaming, not just evidence retention (CHANGELOG.md:347-348) | Amortised re-check every N writes — rotation has to be **periodic**, re-checked during the session, not once per launch (`Harness/Log.cs:84-93`; CHANGELOG.md:310-311) |

---

## 1. Harmony

### H1 · Suppressing the throw is not replaying the tail

- **What happened** — A finalizer that only ate `DeploymentMissionController.FinishDeployment`'s
  exception left the battle permanently frozen: `AllowAiTicking` still false, `DisableDying` still
  true, the player agent still non-detachable and AI-controlled, the deployment behavior still
  attached. No crash, no game either.
- **Lesson** — When the method you are suppressing has side effects the game depends on, the
  finalizer must replay the remaining steps, each in its own `try/catch`, so one failing step does
  not abort the rest.
- **Now** — `Payload/DeploymentCrashGuards.cs:29-33,45-77`.

### H2 · Suppressing an exception from a method that returns through by-ref parameters

- **What happened** — The caller reads uninitialised state. The exception is gone and the corruption
  is silent.
- **Lesson** — A finalizer must set **every** by-ref/out parameter and `__result` to a valid,
  domain-neutral value. Here: `AiBehavior.Hold` + a null target object + the party's own `Position`;
  a non-null **empty** `List<TextObject>` in the incident finalizers.
- **Now** — `Payload/PartyAiCrashGuard.cs:112-114`; `Payload/MapIncidentCrashGuard.cs:287-290,302-305`.

### H3 · A recovery block that throws hands back the null it was fixing

- **What happened** — `ClientHeroCreationGuard`'s finalizer sets `__result = null` if its own
  recovery path throws — the exact value that caused the original crash.
- **Lesson** — A fallback needs its own thought about what the caller does with the fallback value.
  Prefer substituting a result of the **same shape the method returns in its own edge cases**, so
  callers stay on a path native code already supports.
- **Now** — `Payload/ClientHeroCreationGuard.cs:38,47-76` (the substitute), `:70-74` (the trap).

### H4 · Targeting a compiler-generated lambda by its number

- **What happened** — `b__1` / `b__2` numbering changes on any recompile of the game, so a patch
  keyed on the index silently moves to a different lambda — or to the harmless preview-text lambda.
- **Lesson** — Select the lambda by what its IL actually **calls**. Here only
  `<SiegeProgressChange>b__N` methods returning `List<TextObject>` whose IL calls
  `PlayerSiege.get_PlayerSiegeEvent` are patched; the preview-text lambda is deliberately left alone.
- **Now** — `Payload/MapIncidentCrashGuard.cs:33-35,66-80,120-158`; CHANGELOG.md:255-257.

### H5 · A naive IL byte scan misreads operand bytes as opcodes

- **What happened** — A byte inside a 4-byte metadata token can equal `0x28` (`call`). Resolving it
  throws, and a scanner that stops on the first failed resolve stops early; one that advances `i += 4`
  unconditionally desynchronises from the instruction stream.
- **Lesson** — Put `ResolveMember` in its own `try/catch`, keep scanning after a failed resolve
  ("not a real call site (opcode byte inside operand data)"), and advance `i += 4` **only after a
  successful resolve**.
- **Now** — `Payload/MapIncidentCrashGuard.cs:136-151`.

> **Good to know — reading a lambda's captured state.**
> `GetField("amountGetter", Instance|Public|NonPublic)` on the closure display class yields the exact
> `Func<float>` vanilla itself would have called, so a replacement prefix reproduces the real effect
> instead of guessing (`Payload/MapIncidentCrashGuard.cs:248-259`). Rebuilding vanilla's own
> localization id (`{=C0kUpB48}…` with `AMOUNT = MathF.Round(amount*100f)`) means the player cannot
> tell the fix from vanilla and the string stays translated in every shipped language
> (`:231-234`). Disambiguating an overload by `ReturnType == typeof(List<TextObject>)` is cheap and
> update-resistant compared with building an exact parameter-type array (`:73,:94`).

### H6 · A postfix `__result` write-back over a generic return installs cleanly, then crashes the caller

- **What happened** — Harmony's patch-time check is `paramType.IsAssignableFrom(returnType)`, so
  `void Postfix(IEnumerable __result)` installs happily against `IEnumerable<ClanCardSelectionItemInfo>`.
  Harmony then emits `Ldloca` on a result slot typed with the **real** return type and the postfix
  does a raw `stind.ref` with no cast. Vanilla's `foreach` interface-dispatches on the substituted
  `ArrayList` and throws — a crash guard crashing the exact path it instruments. The write-back was
  also unnecessary: a C# `yield` iterator returns a fresh enumerator per `GetEnumerator`.
- **Lesson** — Enumerate a vanilla iterator for **logging only**; never substitute your own
  enumeration into a live UI path.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:119-155` (the current safe form documents "the result
  is NEVER replaced"); `.git/commit-review-cache.json:282,285` holds the BLOCK verdict (`:282`) and
  the finding text (`:285`) — that cache lives inside `.git/`, so it is machine-local and absent from
  a clone; the distributed record of the same finding is CHANGELOG.md:101-103.

### H7 · `AccessTools.Field` returns null for an auto-property

- **What happened** — The backing field is `<Name>k__BackingField`, so a field-only read of a view
  model member returned null and every card read `IsDisabled == false` — a silent false negative that
  also killed the "no clan member can lead a party" hint. No exception anywhere.
- **Lesson** — Read foreign members field-first with a **property fallback**, and make the self-test
  assert that each member resolves as *either* shape, so an upstream field→property refactor becomes
  loud instead of degrading every read to a default.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:157-167,332-336`; `.git/commit-review-cache.json:285`
  (machine-local, not in a clone — see **H6**).

### H8 · Harmony postfixes cannot rewrite a value-typed result of a foreign **internal** enum

- **What happened** — Three quieter approaches to change `ClanModeSyncBehavior.CurrentMode` installed
  cleanly and did nothing, silently, in a purpose-built rig (`scratchpad/HarmonyEnumTest`). "Looked
  applied, did nothing" is the dangerous failure mode when patching obfuscated third-party code.
- **Lesson** — Go to a transpiler that injects a preamble, and prove it in a rig rather than in the
  game. Emit `Call decider` / `Brfalse label` / `Ldc_I4_1` / `Ret`, attaching the label to the **first
  original instruction** so the original body stays byte-identical when the decider says no.
- **Now** — `Payload/ClanModeSoloFix.cs:22-26,64-82`.

### H9 · The decider a transpiler calls must live in a **public** static class

- **What happened** — The emitted call site ends up inside the foreign assembly, so an internal
  target is an accessibility violation at JIT time — a runtime failure no compiler warns about.
- **Lesson** — Any method a transpiler emits a call to is public API, whether you meant it to be or not.
- **Now** — `Payload/ClanModeSoloFix.cs:122-123`.

### H10 · JIT inlining makes a correctly-applied IL patch invisible

- **What happened** — Callers jitted **before** a patch keep the inlined original, so a transpiler
  that "applied successfully" changes nothing for existing call sites. This is the single most
  confusing failure mode in the repo.
- **Lesson** — Any getter-level patch whose value other code reads must be installed at **module
  load**, before campaign code compiles. And read the value back through a reflection `Invoke`, which
  always goes through the Harmony detour — a normal call may hit an inlined copy.
- **Now** — `Payload/ClanModeSoloFix.cs:26-29` (the caveat), `:86-101` (`ReadLiveMode`), consumed at
  `Payload/MarriageBarterGuard.cs:83`.

### H11 · A prefix returning `false` suppresses the original **and every event it would raise**

- **What happened** — Used deliberately in two opposite directions in the same codebase. Blocking
  `dead → Active` means "leave the hero Dead, fire no activation event" — which is the whole point,
  since `OnHeroActivatedEvent` is what reaches the null `HeroDeveloper`. But the ill-hero cure
  returns **true** precisely so vanilla's other daily work still happens.
- **Lesson** — Decide explicitly whether you want the method's side effects to happen. Prefer
  **cure-forward** (repair the state, return true) over skipping when the method owns anything else.
- **Now** — `Payload/DeadHeroReactivationFix.cs:139`; `Payload/IllnessDeathGuard.cs:127`.

### H12 · Harmony runs **all** prefixes even when one returns `false`

- **What happened** — A tracer prefix logged a time-mode change that another patch was about to
  suppress, so the log said the change happened when it did not.
- **Lesson** — Capture intent in the prefix, re-read the live value in the **postfix**, and print
  "change SUPPRESSED/ALTERED by another patch". That two-phase capture is the only practical way to
  see another mod's veto from inside your own log.
- **Now** — `Payload/TimeTrace.cs:19-22,83-128`.

### H13 · Clearing a `[ThreadStatic]` scope flag in a **postfix** leaks it forever

- **What happened** — A postfix does not run when the original throws, so the scope flag stays set
  and every later write on that thread is blocked permanently.
- **Lesson** — Set scope flags in a prefix and clear them in a **finalizer**, returning `__exception`
  unchanged so the exception still propagates. The same applies to depth counters: decrement in a
  finalizer, guard the decrement with `> 0`, and zero every counter in `OnMissionInit` so one battle's
  leak cannot poison the next.
- **Now** — `Payload/TimeEnforcementGuard.cs:35-36,168,180-184`; `Payload/MapClickSpeedKeeper.cs:25-26,68-77`;
  `Payload/SiegeCommandGuard.cs:453-496,157-166`.

### H14 · Returning the wrong default from a `catch` inside a bool prefix

- **What happened** — A prefix that swallows an internal error and returns the wrong value either
  skips vanilla entirely or lets the AI's behaviour run twice.
- **Lesson** — Every `catch` in a patch returns the **vanilla-preserving** value: `return true` in a
  bool prefix (let the original run), a plain `return` in a void prefix, `return __exception` in a
  depth-counter finalizer — and `return null` **only** where suppression is the deliberate feature.
- **Now** — `Payload/SiegeCommandGuard.cs:331-334,302-306,467,481`; `Payload/CivilianGateCloseFix.cs:113`;
  the fail-open protocol is stated at `Payload/ClientBootstrapFix.cs:147-191`.

### H15 · A patch that throws is the crash you were trying to prevent

- **What happened** — Guards live on mission and campaign ticks. An exception from inside one unwinds
  into native code where there is no managed catch.
- **Lesson** — Fail-open everywhere: every prefix returns the vanilla value on internal error, every
  logging helper swallows, and the hottest hooks carry a bare `catch {}`. "A tracer must never take
  the game down."
- **Now** — `Payload/EncounterLoopGuard.cs:128-131`; `Payload/PartyAiCrashGuard.cs:94-98,117-121`;
  `Payload/BackgroundTickBudgetGuard.cs:138-140`; `Payload/MapIncidentCrashGuard.cs:146-157,171-174,206-209`;
  `Payload/TimeFlowPatch.cs:63-78`; `Payload/MapClickSpeedKeeper.cs:82-98`;
  `Payload/TimeEnforcementGuard.cs:149-177,226-254`.

### H16 · One guard's `Apply` throwing skips every later guard

- **What happened** — Guards are applied in sequence from a single entry point.
- **Lesson** — Wrap every `Apply` in its own `try/catch` so "degrade gracefully on a game update"
  holds at the module level, not just per patch.
- **Now** — the rule is enforced inside each guard's own `Apply`, which opens with a `try` and
  swallows: `Payload/IllnessDeathGuard.cs:36-63`; `Payload/ClanPartyCreationAdvisor.cs:61-98`; same
  shape at `Payload/TimeFlowPatch.cs:41-58`, `Payload/PartyAiCrashGuard.cs:35`,
  `Payload/EncounterLoopGuard.cs:53`, `Payload/MapClickSpeedKeeper.cs:31` and
  `Payload/ClientHeroCreationGuard.cs:30`. The **call site** does not wrap them —
  `Payload/PayloadEntry.cs:47-70` is 23 bare `X.Apply(harmony);` statements under one outer `try`
  (opened at `:27`, caught at `:108`), so an exception escaping any `Apply` would still skip every
  later guard.

### H17 · `Harmony.GetPatchInfo` can return null, and so can each patch collection

- **What happened** — A crash class most Harmony code hits once: `Prefixes`, `Postfixes`,
  `Finalizers` and `Transpilers` are each independently nullable.
- **Lesson** — Null-check the `Patches` object **and** every collection before enumerating.
- **Now** — `Payload/BattleMode.cs:251-255,278-283,322-338`.

### H18 · Unpatching is coarse: `Unpatch(method, HarmonyPatchType.All, owner)` removes every kind at once

- **What happened** — The stash records each patch kind separately, but lifting happens per owner, so
  one `Unpatch` call removes prefixes, postfixes, finalizers and transpilers together.
- **Lesson** — Stash per kind but expect to lift per owner; the restore path must be able to rebuild
  several kinds for one owner.
- **Now** — `Payload/BattleMode.cs:256-266` (the `foreignOwners` set) vs `:257-260` (per-kind stashing).

> **Good to know — reversible unpatching of a *foreign* mod's patches.**
> Capture owner + kind + `PatchMethod` + priority + `before[]`/`after[]` from
> `Harmony.GetPatchInfo`, `Unpatch` by owner, then rebuild with `new Harmony(originalOwner)` and
> `Patch()` — with the exact argument positions per kind (`prefix` / `null,postfix` /
> `null,null,null,finalizer` / `null,null,transpiler`), which people routinely get wrong.
> Guard the restore with an `IsPresent` check so a foreign mod that re-applied its own patch in the
> meantime does not end up with two copies running side effects twice, and dedupe the stash on
> `(kind, owner, PatchMethod)` so it does not grow across the many decision passes in a session.
> `Payload/BattleMode.cs:249-319` (stash), `:179-225` (restore), `:186-190,208,297-317,321-347`.

### H19 · Mass-unpatching by owner will rip out your own guards

- **What happened** — Harmony keys patches by owner **string**, and this mod mints a new owner id per
  hot-reload generation (`bltogether.crashguard.gen{N}`). "Is this patch mine?" therefore cannot be an
  identity question.
- **Lesson** — Make it a **prefix** question against your owner-id family before lifting anything.
- **Now** — `Payload/BattleMode.cs:91-97,286`; owner ids minted at `Harness/HotReload.cs:358-360`.

### H20 · Enumerating targets with `DeclaredOnly` + a name match

- **What happened** — The enumeration returns **every** overload, including abstract declarations,
  and silently skips inherited non-overridden implementations.
- **Lesson** — Filter `!method.IsAbstract` (an abstract declaration cannot be patched), know that
  `DeclaredOnly` excludes inherited implementations, and patch every remaining overload **by
  signature filter** rather than by naming a parameter list — e.g. `Name == "SpawnTroop" &&
  ReturnType == typeof(Agent)`. Log the count and re-assert `> 0` in the self-test.
- **Now** — `Payload/BattleMode.cs:236-244`; `Payload/CoopCommandSplit.cs:79-87,423-429`;
  `Payload/TimeFlowPatch.cs:44-53`; `Payload/TimeEnforcementGuard.cs:62-80`; `Payload/TimeTrace.cs:45-79`.

### H21 · Patching a method whose return type may change

- **What happened** — A postfix taking `bool __result` on BT's `ToggleHostManualPause` would break
  silently (or throw at patch time) the day BT changed the method to `void`.
- **Lesson** — Validate the return type explicitly before patching and refuse to install with a named
  reason (`ToggleHostManualPause(bool-return)`) rather than installing a broken hook.
- **Now** — `Payload/JoinSyncPauseEscape.cs:83-93`.

### H22 · Installing a dependent hook unconditionally

- **What happened** — The Campaign setter prefixes are only meaningful inside the scope that
  `EnforcePlaySpeed`'s hook establishes. Installing them when the scoping hook failed to resolve
  leaves a permanently-armed blocker with no owner.
- **Lesson** — Only install a dependent hook when the scoping hook actually landed (`if (count > 0)`).
- **Now** — `Payload/TimeEnforcementGuard.cs:81-83`; `Payload/MapClickSpeedKeeper.cs:52-59`.

### H23 · Fighting a peer mod's prefix on the same member

- **What happened** — BT already prefixes `DefaultEncounterGameMenuModel.GetGenericStateMenu`
  (`AutoWaitMenuPatch`), so a prefix of our own would observe a pre-BT value.
- **Lesson** — When a peer mod prefixes the method you want to observe, **postfix** it; you then log
  the final value either way, and you are not competing for prefix order.
- **Now** — `Payload/TracePatches.cs:28-30,45,193-202`.

### H24 · Blanket-blocking a low-level API takes capabilities from the player

- **What happened** — A blanket refusal of AI hand-offs would also have broken the player's own F6
  "delegate command", vanilla's death hand-off (`Team.DelegateCommandToAI`), and BT's host-side
  player-down releases. Separately, `Formation.TransferUnits` and `OrderController.TransferUnits`
  look interchangeable but are not: the castle-defence tactic uses the former, the player's order UI
  the latter — blocking the wrong one silently removes a player capability.
- **Lesson** — Enumerate the **legitimate** callers from IL before blocking anything, and let them
  through with `[ThreadStatic]` depth counters rather than caller-name sniffing or stack walking.
- **Now** — `Payload/SiegeCommandGuard.cs:44-50,61-66,270-276,453-496`.

### H25 · A tracer prefix on a setter that early-returns reports writes that never happened

- **What happened** — `Formation.SetControlledByAI` early-returns when the incoming value equals the
  current one, so a naive prefix logs a write for every call the engine then discards. The trace
  reads as though control changed hands at moments when nothing changed.
- **Lesson** — Compare the incoming value against the current state and log only **real flips**. This
  applies to any engine setter with an early-return: a tracer that reports the *call* rather than the
  *effect* invents events. Same outcome as **H12** — a log saying a change happened when it did not —
  from a different cause: there the engine's own veto, here the engine's own no-op.
- **Now** — `Payload/ControlTrace.cs:139-154`; the rule is stated in the summary at `:139` and
  enforced by the `__instance.IsAIControlled == isControlledByAI` bail at `:144`.

> **Good to know — the four Harmony shapes this repo actually relies on.**
> **(a) Finalizer returning `null`** is the only hook that intercepts an exception escaping into
> native code without rewriting the method, and `if (__exception == null) return null;` keeps it
> completely inert on the success path — so "never fired" in the health report becomes a retirement
> signal (`Payload/DeploymentCrashGuards.cs:16-26`; `Payload/ConversationCameraCrashGuard.cs:57-66`;
> `Payload/ClanScreenCrashGuard.cs:14-18,47-66`; `Payload/CivilianGateCloseFix.cs:55,100-114`).
> **(b) Finalizer that recovers** — `TaleWorlds.ScreenSystem.ScreenManager.PopScreen()` is static and
> parameterless (`Invoke(null, null)`), so a failed `CreateDataSource` can pop back to the map instead
> of leaving the UI unusable (`Payload/ClanScreenCrashGuard.cs:55-65`).
> **(c) Finalizer that only observes** — take `Exception __exception`, log it with full context, and
> **return it unchanged**: a breakpoint you can ship, with no risk of converting a crash into silent
> corruption (`Payload/CharacterCreationTrace.cs:116-123`; `Payload/MovementOrderInitProbe.cs:73-93`).
> **(d) Prefix that mutates `ref` arguments instead of returning `false`** — vanilla still runs, with
> corrected input, so all of its downstream bookkeeping (events, UI refresh, order-controller state)
> still happens. `ref bool isControlledByAI` flipped to false, `ref bool isPlayerGeneral, ref bool
> isPlayerSergeant` rewritten (`Payload/SiegeCommandGuard.cs:280-307,337-363`). This is the single
> most transferable technique in the codebase — far safer than cancelling a method you would then have
> to reimplement.

> **Good to know — layering, and hooks that do not need a signature.**
> A **narrow prefix for the proven state plus a broad finalizer for everything else** leaves normal
> behaviour untouched while guaranteeing no CTD for shapes not yet diagnosed, and each layer's fires
> are counted separately as evidence (`Payload/PartyAiCrashGuard.cs:22-25,77-99,101-123,131-147`).
> Harmony's `object[] __args` lets one prefix body trace a method whose signature you do not know or
> that varies across builds, and `MethodBase __originalMethod` lets **one** hook body serve many
> patched methods and still name which fired — at the cost that a signature change silently
> mislabels fields (`Payload/TracePatches.cs:86-149,206-240`; `Payload/CharacterCreationTrace.cs:41-45,94-97`;
> `Payload/CoopBattleTrace.cs:96-126`).
> Attribute-based `PatchAll` is right for stable engine targets (it fails hard when a type is
> missing); an explicit `Apply(Harmony)` is right for conditional ones, because it lets a guard
> inspect the world first and choose to stay inactive — and lets load-time fixes be ordered before
> `PatchAll` (`Payload/DeploymentCrashGuards.cs:13,34` with `Payload/PayloadEntry.cs:45,48-75`).
> Scope `PatchAll` to your own assembly (`PatchAll(typeof(X).Assembly)`) rather than the
> calling-assembly overload, which is ambiguous under shadow-copy/hot-reload
> (`Payload/PayloadEntry.cs:38-45`).

> **Good to know — writing a get-only auto-property, and the `MethodInfoWrap` habit.**
> Some engine flags are only reachable through their compiler-generated backing field:
> `AccessTools.Field(type, "<IsPlayerGeneral>k__BackingField")`, cached at `Apply`. Treat such a hook
> as **optional** with an announced degradation, because the name is a compiler detail
> (`Payload/SiegeCommandGuard.cs:101-102,118-126,373-380`). And a one-field struct whose constructor
> short-circuits on a null `Type` keeps `AccessTools.Method` off an unresolved type without a null
> check at every call site (`Payload/ClanScreenCrashGuard.cs:83-91`).

---

## 2. .NET Framework and the CLR

Bannerlord 1.4.8 modules target **net472**. Everything in this section is a consequence of that
runtime plus the way the game loads modules.

### N1 · Harmony-patching a method can poison an unrelated `beforefieldinit` type — permanently

- **What happened** — This mod caused the crash it was diagnosing. Adding `Formation`/`OrderController`
  patches in **v1.3.0** made the CLR *prepare* the `beforefieldinit` struct `MovementOrder` during
  JIT/patching. Its `.cctor` builds six template orders via `MovementOrder..ctor(MovementOrderEnum)`,
  whose one null-capable line is `Mission.Current.CurrentTime`. With no mission alive that NREs, the
  type initializer fails, **.NET caches the failure for the process**, and every battle for the rest
  of the session dies at `Formation.ResetAux` with a `TypeInitializationException`.
- **Lesson** — Merely patching a method makes the CLR prepare every type that method references, and
  a `beforefieldinit` type's static ctor may run at **any** point before first static-field access.
  When you patch broadly, audit the `beforefieldinit` types you are pulling in — and apply the
  load-time safety patch **first**, before `PatchAll` and before every other guard.
- **Now** — `Payload/MovementOrderTypeInitGuard.cs:14-25,26-34`; applied first at
  `Payload/PayloadEntry.cs:36-46`; full IL proof in `docs/ENGINE-NOTES.md` §2
  "`MovementOrder` is a `beforefieldinit` struct whose init needs a live Mission (2026-09-04)".

### N2 · A logged type-init throw can be a **cached re-throw** from a different moment

- **What happened** — The `MovementOrder` crash was chased where it was *logged*
  (`Formation.ResetAux` inside `Mission.AfterStart`, where `Mission.Current` is already live). .NET
  runs a type initializer once, caches the failure, and re-throws the **original** exception with the
  original stack on every later access. Only collateral was ever captured; the origin never was.
- **Lesson** — To find the origin, patch the **instance constructor the static ctor calls** — its
  first-ever call happens inside the static ctor. This inverts the usual "read the stack" instinct,
  and it explains how `Mission.Current` can be live in the logged throw of a null-at-init crash.
- **Now** — `Payload/MovementOrderInitProbe.cs:7-23`; `Payload/MovementOrderTypeInitGuard.cs:21-24`;
  `docs/DIAGNOSTICS.md:80-85`; mission load order proven at `docs/ENGINE-NOTES.md` §1
  "Mission load order (2026-09-04)".

> **Good to know — pinning a static initializer to a moment you choose.**
> `RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle)` defeats `beforefieldinit`'s
> unpredictable timing, and the `catch (TypeInitializationException)` around it doubles as a one-line
> oracle that distinguishes "fixed" from "too late": *"ALREADY poisoned before this guard could patch
> it … the fix must move into the harness SubModule."* Such a fix needs a fresh game launch, never a
> payload hot-reload. `Payload/MovementOrderTypeInitGuard.cs:36-39,59-71,67-71,80-83`;
> `CLAUDE.md` "While the game is running".
> The transpiler that makes the line safe collapses a two-instruction call pair into one helper call
> by rewriting the **first** instruction in place (opcode→`Call`, operand→helper) and `Nop`-ing the
> second, so labels and exception blocks attached to that instruction survive and the net stack
> effect is unchanged; it counts patched sites and logs zero (`:85-113`).

### N3 · `Assembly.LoadFrom` dedups by **simple name** only

- **What happened** — v1.2.3 stamped a unique `AssemblyVersion` per build to defeat the dedup. It
  never mattered: LoadFrom collapsed the new generation onto the already-loaded assembly, returned
  stale code with stale statics, and the reload log said success. Field-proven 2026-09-01 17:37 with
  the log line `LoadFrom deduped to already-loaded 1.2.7.42191`.
- **Lesson** — Vary the assembly **name**, not the version. "A unique name per build is the only
  identity LoadFrom cannot collapse."
- **Now** — `Payload/BLTDeploymentCrashGuard.Payload.csproj:11-18`; `HOTRELOAD.md:10`;
  CHANGELOG.md:119-124. Detected at runtime by comparing the returned assembly's `Location` against
  `Path.GetFullPath` of the requested path, `OrdinalIgnoreCase` (`Harness/HotReload.cs:315-324`).

### N4 · `LoadFrom` also caches **path → assembly**

- **What happened** — Re-using a per-**generation** shadow path (`.genN`) after a failed attempt
  returned the first attempt's assembly without reading the new file, so a correctly renamed build
  still looked like a dedup. Field-proven 2026-09-01 17:43.
- **Lesson** — Make the load path unique per **attempt** (pid + generation + `UtcNow.Ticks`), not per
  generation.
- **Now** — `Harness/HotReload.cs:307-312`.

### N5 · `LoadFrom` locks the loaded file for the process lifetime, and probes from that file's folder

- **What happened** — Loading the canonical DLL directly meant copying a fresh build over it failed
  with a sharing violation, so no reload ever fired.
- **Lesson** — Load a **shadow copy** — and put it in the **same directory** as the canonical DLL,
  because LoadFrom dependency probing follows the loaded file's own folder. The two constraints
  together dictate the whole shadow-copy design.
- **Now** — `Harness/HotReload.cs:298-302`.

### N6 · `Assembly.Load(byte[])` probes the app base, and `AssemblyResolve` never fires when probing succeeds

- **What happened** — Byte-loading the payload resolved its references via default-context probing,
  which **found the game's own 0Harmony 2.4.2.0 in the app base** and bound it silently. The
  2026-08-30 `AssemblyResolve` pin could not possibly help, because the resolver only runs when
  probing **fails**. Field-hit 2026-08-30 16:00.
- **Lesson** — When the wrong assembly is reachable by normal probing, no resolver can save you.
  Change the load **context** (LoadFrom, from the module directory). A byte-loaded assembly has no
  load path at all, so its probing falls back to the app base by construction.
- **Now** — `Harness/HotReload.cs:279-287`; CHANGELOG.md:213-220; `HOTRELOAD.md:10`.

### N7 · A Bannerlord process holds **two** copies of 0Harmony

- **What happened** — The game bin ships 2.4.2.0 in the app base; `Bannerlord.Harmony` module-loads
  2.3.6.0. Which one you bind depends on the load context. Returning whichever
  `AppDomain.GetAssemblies()` listed first for `0Harmony` split the Harmony type identity, so the
  payload's `Apply(Harmony)` no longer implemented `IPayload.Apply(Harmony)`; generation 2 was
  rejected mid-session and tracing could not be enabled without a restart. Field-hit 2026-08-29 22:44.
- **Lesson** — For an assembly whose **types cross a plugin interface boundary**, pin it to the exact
  instance the host is bound to (`typeof(HarmonyLib.Harmony).Assembly`) — never "first match wins".
- **Now** — `Harness/HotReload.cs:144-165`; `:146-148,283-287`.

### N8 · `TypeLoadException: Method 'Apply' … does not have an implementation` means a **type identity split**

- **What happened** — The message names a method and says nothing about assemblies, so it is
  routinely misdiagnosed as a build or interface mismatch. It is neither: the two sides bound to
  different copies of the same assembly.
- **Lesson** — Recognise the message. Then look at which assembly copy each side bound to, and dump
  an evidence pack rather than guessing.
- **Now** — `Harness/HotReload.cs:59-62,149-150,285-286`; CHANGELOG.md:216-221,272-278.

> **Good to know — the evidence pack for a type-load failure.**
> On failure, log: the exception, the host's identity and location, the boundary assembly's identity
> and location, **every loaded copy** of the names that could have split (annotated with
> `ReferenceEquals`), and the failing assembly's own `GetReferencedAssemblies` entries. Read
> `Assembly.Location` through a helper that returns `"(byte-loaded, no path)"` for an empty Location
> — it is empty for byte-loaded assemblies and can throw. This converts "the mod silently did
> nothing" into a log that answers *who supplied the duplicate*, with no decompiler and no debugger.
> `Harness/HotReload.cs:194-233`, called at `:348`.
> The resolver itself is a recipe worth copying: return null for the plugin's own dynamically-named
> family, hard-pin the assemblies whose **types cross the interface**, and only then fall back to
> first-loaded-by-simple-name — logging an `AMBIGUOUS` line when several copies exist, which is how
> you discover a duplicate you did not know about (`Harness/HotReload.cs:63,134-192`).

### N9 · Bannerlord loads module DLLs via `LoadFrom`, which is invisible to default probing

- **What happened** — Your mod, 0Harmony and BT are all in the LoadFrom context. A byte-loaded
  assembly's references are resolved by **default**-context probing, which cannot see them.
  Byte-loading the payload (generation 1) let the harness reference bind to a *different copy* of the
  harness assembly, so `PayloadEntry` implemented **that** copy's `IPayload`; the whole payload
  silently failed to load and a full session was played with zero guards. Field-hit 2026-08-21 15:14.
- **Lesson** — This single fact explains most "my dynamically loaded assembly cannot see the mod I am
  running inside" failures in Bannerlord. It is the root of four separate dated incidents in this repo.
- **Now** — `Harness/HotReload.cs:56-63,276-280`.

### N10 · Generation 1 always worked, which hid the bug for three releases

- **What happened** — Gen1 happened to load via LoadFrom-context probing and saw the module-loaded
  0Harmony 2.3.6.0; only gen2+ went through the byte-load path.
- **Lesson** — A path that works by accident on the first iteration hides the defect until iteration
  N. When only later generations fail, suspect the **load context**, not the code.
- **Now** — CHANGELOG.md:220-221.

### N11 · `[assembly: InternalsVisibleTo]` is matched by **exact** assembly name

- **What happened** — The per-build name stamping needed to defeat LoadFrom dedup silently revoked
  friend-assembly access; the attribute can never cover a name that varies per build.
- **Lesson** — A name-varying reload scheme forces the shared surface (`Log`, `Diag`, `GuardConfig`,
  `SelfHealing`) to be **public**. The attribute survives only for the fixed-name (Roslyn) case.
- **Now** — `Harness/AssemblyInfo.cs:1-9` with `Payload/BLTDeploymentCrashGuard.Payload.csproj:19-24`;
  CHANGELOG.md:126-128.

### N12 · Statics are fresh in every hot-reload generation

- **What happened** — Every payload static resets on reload. `BattleMode`'s patch **Stash** is a
  static `Dictionary`, so a reload while in vanilla battle mode leaves lifted foreign patches
  unrestorable by the new generation. Guards holding deferred state in statics
  (`_pendingLeader`/`_pendingParty`/`_pendingSinceTick`, `_rollBlockLogged`, `_enabled`, `_autoOpen`)
  silently drop that state — a pending troop-screen open just disappears, with no timeout note.
- **Lesson** — Freshness is what makes a reload clean, so distinguish **per-generation caches** from
  **cross-generation state**. The latter belongs in the harness's shared-state bag, never in a payload
  static. Also: guards that re-patch on the fly must read the **current** generation's Harmony
  instance, never a captured one.
- **Now** — `Payload/PayloadEntry.cs:8-11,14-21` vs `Payload/BattleMode.cs:75`;
  `Payload/ClanPartyCreationAdvisor.cs:53-57`; `Payload/IllnessDeathGuard.cs:31-32`;
  CHANGELOG.md:322-325; the bag itself at `Harness/SharedState.cs:6-48`, `Harness/Contracts.cs:25-37`,
  passed into each generation at `Harness/HotReload.cs:36,367`.

### N13 · A hot-reload leaves the previous generation's `AppDomain` event handlers attached

- **What happened** — Each reload piled another `FirstChanceException` handler on, so every exception
  logged N times — a compounding corruption of your own evidence.
- **Lesson** — Guard cross-generation subscriptions with an `AppDomain.SetData`/`GetData` slot. An
  assembly static is fresh per generation, which is exactly what makes it useless here.
- **Now** — `Payload/CharacterCreationTrace.cs:31,127-150`.

### N14 · An exception handler that throws re-enters itself

- **What happened** — `AppDomain.CurrentDomain.FirstChanceException` fires again for the exception
  your handler just raised.
- **Lesson** — Three rails, all required: a `[ThreadStatic]` re-entrancy flag reset in a `finally`, a
  catch-all around the entire handler body, and a hard emission cap.
- **Now** — `Payload/CharacterCreationTrace.cs:19-27,35-36,144,152-196`.

### N15 · `Assembly.GetTypes()` throws on a partially-loadable assembly

- **What happened** — In a modded AppDomain an assembly with one unresolvable dependency throws
  `ReflectionTypeLoadException` on `GetTypes()`. A naive peer-mod type lookup dies the first time BT
  has a missing dependency, and a whole assembly becomes unreadable.
- **Lesson** — Catch it and use `loadEx.Types` — which **null-pads** the entries it could not load.
  Skip the nulls and keep scanning.
- **Now** — `Payload/BattleMode.cs:465-480`; `Payload/JoinSyncPauseEscape.cs:174-189` (comment at `:188`).

### N16 · `Environment.TickCount` is a signed 32-bit millisecond counter that wraps

- **What happened** — It wraps roughly every 24.9 days (49.7 days of uptime counted end to end). An
  unguarded `now - last < Window` latches a circuit breaker, a rate limiter or a log throttle
  **forever** after the wrap; the mirror-image `now - last > N` fires forever.
- **Lesson** — Pair every delta comparison with a direction check — `now >= last` for "within window",
  `now < last` for "expired / force flush" — so a wrap degrades to allow-and-log rather than to a
  permanent freeze. Every throttle in this repo carries the clause.
- **Now** — `Payload/EncounterLoopGuard.cs:96,109,117`; `Payload/PartyAiCrashGuard.cs:155`;
  `Payload/BackgroundTickBudgetGuard.cs:130`; `Payload/BattleMode.cs:415`;
  `Payload/PayloadEntry.cs:166,194`; `Payload/TraceThrottle.cs:63-65`; `Payload/RoleTrace.cs:83`;
  `Payload/RuntimeDiagnostics.cs:44`; `Payload/LogStreamer.cs:101`;
  `Payload/TimeEnforcementGuard.cs:151-153`; `Payload/ShareTimeControl.cs:62-67`;
  `Payload/JoinSyncPauseEscape.cs:244-245`; `Payload/SiegeCommandGuard.cs:515`;
  `Payload/SiegeGatePromptFix.cs:75,112`; `Payload/CivilianGateCloseFix.cs:108`;
  `Payload/CoopCommandSplit.cs:136,334,408`; `Payload/ClanModeSoloFix.cs:135-140`;
  `Payload/PlayerIdentityGuard.cs:37-42`; `Payload/CoopHeroIdentityLock.cs:181-187`;
  `Payload/BootstrapWatch.cs:37-42`.

### N17 · Old assemblies cannot be unloaded on .NET Framework

- **What happened** — Every hot-reload leaks roughly 1–3 MB.
- **Lesson** — Budget for it — "restart every few dozen reloads" — and do not treat rising memory
  during a long dev session as a product bug.
- **Now** — `HOTRELOAD.md:63`.

### N18 · Cross-thread flag pairs treated inconsistently

- **What happened** — `_pendingReload` is `volatile`, but `_debounceTick` — written from the
  `FileSystemWatcher` thread and read from the main thread — is a plain `int`. Similarly
  `SelfHealing.RegisterTest`/`ResetTests` mutate a plain `List<Func<TestResult>>` **without** the
  `Sync` lock that `RecordFire`/`FireSummary` take.
- **Lesson** — Both are fine in practice (worst case a mis-timed debounce window; registration only
  happens on the main thread during `Apply`) but the asymmetry is easy to misread as a guarantee.
  Write down which invariant makes an unsynchronised access safe, or take the lock.
- **Now** — `Harness/HotReload.cs:37,45,482-483`; `Harness/SelfHealing.cs:28-30,83-92,97-106`.

### N19 · A typed getter that cannot distinguish "missing" from "wrong type"

- **What happened** — `SharedState.Get<T>` returns `default(T)` both when the key is absent and when
  the stored value is not a `T`. Across a reload the stored value may come from a different
  generation's type identity — exactly the case you need to detect.
- **Lesson** — Use `Has()`/`GetObject()` where "stored but wrong type" must be distinguishable.
- **Now** — `Harness/SharedState.cs:11-31`.

### N20 · Two different mechanisms both produce "survives a reload"

- **What happened** — The `Contracts` comment lists "guard fire counts" among the things the
  `ISharedState` bag holds. They are actually in a harness static dictionary (`SelfHealing.Fires`)
  and survive because the **harness** is never reloaded.
- **Lesson** — Do not assume a value is in the bag because it persists. Say which mechanism carries it.
- **Now** — `Harness/Contracts.cs:25-30` vs `Harness/SelfHealing.cs:28,94-96`.

### N21 · Hosting Roslyn in-process on net472 inside Bannerlord

- **What happened** — Roslyn bind-conflicts with ButterLib's older `System.Collections.Immutable` /
  `System.Reflection.Metadata`, and `Emit` can throw. ButterLib is present in most modded installs.
- **Lesson** — Keep the prebuilt-DLL path primary, compile Roslyn in only behind an opt-in build
  symbol with a runtime `CompiledIn` probe, and always fall back to the prebuilt DLL on any compile
  failure. Mode (A) build-and-drop is "bulletproof, zero extra deps"; mode (B) is "fragile on net472".
- **Lesson (second-order)** — The Roslyn path emits **bytes** and loads them with
  `Assembly.Load(byte[])` — precisely the load path whose default-context probing was proven to split
  the 0Harmony identity (N6). Expect ROSLYN mode to reproduce the "`Apply` does not have an
  implementation" failure.
- **Now** — `Harness/HotReload.cs:21-25,71,415-432`; `Harness/PayloadCompiler.cs:3-11,21-23,25-105`;
  `HOTRELOAD.md:24,36,46-48`; the risk noted at `Harness/HotReload.cs:279-287,331-340`.

### N22 · `AssemblyVersion` wildcards and `FileVersion` inheritance

- **What happened** — A `$(Version).*` wildcard silently does nothing unless
  `<Deterministic>false</Deterministic>` is set; and a wildcard is **illegal** in
  `AssemblyFileVersion`, yet `FileVersion` is inherited from `AssemblyVersion` if you omit it —
  producing a build error rather than a default. Two SDK-era gotchas in four lines, one failing
  silently and one at build time.
- **Lesson** — Set `<Deterministic>false</Deterministic>` alongside the wildcard, and pin
  `<FileVersion>$(Version).0</FileVersion>` literally.
- **Now** — `Payload/BLTDeploymentCrashGuard.Payload.csproj:28-36`.

### N23 · `csc` names the assembly after its **output file**

- **What happened** — You cannot stamp a varying internal name while keeping the file name fixed in
  one step; the stamp *is* the compile-time output name.
- **Lesson** — Stamp `AssemblyName` (which changes the output file name), then copy to the fixed name
  and delete the stamped file in an `AfterTargets="Build"` target. This is the general MSBuild
  pattern for "internal identity must vary, file name must not".
- **Now** — `Payload/BLTDeploymentCrashGuard.Payload.csproj:19-24,92-97`.

### N24 · A test project that links shipping sources inherits the shipping build properties

- **What happened** — The pure wire-model test projects inherited the payload's per-build **wildcard**
  `AssemblyVersion` via `Directory.Build.props` — meaningless for files that carry no game version,
  and hostile to deterministic builds.
- **Lesson** — Opt out explicitly: `<Deterministic>true</Deterministic>` plus a fixed
  `<AssemblyVersion>`. Opt out in **every** such project — an opt-out applied to one of a pair is the
  easiest kind of fix to believe is finished.
- **Now** — `tests/StashPayloadTest/StashPayloadTest.csproj:10-13` is the only one that opts out.
  `tests/BirthPayloadTest/BirthPayloadTest.csproj` still sets neither property (its `PropertyGroup`
  ends at `:9`), so the pitfall is live in the second wire-model test project.

### N25 · Copying engine or Harmony DLLs into the module bin

- **What happened** — A copied TaleWorlds or Harmony DLL makes the process load two incompatible
  copies of the same types; a copied sibling assembly duplicates its statics. The symptoms (type
  mismatches, patches that do not take) look nothing like the cause.
- **Lesson** — `<Private>false</Private>` on every engine/Harmony reference **and** on the internal
  `ProjectReference`.
- **Now** — `Harness/BLTDeploymentCrashGuard.csproj:32,36,40,44,48`;
  `Payload/BLTDeploymentCrashGuard.Payload.csproj:45-51,56,60,64,68,72,76,80,84,88`.

### N26 · Inherited NuGet feeds

- **What happened** — Machine- and user-level NuGet configs are additive, so an inherited private or
  dead feed changes or breaks a contributor's restore in ways that look like a code problem.
- **Lesson** — Pin the feeds with `<clear />` in a repo-root `NuGet.config`.
- **Now** — `NuGet.config:3-6`.

> **Good to know — .NET Framework networking from inside the game process.**
> TLS 1.2 must be explicitly OR-ed into `ServicePointManager.SecurityProtocol` before an HTTPS POST
> from a Bannerlord mod, or the call fails opaquely; and the upload belongs on a ThreadPool worker
> with explicit timeouts, because a synchronous upload on the game thread stalls the game.
> `Payload/LogStreamer.cs:151-159`.

---

## 3. Engine — mission lifecycle and deployment

### E1 · Treating a null `Mission.InitialPlayerAgent` as a startup-only condition

- **What happened** — `Mission._initialPlayerAgent` is assigned only when an agent is built with
  `Controller == AgentControllerType.Player`, and it is **re-nulled whenever that agent is removed**.
  Vanilla never hits the null only because the native spawn path always creates the player agent in
  `OnSetupTeamsOfSide(PlayerSide)`.
- **Lesson** — Guard the dereference, not the moment.
- **Now** — `Payload/DeploymentCrashGuards.cs:29-32`; `UPSTREAM_BUG_REPORT.md:60-70`;
  `Payload/PlayerIdentityGuard.cs:9-15`.

### E2 · Mistaking a suppressed crash for a fixed feature

- **What happened** — The `DeploymentMissionController.SetupTeams` NRE is a **vanilla** line crashing
  on mod-induced state; the actual root cause is that BT never rosters or spawns the player side.
  Guarding removed the CTD and left the battle unplayable: every player formation 0/0, with a
  105-member unwounded party.
- **Lesson** — Ship the guard **and** file the root cause upstream. "The crash is gone" is not "the
  bug is fixed"; verify the gameplay outcome, not the absence of an exception.
- **Now** — `Payload/DeploymentCrashGuards.cs:8-12`; `UPSTREAM_BUG_REPORT.md:85-93,104-108`;
  CHANGELOG.md:355-357,291-293.

### E3 · Correcting player control during the deployment phase

- **What happened** — `Controller = None` on the player agent is **legitimate** while a
  `DeploymentMissionController` exists. A corrective loop that "fixes" it fights the deployment
  system. Separately, `Mission.Scene` can be null while `Mission.Current` is live.
- **Lesson** — Before writing a corrective loop, enumerate the states where the "wrong" value is
  correct, and gate on the behavior that owns them
  (`GetMissionBehavior<DeploymentMissionController>()`). Do not treat a mission as usable before its
  `Scene` exists.
- **Now** — `Payload/PlayerIdentityGuard.cs:16-18,45-48,58-61`.

### E4 · Touching deployment positioning at all

- **What happened** — The crash-guard family had already learned that intervening in the deployment
  path is where sieges break.
- **Lesson** — Let vanilla's auto-deploy position the formations, then take **control** at
  `OnDeploymentFinished` with a move order to the position vanilla already chose. Minimal surface, no
  re-implementation of deployment.
- **Now** — `Payload/SiegeCommandGuard.cs:51,427-432`; CHANGELOG.md:75-76.

### E5 · An unguarded state dump becomes the crash it was meant to explain

- **What happened** — During a mission or scene transition the engine accessors themselves throw:
  `Mission.Mode`, `Mission.CurrentState`, `Mission.Scene`, team and formation getters.
- **Lesson** — Wrap **every** engine read individually — per-property `try/catch` or a
  `SafeGet<T>(Func<T>)` helper — and degrade to `?` / `threw:<Type>` rather than propagating.
- **Now** — `Payload/RuntimeDiagnostics.cs:95-126`; `Payload/ControlTrace.cs:299-345`.

### E6 · Mutating campaign identity inside a mission

- **What happened** — `ChangePlayerCharacterAction` mid-mission leaves the mission's agents, teams and
  controllers bound to a hero who is no longer the player — the exact breakage the in-mission
  identity guard exists to repair.
- **Lesson** — Defer identity changes until `Mission.Current` is null.
- **Now** — `Payload/CoopHeroIdentityLock.cs:72-89,167`.

### E7 · A corrector with no cap fights another system forever

- **What happened** — A repair loop that keeps losing a fight tanks the frame rate and never stops.
- **Lesson** — Cap the corrections (five attempts and a log trail here), reset the cap per mission by
  reference compare, and wrap each independent repair in its own `try/catch` so one throwing property
  — a foreign patch, a null team — cannot abandon the rest and leave the player half-corrected.
- **Now** — `Payload/PlayerIdentityGuard.cs:27,49-57,91-135`.

> **Good to know — the mission and module lifecycle surface.**
> `MissionState.FinishMissionLoading` calls `Mission.Tick` → `OnMissionAfterStarting` →
> `Mission.AfterStart`; `Mission._current` is set earlier by `Mission.Initialize` inside
> `MissionState.OpenNew` (`docs/ENGINE-NOTES.md` §1 "Mission load order (2026-09-04)"). The module
> lifecycle is module screen →
> game start → `OnMissionBehaviorInitialize` (per mission) → application tick
> (`Harness/SubModule.cs:24-48`). `MBSubModuleBase` has five overrides, four `protected override` but
> `OnMissionBehaviorInitialize(Mission)` is `public override`
> (`Harness/SubModule.cs:16,24,33,42,51`). On-screen messaging is
> `InformationManager.DisplayMessage(new InformationMessage(text, TaleWorlds.Library.Color))` and can
> throw early in startup, so it belongs behind a swallowing helper (`Harness/Log.cs:3-4,122-131`).

---

## 4. Engine — formations, orders, and command authority

### E8 · A one-time hand-off of formations to the player **decays**

- **What happened** — `Formation.RemoveUnit` hands an emptied formation back to the AI, so a
  formation wiped and then refilled by reinforcements is the AI's again mid-battle. In a siege,
  vanilla's default for every formation is AI control **on**:
  `BattleDeploymentHandler.SetDefaultFormationOrders` ends with
  `SetOrder(IsSiegeBattle || IsSallyOutBattle ? AIControlOn : AIControlOff)`.
- **Lesson** — Command ownership in Bannerlord is **re-derived continuously**. You must refuse the
  hand-off with a standing prefix on `SetControlledByAI`, not assign ownership once.
- **Now** — `Payload/SiegeCommandGuard.cs:26-33,280-307,389-451`; CHANGELOG.md:64-72;
  `docs/ENGINE-NOTES.md` §3 "Siege defense: vanilla's default is AI control ON (IL-proven,
  2026-09-03)".

### E9 · Being the settlement owner is not being the general

- **What happened** — `MapEvent.IsPlayerSergeant` demotes the player merely for being inside an army
  led by someone else — even when defending their **own** castle — and `Team.SetPlayerRole` then
  hands every formation to the AI.
- **Lesson** — Role is decided from **army membership**, not settlement ownership. Re-derive
  ownership campaign-side (`MapEventSettlement.OwnerClan == Clan.PlayerClan`) and assert the general
  role explicitly, only for the player's own settlement.
- **Now** — `Payload/SiegeCommandGuard.cs:34-36,245-265,337-363`; CHANGELOG.md:65-67,72-73.

### E10 · Patching only the first of two authorities over the same flag

- **What happened** — The player's battle role is decided in **two** places: `Team.SetPlayerRole` and
  `AssignPlayerRoleInTeamMissionController.AfterStart`. Patching only the first is silently overridden.
- **Lesson** — Look for a second authority over the same flag. Here the second one requires
  compiler-generated backing-field reflection and is therefore treated as an **optional, explicitly
  announced** degradation rather than a hard requirement.
- **Now** — `Payload/SiegeCommandGuard.cs:99-102,118-126,365-387`.

### E11 · Using mission-side state for a decision made before the mission has it

- **What happened** — `Team.SetPlayerRole` runs before the mission's `PlayerTeam` exists.
- **Lesson** — Fall back to campaign-side truth for anything evaluated during mission setup:
  `MobileParty.MainParty.MapEvent` (`IsSiegeAssault`, `PlayerSide`, `MapEventSettlement`) plus
  `Settlement.OwnerClan` vs `Clan.PlayerClan`.
- **Now** — `Payload/SiegeCommandGuard.cs:242-244`.

### E12 · Blocking the wrong half of a near-duplicate API pair

- **What happened** — `Formation.TransferUnits(Formation,int)` is the **tactic-only** API; the
  player's order UI goes through `OrderController.TransferUnits`. Blocking the latter would have taken
  re-organisation away from the player while leaving the AI free.
- **Lesson** — Prove which caller uses which from IL before patching either.
- **Now** — `Payload/SiegeCommandGuard.cs:48-50,94`.

### E13 · Treating a sally-out as a siege defence

- **What happened** — Sally-out battles are also `IsSiegeBattle`, and vanilla's AI-control-on default
  covers both — but a sally-out is an attack, not a hold-your-ground defence.
- **Lesson** — Exclude `IsSallyOutBattle` explicitly in the scope predicate.
- **Now** — `Payload/SiegeCommandGuard.cs:22,212,394`.

### E14 · Moving a player's own agent between formations

- **What happened** — In co-op, re-assigning agents by class would displace a human player's body —
  either machine's player, including BT's remote "ghost hero".
- **Lesson** — Exclude `Agent.Main`, `Agent.IsPlayerControlled`, `Hero.MainHero` and the BT ghost-hero
  id explicitly before any re-assignment. Companions are deliberately **not** excluded — they travel
  with their party. A remote hero id may name a `Hero` **or** a `CharacterObject` whose `HeroObject`
  is the hero, so check both and compare both `StringId`s.
- **Now** — `Payload/CoopCommandSplit.cs:41-42,265-266,273,299-323,356-363`.

### E15 · A membership guarantee that the game re-sorts underneath you

- **What happened** — The Order of Battle screen and reinforcement arrivals re-sort troops by class,
  undoing a formation split.
- **Lesson** — Continuous enforcement, not a single application: a spawn postfix **plus**
  `OnDeploymentFinished` **plus** a 500 ms tick.
- **Now** — `Payload/CoopCommandSplit.cs:38-40,95,122-147`.

> **Good to know — the mechanics of taking a formation without moving it.**
> `Formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache.GroundVec3)` →
> `SetControlledByAI(false, false)` → `SetMovementOrder(MovementOrder.MovementOrderMove(spot))` when
> `WorldPosition.IsValid`; `Team.FormationsIncludingEmpty` reaches formations that have no units yet
> (`Payload/SiegeCommandGuard.cs:414-434`). An AI-controlled formation belongs to
> `TacticDefendCastle`: `FormationAI.TickOccasionally` only runs behaviors while `IsAIControlled`, and
> the tactic assigns wall/gate/keep positions, re-plans on a breach and re-balances troops via
> `Formation.TransferUnits`/`Split` (`:26-33`).
> Agent/party plumbing for re-assignment: `Agent.Origin as PartyAgentOrigin` → `.Party` gives the
> owning `PartyBase`; `Agent.Formation` is a settable property; `CharacterObject.DefaultFormationClass`
> is a troop's intended class; `Team.GetFormation(FormationClass)` and `Formation.FormationIndex` are
> `FormationClass`-typed, so cast to `int` for arithmetic (`Payload/CoopCommandSplit.cs:277-297`).
> `Mission.PlayerTeam.GeneralAgent` and `PlayerOrderController.Owner` are settable, so both command
> links can be asserted and repaired at a transition — and `Agent.IsActive` is a **method**, not a
> property; getting that wrong is a silent reflection failure that disables the repair
> (`Payload/StealthHideoutAdvisor.cs:85-103`). `FormationClass` 0–7 are regular, 8 general, 9
> bodyguard (`docs/ENGINE-NOTES.md` §2 "Formation classes and indices").

---

## 5. Engine — time control

### E16 · Prefix-skipping a third-party method that keeps state

- **What happened** — Time enforcement v1 skipped BT's `EnforcePlaySpeed` entirely (prefix returning
  false). That let BT's internal time state machine go stale, which plausibly contributed to a joining
  player meeting a **stuck shared pause** — host unpause clicks vetoed while a peer was connected,
  despite shared time control being BT's own design (2026-08-19 00:32–00:35).
- **Lesson** — Let the method run and block only the specific side-effect **writes** you object to,
  scoped to that method's execution window via a `[ThreadStatic]` flag set in its prefix and cleared
  in its finalizer. This is the general answer for any mod-vs-mod conflict.
- **Now** — `Payload/TimeEnforcementGuard.cs:14-21,70-73,84-92,186-189`.

### E17 · A guard that blocks a write the peer re-requests every tick

- **What happened** — BT's `EnforcePlaySpeed` retried `UnstoppablePlay` every tick while the guard
  blocked the write, so the mode never changed and BT never converged. With tracing on, the `[TIME]`
  tracer logged each blocked attempt **with a full stack** at roughly 60 lines/second, filled the
  8 MB log in minutes, and rotated the real co-op-setup evidence off the end.
- **Lesson** — A guard that blocks a write creates a non-converging retry loop in the other mod. Any
  tracer on that path must coalesce from day one (first line in full, repeats collapsed into a
  windowed rollup), and the log needs a rolling window rather than a single `.1` overwrite. The
  coalescing emitter belongs in the **hot-reloadable** payload so the fix can land without a restart.
- **Now** — `Payload/TraceThrottle.cs:6-21`; `Payload/TimeTrace.cs:92-95,121-123`; CHANGELOG.md:5-16;
  `docs/ENGINE-NOTES.md` §4 "Time control in co-op (pre-2026-09-04)"; the convention is stated in
  `CLAUDE.md` "Conventions for guards/fixes".

### E18 · Using a peer mod's method calls as proof of a network session

- **What happened** — BT's `SetPaused` / `ApplyTimeState` also fire **once during a solo game load**
  (log 2026-08-18 23:49: `OnGameLoaded → SetPaused → ApplyTimeState`). Stamping "co-op activity" on
  every call would have faked a connected session and permanently disabled the solo-only time
  neutralizer.
- **Lesson** — Before treating a third-party call as evidence of a session, prove it is
  network-originated — here by a bounded stack walk for a packet-handler frame.
- **Now** — `Payload/TimeEnforcementGuard.cs:191-222,228-235`; `Payload/BattleMode.cs:392-416`.

### E19 · A vanilla option that hard-codes one enum value

- **What happened** — Vanilla's "map double click behavior = keep speed" checks `mode == 4`
  (`StoppableFastForward`) and therefore does **not** recognise the **unstoppable** fast-forward
  variant BT enforces. In co-op every click-to-move dropped to normal speed and the sync yanked it
  back up — a visible fast-forward flip-flop.
- **Lesson** — Check the value the mod actually sets, not the option's name. Then veto exactly one
  `(old, new)` transition in the setter prefix — reading the old value from `__instance` and the new
  from `value` — so every other consumer, including click-to-unpause (`Stop → StoppablePlay`), keeps
  working.
- **Now** — `Payload/MapClickSpeedKeeper.cs:11-18,79-100`.

### E20 · Binding to one spelling of a member that exists as two

- **What happened** — Campaign's time-control lock exists as either a method
  (`SetTimeControlModeLock`) or a property setter (`set_TimeControlModeLock`) depending on the build.
  Binding to one name alone silently loses the hook.
- **Lesson** — Probe all plausible member spellings, patch whichever resolves, and log the **total
  count** so a drop from N to N−1 is visible in the log alone.
- **Now** — `Payload/TimeTrace.cs:19-20,39-40`; `Payload/TimeEnforcementGuard.cs:84`.

### E21 · Forcing an engine predicate for **all** parties

- **What happened** — `Campaign.TickMapTime`'s `IsMainPartyWaiting` halts campaign time **without**
  changing the time-control mode — the widely-reported "the speed buttons say playing but the clock
  is frozen". Forcing `MobileParty.ComputeIsWaiting` to false for every party would change AI party
  behaviour across the whole campaign.
- **Lesson** — Gate the postfix on `__instance.IsMainParty` (and on `__result` already being true) so
  exactly one party is affected.
- **Now** — `Payload/TimeFlowPatch.cs:13-20,65-69`.

### E22 · Driving a **toggle** by reflection after a read that can lie

- **What happened** — `ToggleClientTimeControlPermission` is a toggle and
  `IsClientTimeControlEnabledForCurrentMenu` can lie, so calling the toggle after a wrong "already
  off" read turns the permission **off** instead of on.
- **Lesson** — Verify the resulting state, be prepared to flip back ("toggled the wrong way (was
  already true and menu-check lied) — flip back"), and **stop after the first confirmed success** so
  you never churn a setting the host may deliberately change later.
- **Now** — `Payload/ShareTimeControl.cs:56-61,94-102`.

### E23 · Trusting the return value of a reflected call whose result is in `out` parameters

- **What happened** — The `MethodInfo` may be `void`; the real result lives in by-ref outs.
- **Lesson** — Invoke with an `object[]` and read the by-ref results back out of the args array. It
  also makes the call survive a return-type change between versions.
- **Now** — `Payload/ShareTimeControl.cs:121-136` (comment at `:127`).

### E24 · Clearing a stuck join hold without clearing the manual pause

- **What happened** — Cancelling via BT's transfer-cancel router is not enough on its own: the
  player's **own** pause presses may have toggled BT's manual pause reason on, so time still does not
  resume and the fix appears to do nothing.
- **Lesson** — After cancelling, explicitly clear the manual reason too —
  `SetPaused(false, "Host", true, "join-escape")`.
- **Now** — `Payload/JoinSyncPauseEscape.cs:323-325`.

### E25 · Offering a destructive recovery on an unreadable state

- **What happened** — A misread of the pause state would let the recovery destroy a healthy join.
- **Lesson** — `HeldJoinReasons` returns null both when neither reason holds the pause **and** when
  the state cannot be read — never offer a cancel on uncertainty. Then gate the destructive half on
  consent: explain first, arm a bounded window, act only on a second press.
- **Now** — `Payload/JoinSyncPauseEscape.cs:29-33,240-278,280-311` (contract stated at `:280-281`).

> **Good to know — what the player actually experiences, which is why this guard exists.**
> The host's pause key only toggles BT's **manual** pause reason, so it cannot clear a join hold. The
> paused state therefore does not change, BT's "show a message when the state changes" check shows
> nothing, and the press is **silently swallowed** — no message, no state change, and before this
> guard no log line either. The player concludes the keybind is broken. That is shape #1 of this file
> (silence; nothing looks exactly like "the bug did not happen"), produced here by a peer's
> change-only messaging, and it is the observation that makes **E24**, this entry and **B18**
> comprehensible: the first press exists to *break the silence* (an on-screen explanation of who is
> holding time), not to cancel anything.
> (`Payload/JoinSyncPauseEscape.cs:14-16,29-31`; the guard's own line
> `unpause swallowed by join hold` at `:249-253`.)

### E26 · Caching a foreign singleton **instance** instead of its `FieldInfo`

- **What happened** — Caching BT's pause coordinator instance breaks if BT reassigns the static field
  mid-session.
- **Lesson** — Cache the `FieldInfo` and read `GetValue(null)` live on every query.
- **Now** — `Payload/JoinSyncPauseEscape.cs:47,286`.

### E27 · Config off-switches that only match a literal `false`, read once

- **What happened** — `timeAlwaysFlows` and `shareTimeControl` are matched with a regex that accepts
  only the literal `false`; any other value, a missing file or a read exception silently leaves the
  feature **on**. The value is cached in a `bool?` for the process, so editing the file mid-session
  does nothing until the payload reloads.
- **Lesson** — This is deliberate — a malformed config can never disable a fix — but it must be
  written down, because "I turned it off and nothing changed" is otherwise indistinguishable from a bug.
- **Now** — `Payload/TimeFlowPatch.cs:27-37,88-99`; `Payload/ShareTimeControl.cs:40-50,196-208`.

### E28 · Reading an intentional feature as a restriction to be bypassed

- **What happened** — Enabling shared client time control looks like a hack around a deliberate host
  restriction. It is not: BannerlordTogether **ships** the feature and merely defaults it off.
- **Lesson** — Invoke the host's own documented grant (`ToggleClientTimeControlPermission`) rather
  than bypassing the `AllowClientTimeControl` check. Zero patch surface: a polled reflection driver,
  not a Harmony patch, so there is nothing to interfere with another mod and nothing to break on an
  upstream update beyond the two reflected members — each individually reported when missing.
- **Now** — `Payload/ShareTimeControl.cs:12-21,52-119`, driven from `Payload/PayloadEntry.cs:147`.

---

## 6. Engine — encounters and the campaign map

### E29 · A rate limiter that cannot tell a pathology from a busy moment

- **What happened** — Version 1 of the encounter loop breaker was a **pure rate** breaker (N
  applications in a window). A partner's legitimate join storm looks exactly like that, so the breaker
  would have suppressed real joins.
- **Lesson** — Gate the limiter on the pathological **signature**, not on rate alone. Only
  applications that closely follow a *local* `PlayerEncounter.Finish` (within 4 s) count toward
  tripping; join requests have no preceding local `Finish`, so they can never trip it. False positives
  become structurally impossible rather than merely unlikely.
- **Now** — `Payload/EncounterLoopGuard.cs:37-41,109-112`.

### E30 · A guard whose trip signal comes from an optional subsystem

- **What happened** — The finish stamp that gates E29 comes from a **tracing-only** patch:
  `EncounterLoopGuard.NoteEncounterFinish` is called only from `TracePatches`' `PlayerEncounter.Finish`
  prefix, and `TracePatches.Apply` runs only when `guardconfig` `tracing=true` — which defaults to
  **false**. With tracing off, `_lastFinishTick` stays 0, `followsFinish` is always false, and the
  breaker can never trip.
- **Lesson** — A guard is only as live as the subsystem that produces its signal. If a decision
  depends on a debug-only code path, the documented behaviour differs between troubleshooting and
  normal play.
- **Now** — `Payload/EncounterLoopGuard.cs:42-45,109-112`; `Payload/TracePatches.cs:44,185-188`;
  `Payload/PayloadEntry.cs:81-84` (the `if (tracing)` gate and the `TracePatches.Apply` call it
  guards); `Harness/GuardConfig.cs:106` (`"tracing": false` default).

### E31 · Trying to repair transient half-synced co-op state

- **What happened** — BT's join syncs a `MobileParty`'s fields **piecemeal**, producing states vanilla
  can never produce. The party heals on its own once sync completes.
- **Lesson** — Prefer skipping one tick — which reruns — over repairing the state: "skipping one
  party's encounter handling for a tick is benign; it reruns next tick, and the party heals when its
  sync completes."
- **Now** — `Payload/PartyAiCrashGuard.cs:18-23,22-23,86-93,92,127-130`.

### E32 · A computed getter has no settable mirror

- **What happened** — `PlayerSiege.PlayerSiegeEvent` is
  `MainParty.SiegeEvent ?? MainParty.CurrentSettlement?.SiegeEvent` — you cannot repair the null by
  assigning it.
- **Lesson** — When the engine **derives** a value, the fix has to happen at the **effect site**:
  apply the effect to the object you found yourself, rather than patching the derivation. Knowing the
  `AttachedTo` / `Army.LeaderParty` derivations vanilla ignores is what makes an army-riding party's
  siege findable at all — a real siege is reachable five ways.
- **Now** — `Payload/MapIncidentCrashGuard.cs:19-22,189-197`; `UPSTREAM_BUG_REPORT.md:114-116`.

### E33 · One null with two causes, treated as one

- **What happened** — The same null had a co-op army-attach gap **and** a genuinely-over siege behind
  it. A single "skip the effect" would silently delete a game feature for co-op players.
- **Lesson** — Split the diagnosis before writing the fix: repair the recoverable case with the exact
  vanilla effect, and report the truth in the unrecoverable one — never a feature downgrade. Probe an
  ordered array of alternate derivations using the **same validity predicate the crash site needs**,
  so the object you find cannot re-trigger the original NRE. Check the exact dereference chain first
  and return early when it is intact, so the patch is live only in the state that would have crashed.
- **Now** — `Payload/MapIncidentCrashGuard.cs:22-31,160-175,177-211,213-246`, called at `:215-218`.

### E34 · Tracing an engine event that AI parties fire constantly

- **What happened** — Tracing `EncounterManager.StartSettlementEncounter` unfiltered drowned the log
  on 2026-08-18 — AI parties enter settlements all the time.
- **Lesson** — Only the player's own encounters are diagnostic signal. Filter by
  `MobileParty.IsMainParty` (also through `PartyBase.MobileParty`) before emitting.
- **Now** — `Payload/TracePatches.cs:104-131,151-177`.

---

## 7. Engine — heroes, clans, and campaign actions

### E35 · Skipping a daily tick to disable one of its effects

- **What happened** — The third-party NoSickness mod skips `AgingCampaignBehavior.DailyTickHero`
  entirely while the hero is ill and never clears the ill flag. That leaves a **permanently stuck**
  ill flag and skips everything else `DailyTickHero` owns — aging events, come-of-age events.
- **Lesson** — **Cure forward**: repair the state (`MainHeroIllDays = -1`, death mark cleared) and
  return **true** so vanilla runs normally. A prefix returning false suppresses the original *and*
  every event it raises.
- **Now** — `Payload/IllnessDeathGuard.cs:19-23,116-127`.

### E36 · Guarding each consumer of bad state instead of blocking its source

- **What happened** — The old-age death path has several stages (ill days, HP drain, death mark,
  extra-life consumption). Guarding each one is endless.
- **Lesson** — Prefix `IsItTimeOfDeath` and block the **roll**, not the kill — one patch removes the
  whole downstream state machine.
- **Now** — `Payload/IllnessDeathGuard.cs:17-19,79-100`.

### E37 · Guarding the frame where the NRE surfaces

- **What happened** — The issue-quest CTD manifests in
  `CharacterDevelopmentCampaignBehavior.OnHeroActivated → HeroDeveloper.DevelopCharacterStats()`,
  dereferencing a null `HeroDeveloper`. Guarding the perk code is the obvious move and the wrong one:
  the defect is that a **dead hero is being reactivated and re-added to the party at all**, from an
  `IsAlive`-less loop in `IssueManager`. `Hero.OnDeath()` nulls `_heroDeveloper` and is the only place
  that happens.
- **Lesson** — Proving "this field is nulled in exactly one place" is what converts a hypothesis into
  a fix. Then ship **both**: the caller fix for the known repro (`IssueManager.MakeAlternativeTroopsReturn`)
  and a class-level domain invariant for every unknown caller (`Hero.ChangeState`, blocking
  `dead → Active`). The test of a good invariant is that the legitimate path is unaffected — a real
  revive clears `IsDead` first, so it is never blocked.
- **Now** — `Payload/DeadHeroReactivationFix.cs:9-27,21-31,108-147`.

### E38 · A destructive predicate that removes on uncertainty

- **What happened** — `TroopRoster.RemoveIf` with a predicate whose `catch` returned **true** would
  delete living troops on any reflection or engine hiccup.
- **Lesson** — `catch { return false; } // never remove on uncertainty`. For a destructive predicate
  the exception path must be the conservative one — and `TroopRosterElement` is a struct, so
  `default(TroopRosterElement)` (null `Character`) is the cheapest degenerate input for pinning it in
  a self-test.
- **Now** — `Payload/DeadHeroReactivationFix.cs:91-104,163`.

### E39 · A transaction split across two mods stops being atomic

- **What happened** — `BarterManager.ApplyAndFinalizePlayerBarter` applies all offered barterables in
  one loop. BT suppresses the marriage barterable while its siblings apply natively, so when BT's gate
  then rejects, "the dowry is gone and no marriage happened."
- **Lesson** — Gold cannot be un-applied from a patch. The whole transaction must be cancelled
  **before anything applies** — the reusable answer to "mod A suppresses one leg, the other legs still
  commit".
- **Now** — `Payload/MarriageBarterGuard.cs:11-21,54-91`.

### E40 · Opening a screen from inside a postfix whose stack is still unwinding

- **What happened** — Opening the manage-troops screen inline from the `CreateNewClanParty` postfix
  runs while "the clan screen's popup + inquiry are still unwinding on this call stack".
- **Lesson** — Defer screen work to the next main-thread `Tick`, with the clan screen popped first, so
  the party screen sits on the map exactly like vanilla's own flows. Record pending state and act from
  the tick pump.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:30-37,185-197,199-267`; pumped at
  `Payload/PayloadEntry.cs:154`.

### E41 · Pushing a screen without checking what is on the stack

- **What happened** — A helpful mod that pushes a screen over a mission or over another screen wedges
  the UI.
- **Lesson** — Reproduce the preconditions vanilla's own callers satisfy: refuse over a `Mission`,
  refuse when a `PartyState` is already active ("the player opened one — wait for it to close"),
  `PopState(0)` a `ClanState` to land on `MapState`, and refuse on anything else (inquiry,
  encyclopedia) to retry next tick.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:235-256`.

### E42 · Treating a freshly created `MobileParty` on a BT **client** as final

- **What happened** — BT's `ClientWarPartyCreationPatch` registers a pending host-side creation, so
  the local party is **provisional** and can be swapped for the host-authoritative instance.
- **Lesson** — Wait a settle window (3 s here) and re-check **identity**, not just presence: on a
  `ReferenceEquals` mismatch, adopt the new instance and restart the window rather than acting on a
  doomed object.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:34-37,48-51,226-233`.

### E43 · Waiting forever for co-op state that may never arrive

- **What happened** — A pending action keyed on a networked object can hang indefinitely.
- **Lesson** — Bound the wait (`PendingTimeoutMs = 15000`) and give up with **both** a log line and an
  on-screen fallback telling the player what to do instead ("click the party on the map").
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:51,210-215`.

### E44 · Flooding the log from a prefix that fires every in-game day

- **What happened** — A permanently-active daily prefix writes a line per day for the life of the save.
- **Lesson** — A one-shot flag with explicit wording — "logged once, active every day" — proves the
  guard engaged without the flood.
- **Now** — `Payload/IllnessDeathGuard.cs:32,87-92`.

### E45 · Checking a config flag before resolving the patch targets

- **What happened** — A feature turned off by config would hide the fact that a game update broke its
  targets.
- **Lesson** — Resolve **both** targets first and only then honour the off-switch, so drift is still
  reported when the feature is disabled — and report disabled-by-config as `ok = TRUE` with the detail
  "disabled by config", so it is never mistaken for a failure.
- **Now** — `Payload/IllnessDeathGuard.cs:38-52`.

### E46 · Two mods patching the same vanilla behaviour

- **What happened** — This guard and the standalone NoSickness mod both patch the illness path.
- **Lesson** — The safe composition is for one of them to be strictly **state-reducing**: because this
  guard only ever cures and never increments ill days, NoSickness's prefix always sees a healthy hero
  and passes through. That reasoning has to be written down, or the next change breaks it. (Note the
  documentation drift here: CHANGELOG.md:303-304 describes a *stand-down*; what shipped is
  coexistence — say which of the two you built.)
- **Now** — `Payload/IllnessDeathGuard.cs:25-27`; CHANGELOG.md:303-304 and `Harness/GuardConfig.cs:92`
  (both still say *stand-down*).

> **Good to know — the vanilla old-age illness machine, and identity switching.**
> `BecomeOldAge` at 55, `MainHeroIllDays`, `Hero.DeathMark` with a private setter, and the
> `ApplyByDeathMark` branch — an IL-proven map of a mechanic many mods disable badly. Clearing a
> pending death mark:
> `AccessTools.PropertySetter(typeof(Hero),"DeathMark")?.Invoke(hero, new object[]{ KillCharacterActionDetail.None })`
> (`Payload/IllnessDeathGuard.cs:9-16,112-123`).
> `ChangePlayerCharacterAction.Apply(Hero)` is vanilla's supported player-identity switch (the
> death-succession path), and a save stores exactly **one** player identity — there is no per-machine
> identity in the save format, which is the whole shared-save co-op handoff problem
> (`Payload/CoopHeroIdentityLock.cs:12-16,22-24,167`).

---

## 8. Engine — settlements and scene objects

### E47 · An exact float threshold that vanilla itself never reaches

- **What happened** — `CastleGate.ServerTick` activates the gate's interaction points only on an exact
  `animation parameter >= 1.0`, while vanilla's own `SetInitialStateOfGate` parks a **closed** gate at
  a frozen **0.99** and freezes the skeleton. The gate looks at rest and is permanently
  un-interactable.
- **Lesson** — "It is there but has no prompt" is the signature of an exact-threshold test against an
  animation or float parameter. Correct only inside a narrow at-rest band (`[0.98, 1.0)`), leaving
  genuinely mid-swing (`< 0.98`) and already-correct (`>= 1.0`) cases to vanilla, so the fix cannot
  mask real motion.
- **Now** — `Payload/SiegeGatePromptFix.cs:13-27,32,88-91,134`; CHANGELOG.md:151-157.

### E48 · Unlocking one of several independent locks

- **What happened** — A civilian gate is locked **three** independent ways: `MissionObject.IsDisabled`
  on the gate machine, `IsDisabled` on every standing point, and `UsableTeam` set to
  `Mission.DefenderTeam`, which `StandingPointWithTeamLimit.IsDisabledForAgent` compares against
  `agent.Team` and which never equals the player's team. `CloseDoor()` also early-outs on
  `IsDisabled`.
- **Lesson** — Enumerate **all** the gates on a capability from IL before writing the fix; unlocking
  one does nothing. All three here are deliberate "gates are scenery in town" design, not an oversight.
  `MissionObject.IsDisabled` has no public setter — use `AccessTools.PropertySetter` + `Invoke`.
- **Now** — `Payload/CivilianGateCloseFix.cs:11-25,41,82-87`; CHANGELOG.md:161-169.

### E49 · Making previously-dead code run

- **What happened** — Civilian scenes never ticked gates before this fix re-enabled them, so
  `CastleGate.OnTick` / `ServerTick` now run in a context they were never written for and could hit a
  siege-only assumption.
- **Lesson** — When a fix activates a dormant code path, add self-disabling insurance in the **same
  change** — here a tick finalizer that swallows and logs — even when no failure is known ("none
  known; self-disabling insurance").
- **Now** — `Payload/CivilianGateCloseFix.cs:27-28,98-114`; CHANGELOG.md:170-171.

### E50 · Overriding a deliberate scenario decision

- **What happened** — `UsableMachine.IsDeactivated` (machine-level deactivation — the settable
  declaration is `UsableMissionObject.IsDeactivated`; `MissionObject` itself carries only
  `IsDisabled`) can be a deliberate scene or scenario decision; overriding it would break scripted
  scenes. A `CastleGate` resolves the read to `UsableMachine::get_IsDeactivated`.
- **Lesson** — Respect machine-level deactivation and correct only the specific frozen-parameter case.
- **Now** — `Payload/SiegeGatePromptFix.cs:66-69`.

### E51 · "Fixing" a correct-but-similar-looking case

- **What happened** — A ram-**destroyed** gate has no close prompt, and that is correct vanilla
  behaviour, not the bug being fixed. "Fixing" it would let players close a broken gate.
- **Lesson** — Carve the correct case out explicitly and log it under tracing, so the next
  investigator does not chase it.
- **Now** — `Payload/SiegeGatePromptFix.cs:24-26,70-80`.

### E52 · Restoring more than the capability requires

- **What happened** — The civilian gate fix deliberately does **not** restore the nav-mesh ability
  flags vanilla cleared for the open state.
- **Lesson** — Restore only what blocks the capability you are restoring. An open civilian gate then
  behaves identically to before, and closing goes through vanilla's own `CloseDoor` — animation,
  `SetGateNavMeshState`, colliders — instead of a reimplementation.
- **Now** — `Payload/CivilianGateCloseFix.cs:22-25`; CHANGELOG.md:169-170.

### E53 · Enabling an interaction with nobody local to use it

- **What happened** — Re-enabling a town gate with a null `Mission.Current.PlayerTeam` would make it
  interactable with no local team, and `SetUsableTeam(null)` would be meaningless.
- **Lesson** — Bail early: "nobody local to use the gate — leave it as scenery."
- **Now** — `Payload/CivilianGateCloseFix.cs:77-81`.

---

## 9. Engine — UI, screens, and view models

### E54 · Substituting your own enumeration into a live UI path

See **H6** for the mechanism. The rule in one line: enumerate a vanilla `yield` iterator for
**logging only**; the result is never replaced (`Payload/ClanPartyCreationAdvisor.cs:119-155`;
CHANGELOG.md:101-103).

### E55 · Reading view-model members with `AccessTools.Field` alone

See **H7**. Field-first with a property fallback, and a self-test that accepts *either* shape for
each of `Title` / `IsDisabled` / `DisabledReason`
(`Payload/ClanPartyCreationAdvisor.cs:157-167,332-336`).

### E56 · Letting a failed screen build leave the UI unusable

- **What happened** — Swallowing an exception in a screen-construction path normally strands the
  player on a dead screen.
- **Lesson** — A finalizer can **recover**: `TaleWorlds.ScreenSystem.ScreenManager.PopScreen()` is
  static and parameterless (`Invoke(null, null)`), so a failed `CreateDataSource` pops back to the map
  — a CTD becomes a graceful cancel.
- **Now** — `Payload/ClanScreenCrashGuard.cs:55-65`.

### E57 · Building a screen instead of calling the game's own entry point

- **What happened** — Re-implementing a flow loses everything the engine does around it.
- **Lesson** — Reuse the vanilla entry point: `Helpers.PartyScreenHelper.OpenScreenAsManageTroops(MobileParty)`
  is the exact call the "manage garrison" menu and the clan-member conversation use, so you inherit
  its behaviour and its preconditions (it expects the map state). The same principle covers
  `CastleGate.CloseDoor`/`OpenDoor`, `ChangePlayerCharacterAction`, and `SetProgress` with vanilla's
  own report text.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:32-38,256,337`; CHANGELOG.md:95-98,142-144,169-170,251-253.

### E58 · Applying a peer's data update under an open screen

- **What happened** — Bannerlord screens bind to the **live** `ItemRoster`; clearing it under an open
  stash screen is visible corruption.
- **Lesson** — Check for the open screen **before dequeuing**, still under the queue lock, so the
  update is deferred rather than lost.
- **Now** — `Payload/StashSync/StashSyncGuard.cs:285-289`.

> **Good to know — `ClanPartiesVM` and the clan Parties tab.**
> The exact greyed-card and disabled-button reason sets, the yield-iterator candidate list, and
> `ClanCardSelectionItemInfo`'s public fields — everything needed to instrument or extend the tab —
> plus the vanilla gap behind "I made a party and it did not let me add anyone": `CreateNewClanParty`
> makes a **leader-only** party with no troop step.
> `Payload/ClanPartyCreationAdvisor.cs:19-28,119-121,157-167,277-300`.

---

## 10. BannerlordTogether — interoperating with a peer mod

BT is a second mod patching the same engine, loaded by the same launcher, sometimes **after** this
one, with obfuscated members and no published API. Everything in this section is about the seam
between two mods rather than about the engine.

### B1 · Assuming the peer will verify or bootstrap again later

- **What happened** — BT's `CoopSubModule.TryVerifyNativeActionCacheWhenCampaignMapReady` audits the
  engine's `ActionIndexCache` before applying its deferred Harmony patches. On a **client** it
  compares the engine's *static* mirror fields — all sitting at `-1`, unprimed — against fresh native
  lookups, logs `BootstrapAborted reason=action-cache-mismatch … restartRequired=True` (the captured
  line is quoted at `UPSTREAM_BUG_REPORT.md:13-14`), and sets
  `_harmonyPatchBootstrapAttempted = true`, which permanently blocks retry. The whole session then
  runs with BT's sync patches unapplied: invisible partner armies, joins not registering on the host,
  speed desync, no client hero selection, a host-style map shell. BT's own log proves the native
  catalog is fully loaded (`actions=5167`, every action code valid, `diskLoad=False`) — the audit is
  a **false negative**. It is written only to `bt-sync-client.txt` on the Desktop; nothing reaches
  the player.
- **Lesson** — An interop fix that has to beat a **one-shot** must be installed before the peer
  reaches that code. On a mid-game payload reload the same code can only install its prefix and wait
  for the next process. And a peer's silent half-load is your job to surface — a session-long silent
  degradation costs whole evenings before anyone opens a log.
- **Now** — `Payload/ClientBootstrapFix.cs:9-30,74-82,173-176`; the ordering note at
  `Payload/PayloadEntry.cs:72-74`; `UPSTREAM_BUG_REPORT.md:22-32`; `docs/UPSTREAM_CONTRIBUTION.md:68-70`.

### B2 · A "restart required" that has no mechanism to become true

- **What happened** — `BootstrapAborted` reports `restartRequired=True` in the captured client log
  (`UPSTREAM_BUG_REPORT.md:13-14`, BT v0.5.0.1, game 1.4.8.119303), but BT's shipped
  `RuntimeDataCache` (dated 2026-06-30) never loads for game build 1.4.8.119303 and **no fresh cache
  is ever persisted**, so the next launch is identical. Proven by reproducing with the `.rdc` present
  (2026-08-19 20:46) and with it removed (21:41): `diskLoad=False` and all-`(-1)` sentinels both ways.
- **Lesson** — Test a "restart and it will fix itself" claim **both** with and without the artefact
  it depends on. If nothing writes that artefact, the loop is permanent and the cure is somewhere
  else; do not present cache-clearing as the remedy.
- **Now** — `UPSTREAM_BUG_REPORT.md:16-22`; `Payload/BootstrapWatch.cs:97-99` (detection and hygiene
  only); the actual fix is `Payload/ClientBootstrapFix.cs` priming the static mirrors.

### B3 · Patching a peer's readiness check out instead of satisfying it

- **What happened** — Force-passing BT's verification wholesale would let it patch **before** the
  engine catalog is loaded — the exact thing that check exists to prevent.
- **Lesson** — Reproduce the peer's own preconditions (num action codes > 0, the four probe actions
  resolve, no animation disk load in flight), prime the stale state so its audit legitimately passes,
  and remove only the one over-strict requirement. When the catalog is genuinely not ready, the
  prefix returns `true` and BT's original wait logic runs unchanged.
- **Now** — `Payload/ClientBootstrapFix.cs:22-30,147-191,250-284,288-320`. Two details anyone auditing
  action-cache state needs: a mirror field's **name is the action name**, so priming is
  `ActionIndexCache.Create(fieldName)` per static field, and the `act_none` sentinel must be excluded;
  the readiness gate itself is four-part, and `MBAnimation` has no guaranteed defining assembly across
  game versions, so it is resolved through a candidate list like everything else (`:105-138,220-248`;
  `UPSTREAM_BUG_REPORT.md:10-14`).

### B4 · A self-disable probe that inspects one field

- **What happened** — Judging "the mirrors are already primed" from a single `ActionIndexCache` field
  would see a **partially** primed state — sentinel valid, other mirrors still `-1` — and make the
  fix stand down, silently re-opening the bug it exists to fix.
- **Lesson** — A stand-down probe must inspect every element of the state it claims is healthy and
  return false the moment one is bad. Log **which** of the two explanations applies — "primed by us"
  versus "already primed and we never intervened — BT/engine handles it" — because that distinction
  is what makes retiring the workaround a decidable question instead of a guess.
- **Now** — `Payload/ClientBootstrapFix.cs:155-170,216-248`; the mechanism at `Harness/SelfHealing.cs:16-19`.

### B5 · Reading a peer's log for a **previous** session's event with a tail scan

- **What happened** — A 256 KB tail scan missed `BootstrapAborted` entirely: live test 2026-08-19
  21:14, the abort line sat at ~50 KB of a 12.7 MB log.
- **Lesson** — Scan the **whole file** at startup, because a previous session's event can be
  anywhere; only mid-session polls can rely on the tail, where new lines land. Open with
  `FileShare.ReadWrite` — that is what makes reading a file another process is actively writing
  possible at all. Persist a handled-offset ledger so an old event is not re-handled every launch,
  and treat that offset as a **monotonic marker**, never a byte-exact seek position: the arithmetic
  assumes 2-byte line endings (`consumed += line.Length + 2`).
- **Now** — `Payload/BootstrapWatch.cs:66-70,75-79,132-189,191-218,203-210`.

### B6 · Remediating a peer's regenerable data by deleting it

- **What happened** — The stale `.rdc` has to be out of the way before BT's next audit, but deleting
  another mod's file outright is unrecoverable and leaves no trail.
- **Lesson** — **Rename, never delete**: `<name>.stale-<timestamp>` is reversible by hand, auditable,
  and still forces the owner to rebuild. Reach the sibling module through `Assembly.Location` plus
  three `..` levels rather than a hardcoded Steam path (two levels reach your own module root).
- **Now** — `Payload/BootstrapWatch.cs:97-129,105-107,136-137`.

### B7 · By-name reflection into a peer mod degrades **silently**

- **What happened** — BT moved its network classes into `BannerlordTogether.Network.*`. Both
  pregnancy-sync and stash-sync simply stopped working; the only signal anywhere was `not resolved`
  in the 2026-09-01 mod-health line.
- **Lesson** — Resolve through an **ordered candidate list** of fully-qualified type names, patch
  wherever the member is found, and report DEGRADED when none resolved; refuse to activate on a
  *partial* resolve, so a half-resolved reflection set cannot silently skip checks. The startup
  health line is the only thing that converts a peer's refactor into a signal instead of a mystery
  "the feature stopped working".
- **Now** — `Payload/PregnancySync/PregnancySyncGuard.cs:225-239`;
  `Payload/StashSync/StashSyncGuard.cs:117-131`; `Payload/ClientBootstrapFix.cs:105-138`;
  `Payload/BattleMode.cs:457-491`; CHANGELOG.md:129-132.

### B8 · Latching the **attempt** instead of the **success** when the peer loads late

- **What happened** — BT frequently loads after this module, so the first `Apply` finds nothing.
  `ShareTimeControl`'s `_resolved` flag latches after a *single* resolution attempt, so if BT is not
  in the AppDomain on the first tick, resolution is never retried for the rest of the process —
  while `TimeEnforcementGuard` and `JoinSyncPauseEscape` return early **without** latching and are
  retried from later lifecycle hooks.
- **Lesson** — Latch the success, never the attempt, and retry from `OnBeforeInitialModuleScreen` /
  `OnGameStart`. Decide per-lookup whether a *negative* result should be cached at all: `BattleMode`'s
  `_searched` latch means a late-loading BT is never found for that generation, in the same codebase
  that deliberately retries elsewhere. A negative cache and a retry policy side by side will disagree.
- **Now** — `Payload/ShareTimeControl.cs:152-158` against `Payload/TimeEnforcementGuard.cs:56-59`,
  `Payload/JoinSyncPauseEscape.cs:69-73` and `Payload/PayloadEntry.cs:115-124`; the negative cache at
  `Payload/BattleMode.cs:497-503`.

### B9 · The peer-assembly scan is single-assembly by construction

- **What happened** — `FindTransferCancel` and `PeerDetection.FindCoopType` both `return null` from
  **inside** the assembly loop, once the first assembly named `BannerlordTogether` has been scanned.
- **Lesson** — Know the shape you shipped: if BT were ever loaded twice — two AppDomain entries, a
  shadow copy — only the first is searched and every lookup silently resolves nothing. The BT
  assembly's simple name is exactly `BannerlordTogether`; that string plus the late-load retry is the
  whole entry point for reflecting into it without a compile-time reference.
- **Now** — `Payload/JoinSyncPauseEscape.cs:217-219`; `Payload/BattleMode.cs:476-479`.

### B10 · Disabling a peer's per-frame feature to stop a freeze

- **What happened** — BT's `CoopSubModule.TryBackgroundCampaignTick` runs `Campaign.RealTick` + `Tick`
  on **every** application tick while the host is in a mission, with no time budget. When a campaign
  tick became pathologically expensive — a third army joining an ongoing battle put
  `EncounterManager.HandleEncounters`, encounter-hold checks and hourly-AI catch-up into multi-second
  ticks — every frame drowned in background campaign work. Turning the feature off would have killed
  the co-op background world: the other player's map stops advancing.
- **Lesson** — **Throttle, do not disable.** An equal-time backoff — exceed a 100 ms budget, then
  pause background ticking for exactly as long as that tick took, capped at 10 s — bounds the worst
  case, keeps the feature, and degrades proportionally with the pathology instead of needing a tuned
  constant per scenario. Prove skipping is safe **by construction** first: BT's own method begins with
  unconditional early-outs (paused / saving / not host), so its callers demonstrably tolerate no-op
  ticks. Extract the decision into a pure static function so it is testable with no game running.
- **Now** — `Payload/BackgroundTickBudgetGuard.cs:8-30,36-40,85-95,143-156`;
  `UPSTREAM_BUG_REPORT.md:148-151`.

### B11 · A prefix/postfix timing pair silently measures garbage

- **What happened** — When the prefix **skips** the call, the postfix still runs and the measurement
  is meaningless.
- **Lesson** — Use a zero sentinel: stamp `_startTimestamp` only when the call actually proceeds,
  return early from the postfix when it is 0, and reset it to 0 immediately after measuring.
  `Stopwatch.GetTimestamp`/`Frequency` gives allocation-free high-resolution timing for a per-tick
  hook.
- **Now** — `Payload/BackgroundTickBudgetGuard.cs:97-121`.

### B12 · A peer's own log line inside a hot prefix is part of the pathology

- **What happened** — BT's `SuppressClientMirroredPartyHandleEncounterPatch.Prefix` builds a log
  string (`String.Concat`) on **every** invocation; under encounter churn that is itself a measurable
  cost, and it was part of the observed freeze.
- **Lesson** — Never build a log string unconditionally in a per-call hot path — build the message
  only when you will emit it, and route high-frequency lines through a rate-limited or coalescing
  emitter that carries a since-last-report count.
- **Now** — `UPSTREAM_BUG_REPORT.md:143-145,156-157`; the counter-pattern at
  `Payload/PartyAiCrashGuard.cs:149-167` and `Payload/BackgroundTickBudgetGuard.cs:129-136`.

### B13 · Stripping the peer's patches while a partner is connected

- **What happened** — Lifting BT's battle patches sabotages a live session: the partner's army never
  enters the authoritative battle. The mirror-image failure was already recorded — hosting alone with
  BT active brings battles up with empty formations and a `SetupTeams` NRE, because BT's pipeline
  strips the player side (proven 2026-08-18).
- **Lesson** — When your fix works by **disabling someone else's code**, enumerate the blast radius
  of being wrong and bias the default away from it. Keep the target list narrow and say why: this one
  is battle-mission-only, and "campaign/map co-op machinery is intentionally not listed" because
  lifting map sync would break co-op even in a solo-hosted session's overworld. A compatibility mod
  sometimes has to *restore vanilla behaviour* rather than add behaviour.
- **Now** — `Payload/BattleMode.cs:16-19,37-38,127-132`. The list itself is worth reading before
  writing any co-op, battle-AI or deployment mod: 24 members spread across
  `TaleWorlds.CampaignSystem`, `TaleWorlds.MountAndBlade` and `SandBox` — a namespace split
  `AccessTools.TypeByName` has to be told about — including a base/derived pair *across* that split
  (`:51-52,:59-60`). `BattleInitializationModel.CanPlayerSideDeployWithOrderOfBattle` is the concrete
  caching wrapper (it fills `_isCanPlayerSideDeployWithOOBCached`, then `callvirt`s the **abstract**
  `CanPlayerSideDeployWithOrderOfBattleAux`), and `SandboxBattleInitializationModel.GetAllAvailableTroopTypes`
  is the concrete override of an abstract base member — `BattleInitializationModel.GetAllAvailableTroopTypes`
  has no body. That is exactly the shape **H20** warns about: the abstract declarations on the base
  cannot be patched, so the `!method.IsAbstract` + `DeclaredOnly` filter at
  `Payload/BattleMode.cs:236-244` is load-bearing for this list. Note that the Sandbox model
  overrides `…Aux`, not `CanPlayerSideDeployWithOrderOfBattle` — chasing an override of the wrapper
  in `SandBox.dll` finds nothing (`:39-63`).

### B14 · Patching out a peer's refusal instead of satisfying its rule

- **What happened** — BT approves a formation for the client only when it holds the client's troops
  **alone** (`IsClientFormationCommandApproved` = `FormationHasClientOwnedUnit &&
  !FormationHasHostOwnedUnit`, or the client is its `PlayerOwner`/`Captain`). Vanilla spawns *both*
  parties' troops into the same class formations, so every formation is mixed, the client's
  `AllowedFormationMask` stays empty, and every client order is refused with
  `[SPNATIVE ORDER-GUARD] blocked local`.
- **Lesson** — Read the peer's rule from **its** IL and change the **input** so the rule grants the
  capability, rather than patching the refusal path. Splitting the two parties into separate formation
  blocks (host I–IV, client V–VIII) makes BT's own approval, membership snapshot and order forwarding
  do the rest — with no patch on BT's decision code at all.
- **Now** — `Payload/CoopCommandSplit.cs:19-42`; `docs/ENGINE-NOTES.md`, the BT client command model.

### B15 · Applying a fix on the side of the session that cannot own it

- **What happened** — On a BT client the host's command assignment is authoritative, so a siege
  command fix there would silently fight the network layer.
- **Lesson** — Detect the peer role and **stand down** with an honest, actionable on-screen message
  ("host the session"), rather than half-applying and leaving the player with a fix that looks
  installed and does nothing.
- **Now** — `Payload/SiegeCommandGuard.cs:52-53,221,399-405`.

### B16 · Assuming the peer owns identity in a shared save

- **What happened** — A Bannerlord save stores exactly **one** player identity — whoever was
  `MainHero` when it was saved. BT's identity registry (slots, Steam/password claims) is consulted
  only on the **client join** flow, and `SharedSaveMode` is a bare session flag (verified by assembly
  scan). Nothing fixes the identity of the person *loading* the save, so the second host plays as the
  first host's hero.
- **Lesson** — Find exactly where the peer's handling stops and supply only the missing piece: a
  per-machine hero map keyed by `Campaign.UniqueGameId`, applied through vanilla's own
  `ChangePlayerCharacterAction` (the death-succession path). Prefer an explicit one-time claim plus
  one unambiguous auto-case (a campaign created here) over any inference — a wrong guess replicates
  the very bug being fixed. Only follow the record **forward** when the recorded hero is gone or dead;
  a living mismatch is never overwritten, and switching to a dead or missing recorded hero would be
  worse than doing nothing.
- **Now** — `Payload/CoopHeroIdentityLock.cs:11-33,117-125,167,176-178,199-209`; CHANGELOG.md:137-141.

### B17 · A hand-rolled JSON writer for a persisted map

- **What happened** — The per-machine hero map is written and read with flat regex-parsed JSON, to
  avoid taking a serializer dependency inside a Bannerlord module. The writer does **no escaping**.
- **Lesson** — It is safe only for the narrow key/value shape it actually holds — campaign GUIDs and
  hero `StringId`s, both quote-free — and anything richer needs a real serializer. What makes a naive
  parser trustworthy at all is a self-test pinning the round trip (`ParseMap(FormatMap(x))`), so write
  that test in the same change as the writer.
- **Now** — `Payload/CoopHeroIdentityLock.cs:257-314`, self-test at `:316-327`; the escaping gap at
  `:266,285-286`.

### B18 · Inventing a teardown instead of reusing the peer's own recovery routine

- **What happened** — A stuck join hold freezes the host. A bespoke teardown would leave BT in a
  state it has no code path out of.
- **Lesson** — Invoke the exact method the peer's own watchdog/timeout calls — here its
  transfer-cancel router — so all of its cleanup happens the way it intends. Then clear what your own
  change left behind: cancelling the transfer alone is not enough, because the player's pause presses
  may have toggled BT's **manual** pause reason on, and the fix then appears to do nothing.
- **Now** — `Payload/JoinSyncPauseEscape.cs:22-33,313-322,323-325`.

### B19 · Trusting the peer's declared state over its observed traffic

- **What happened** — Reflection reads of BT's session state proved unreliable *mid-session*: on
  2026-08-19 20:27 they reported "no remote player" while BT packets were arriving every two seconds.
  The mod went vanilla mid-session and the two players' game speeds desynced.
- **Lesson** — Observed traffic beats declared state. The peer's **own packet handlers firing is proof
  of a live session**, so stamp an activity tick from a traced call and let a 15-second window
  short-circuit the tri-state answer to true. Guard the stamp against false positives — the same
  methods also run once during a solo game load, so the call must be proven network-originated first
  (**E18**). Every consumer that gates on "is a peer there" — battle mode, time enforcement, both sync
  features — inherits that fail-safe, which is what makes "never fight real co-op" provable rather
  than hoped for.
- **Now** — `Payload/BattleMode.cs:392-416,511-555`; consumed at
  `Payload/TimeEnforcementGuard.cs:155-156`, `Payload/ClanModeSoloFix.cs:143,161-165`,
  `Payload/PregnancySync/PregnancySyncGuard.cs:251-258`, `Payload/StashSync/StashSyncGuard.cs:148-157,373`.

### B20 · Resolving the peer's two parties to a degenerate answer instead of refusing

- **What happened** — `CoopCommandSplit` has to name which of the two parties in a co-op battle is
  the host's and which is the client's, from BT's `GhostHeroStringId`. Several ways of getting that
  wrong all produce a *plausible* answer rather than an error: an empty ghost id, an id that resolves
  to no hero, a hero with no party, or a ghost party whose `Party` **is** `PartyBase.MainParty`. Any
  of them collapses both sides into one block — which hands one player command of both armies, the
  exact opposite of what the feature promises.
- **Lesson** — Refuse to resolve rather than resolve wrongly. Identity resolution needs the same
  discipline **B19** applies to session state: when the inputs are ambiguous the answer is *not yet*,
  not a guess. Make the refusal cheap to repeat — retry on a timer instead of failing the feature
  permanently.
- **Now** — `Payload/CoopCommandSplit.cs:347-366` — three `return false` refusals (null campaign or
  main party; empty ghost id; a ghost party that is null, party-less, or **is** `PartyBase.MainParty`)
  after the solo bail at `:343-345`. Retry is throttled to one attempt per `ResolveRetryMs` = 2 s
  (`:50,:333-338`); callers at `:197,:234`.

> **Good to know — BT's external observability surface.**
> Everything a companion mod can learn about a BT session without a compile-time reference:
> `CoopSession` statics `IsClient` / `IsHost` / `IsActive` / `Server.GameplayPeerIds` /
> `Server.ConnectedPeerIds`, read **static-property-first then static-field**, because peer mods
> convert between the two across releases (`Payload/BattleMode.cs:493-562,575-600`); BT's per-role
> logs on the Desktop — `bt-sync-host.txt` / `bt-sync-client.txt` / `bt-sync-solo.txt` — and its
> `Modules/BannerlordTogether/RuntimeDataCache/*.rdc` (`Payload/BootstrapWatch.cs:8-13,54-65,100-123`);
> and the assembly itself at
> `<Game>/Modules/BannerlordTogether/bin/Win64_Shipping_Client/BannerlordTogether.dll`, which is where
> you point an IL probe (`tools/il-probes/README.md:32`).
> Two BT-side facts that cost real sessions and belong in a report rather than in a workaround: the
> client bootstrap trio (`TryVerifyNativeActionCacheWhenCampaignMapReady`,
> `_nativeActionCacheVerified`, `_harmonyPatchBootstrapAttempted`) runs on **client sessions only**;
> and BT's dedicated-server flow has the owner window and the spawned authority instance both binding
> the same hardcoded port 47770, so the authority fails all five bind attempts and self-destructs
> (`UPSTREAM_BUG_REPORT.md:34-38`) — two components of one flow must never share a hardcoded port.

> **Good to know — obfuscated members, and how these names were recovered.**
> Several BT targets here came from **runtime stack traces**, not from a decompile (`SetPaused`,
> `ApplyTimeState`, `BattleSyncBehavior.ProcessPendingClientEncounterRequests`), so a rename disables
> the hook with no error beyond a missing "tracer active" line — always log the patched **count** and
> treat zero as drift rather than as absence of the problem
> (`Payload/TimeEnforcementGuard.cs:22-25,133-137`; `Payload/EncounterLoopGuard.cs:8-14` — where the
> stack traces also gave up the mechanism behind the infinite `encounter_meeting` loop:
> `BattleSyncBehavior.ProcessPendingClientEncounterRequests` re-applies a pending entry that is never
> consumed, `ApplyEncounterRequestNow → StartPartyEncounter → RestartPlayerEncounter`). Where the
> name is obfuscated but the shape is known, search by **signature** — return type plus parameter
> types, including a parameter type discovered at runtime from another member's `ReturnType`
> (`Payload/JoinSyncPauseEscape.cs:140-157`) — and locate an obfuscated declaring type by requiring
> **two independent facts** of the same type (it references `SaveTransferAckPacket` *and* declares
> `static void A(string,string,bool)`), logging the resolved type name so drift stays diagnosable
> (`:159-226`). `ClanModeSyncBehavior.CurrentMode` returns obfuscated enum `af` (`bI` = 0 Unknown,
> `bi` = 1 Separate) and stays Unknown forever when hosting with no peer, which is what produces
> "[BT] Marriage is blocked until clan mode is synchronized" in a solo-hosted session
> (`Payload/ClanModeSoloFix.cs:10-21,66-71`).

---

## 11. Co-op sync — wire protocols and shared state

Two features replicate state between machines over BT's transport: pregnancy/birth sync
(`Payload/PregnancySync/`) and settlement stash sync (`Payload/StashSync/`). Both parse hostile bytes
on someone else's network thread and mutate the campaign on ours. The stash feature can **delete
player items**, which is why most of this section is about the failure directions of a snapshot.

### S1 · Treating absence from a snapshot as deletion

- **What happened** — The first stash design was `Clear()` then re-add whatever the payload names.
  The recorded path: a client stashes a player-crafted sword; the host applies the snapshot and skips
  it (the id does not resolve there); later the host edits that stash and its snapshot has no sword;
  the client clears and re-adds only what the payload names — and the sword is gone irrecoverably,
  with **no log line at all**, because the `skipped` counter only counts entries the payload
  contained.
- **Lesson** — Snapshot semantics are correct for anything both sides can **name** and destructive
  for anything the sender cannot **express**. An item's absence from a snapshot is a withdrawal only
  if the sender was capable of mentioning it. Enumerate what your wire format cannot express, and
  preserve exactly that across an apply: read the machine-local stacks out **before** the clear, then
  re-add them after.
- **Now** — `Payload/StashSync/StashSyncGuard.cs:36-45,324-345,362-365`; CHANGELOG.md:204-209.

### S2 · Re-adding every preserved stack after the apply

- **What happened** — Naive preservation duplicates. If the two machines classify an item differently
  — a peer on an older version that sent everything resolvable, a different mod set, or the
  classifier's `catch { return true; }` firing on only one side — the receiver applies the peer's
  stack **and** re-adds its own.
- **Lesson** — Condition preservation on the payload's contents: build a `HashSet` of every id the
  payload names and never preserve one of those. "The payload's word wins for ids it mentions."
- **Now** — `Payload/StashSync/StashSyncGuard.cs:327-345`.

### S3 · A property that sounds like the test you want

- **What happened** — `item.WeaponDesign != null` reads like "was crafted by the player". It is true
  for **every** `<CraftedItem>` definition — 260 in SandBoxCore `weapons.xml` plus 23 tournament
  weapons on Native v1.4.8 — so ~283 ordinary vanilla weapons (most swords, axes, mauls, spears,
  polearms) were classified machine-local and silently stopped syncing, permanently, with a log line
  worded as if that were expected.
- **Lesson** — Verify a predicate against the game's own `ModuleData` counts, not against the name it
  reads like. `ItemObject.IsCraftedByPlayer` is the real test; a StringId round-trip through
  `MBObjectManager` is the second clause. And when the classifier itself throws, err toward
  **preserving** — "unreadable = unexpressible".
- **Now** — `Payload/StashSync/StashSyncGuard.cs:41-43,213-234`.

### S4 · Validating structure but not semantics at parse time

- **What happened** — The parser bounded the entry **count** but not the per-item values. A corrupt
  or truncated packet with `Count = -1` flowed straight into `stash.AddToCounts(item, -1)` on a
  freshly cleared roster. Worse, the original test suite **asserted that negative counts round-trip**
  — the test actively certified the bug.
- **Lesson** — Reject semantically impossible values where you parse them: `Count <= 0`, empty ids,
  implausible totals. And when a review finds a bug, check whether a test is **pinning** it: a
  round-trip test proves fidelity, never validity.
- **Now** — `Payload/StashSync/StashPayloadData.cs:101-104`; `tests/StashPayloadTest/Program.cs:37-53`
  (corrected assertions); `Payload/StashSync/StashSyncGuard.cs:473-478` (the self-test now asserts
  rejection).

### S5 · An assertion that cannot fail

- **What happened** — The "no BT packet with first byte 1..255 is misread as ours" loop can never
  fail, because `IsOurPacket` short-circuits on `data[0] != Marker` by construction — every iteration
  exits at byte 0. The risk it claimed to cover (a real BT packet that actually starts
  `0x00 'B' 'T' 'C' 'S'`) was untested.
- **Lesson** — Ask of every assertion what change would make it **fail**. If there is none, it is
  documentation, not a test — keep it, but say so in the file, and put the transport fact it stands
  for where it belongs (in the framing type's own comment). Derive framing constants from behaviour
  (`framed.Length - payload.ToBytes().Length`) so the test stays honest through refactors.
- **Now** — `tests/StashPayloadTest/Program.cs:70-79`; `tests/BirthPayloadTest/Program.cs:84-97`.

### S6 · Doing engine work in the packet-receive hook

- **What happened** — BT's `ShouldAcceptIncomingPacket` runs on the **LiteNetLib network thread**.
  `HeroCreator`, `MBObjectManager` and `ItemRoster` mutation are main-thread-only; doing them there is
  silent state corruption rather than an exception.
- **Lesson** — Parse on the network thread (bytes are thread-safe), enqueue under a lock, and do all
  engine work on the game `Tick`, with a per-item `try/catch` so one bad item cannot throw onto the
  game loop. This is the general shape for consuming **any** foreign mod's network callback.
- **Now** — `Payload/PregnancySync/PregnancySyncGuard.cs:38-41,99-127,326-333`;
  `Payload/StashSync/StashSyncGuard.cs:57-58,248-254,270-300`.

### S7 · A re-entrancy flag whose thread discipline is unstated

- **What happened** — `_reconstructing` — the flag that stops a birth we create during reconstruction
  from being re-broadcast — is a plain `static bool`, not `[ThreadStatic]`. It is sound **only**
  because reconstruction is confined to the main-thread `Tick` drain.
- **Lesson** — A re-entrancy flag's thread discipline is part of its correctness argument, so write
  it down next to the flag. If the guarded call ever moves off the single thread, the flag has to
  become `[ThreadStatic]` or a depth counter.
- **Now** — `Payload/PregnancySync/PregnancySyncGuard.cs:36,247-250,369-390` against the single-thread
  constraint at `:38-41`.

### S8 · Subscribing to `CampaignEvents` at module or patch time

- **What happened** — `CampaignEvents` resolves through `Campaign.Current`, which is **null at module
  load** and is per-campaign. A subscription made at patch time binds to nothing, or to a dead
  campaign — so the host would never broadcast a birth, silently.
- **Lesson** — Split load-time Harmony patching (safe at load) from per-campaign event subscription
  (must be re-wired at game start). Key the subscription on
  `ReferenceEquals(_subscribedCampaign, Campaign.Current)` with a stable sentinel owner, so it is
  idempotent across payload reloads and re-subscribes on a new campaign. Without that identity key
  you get no listener, a dead listener, or N duplicates after N reloads.
- **Now** — `Payload/PregnancySync/PregnancySyncGuard.cs:59-62,75-96`.

### S9 · Expecting two machines to agree on an object they each created

- **What happened** — Two machines that independently create "the same" hero disagree forever, because
  the engine keys objects by `StringId`.
- **Lesson** — Re-key: `MBObjectManager.Instance.UnregisterObject(obj)`, set `Hero.StringId` to the
  host's id, then `RegisterPresumedObject(obj)`. Make it idempotent by looking the id up first, so a
  resend or a shared base save is harmless.
- **Now** — `Payload/PregnancySync/PregnancySyncGuard.cs:359-362,413-420`.

### S10 · Serializing what the receiver can re-derive

- **What happened** — The SPEC called for clan, culture and birthday on the wire. `CampaignTime` has
  no round-trippable form, and `DeliverOffSpring(mother, father)` reproduces all three identically on
  both machines anyway.
- **Lesson** — Send only what the receiver **cannot** deterministically re-derive — here id, gender,
  name and appearance. It shrinks the wire and removes whole classes of serialization problems by
  making the engine's own determinism part of the protocol.
- **Now** — `Payload/PregnancySync/BirthPayloadData.cs:33-43`; `Payload/PregnancySync/PregnancySyncGuard.cs:372`.

### S11 · A magic ordinal for a foreign enum, behind a health flag that does not cover it

- **What happened** — `private const int StashMode = 3` for
  `Helpers.InventoryScreenHelper+InventoryMode.Stash`. If the enum shifted, no stash would ever sync
  while `Diag.Report` still printed **active**, because the `ok` flag reflects only *patch* success,
  not mode detection — and the loopback self-test covered framing only.
- **Lesson** — Resolve enum values from the live type at runtime (`AccessTools.TypeByName` +
  `Enum.Parse` + `Convert.ToInt32`), keep the known value only as a labelled fallback, and log when
  the live value differs. Then check that your health signal actually covers the thing that can
  silently break, not just the thing that throws.
- **Now** — `Payload/StashSync/StashSyncGuard.cs:52-55,95-115`.

### S12 · A fail-open reflection check with a bare `catch {}`

- **What happened** — `IsLocalStashScreenOpen` returned "not open" whenever anything failed, so if
  `Campaign.InventoryManager` were ever renamed the deferral would never engage and peer updates would
  clear the roster underneath a live stash screen — with zero diagnostic, forever.
- **Lesson** — Where a fail-open path exists, make it **audible**: distinguish "legitimately absent"
  (the manager is null) from "the reflection chain broke" (a property lookup returned null), and log
  once, naming the suspected cause and the consequence. One line, once, converts a permanent silent
  behaviour loss into a diagnosable one.
- **Now** — `Payload/StashSync/StashSyncGuard.cs:51,382-400`.

### S13 · Dropping the whole packet on one bad entry

- **What happened** — `FromBytes` returns null on the first malformed entry, so one bad stack discards
  the entire stash snapshot rather than the offending stack.
- **Lesson** — Accepted deliberately as the fail-safe direction now that the sender can no longer emit
  those, but the blast radius is the **packet**, not the entry. Record such a decision instead of
  silently living with it.
- **Now** — `Payload/StashSync/StashPayloadData.cs:101-104`.

### S14 · Confusing "inherent to the design" with "a bug that looks inherent"

- **What happened** — The **first** sync between two players whose stashes have already diverged — the
  exact state the feature exists to fix — silently replaces one side wholesale, with no warning. That
  is inherent to snapshot semantics and was knowingly accepted. The crafted-item deletion (S1) looked
  the same but was **not** inherent: the sender *has* the item and merely cannot say so.
- **Lesson** — Before accepting a destructive edge as inherent, ask whether the sender actually lacks
  the information or merely lacks the vocabulary. Only the first is inherent.
- **Now** — `Payload/StashSync/StashSyncGuard.cs:36-45`; both findings recorded against
  CHANGELOG.md:204-209.

### S15 · Shipping the data-destructive feature default-on

- **What happened** — `stashSync` shipped default **true** with the crafted-item deletion path live,
  while `pregnancySync` — which can only *add* a hero — was held off pending a live two-machine
  validation. The review asked for the same bar on both.
- **Lesson** — The default-on decision belongs to the risk of the **worst** path, not to how confident
  the wire tests are. A feature that can delete player data should not out-rank a feature that cannot.
- **Now** — `Harness/GuardConfig.cs:94,96` (both ship `true` today).

### S16 · Claiming a channel on a marker byte alone

- **What happened** — Byte 0 is free on BT's transport, but only because **three** facts hold
  together: dispatch is `(PacketType)data[0]`, the `PacketType` byte enum consumes every value 1..255,
  and `OnNetworkReceive` rejects empty packets while the dispatch switch has no `case 0` and no
  `default`.
- **Lesson** — Write down every fact a safety argument rests on, in the file, with the note that it
  was decompile-proven — and add a 4-byte magic anyway so a misread is impossible in both directions
  ("safe twice over"). A per-feature magic (`BTCG` for births, `BTCS` for stash) multiplexes several
  features on one borrowed channel: adding feature N+1 costs one constant and cannot break feature N,
  and the property is testable as four-way discrimination rather than assumed.
- **Now** — `Payload/PregnancySync/BirthWireFraming.cs:5-25`; `Payload/StashSync/StashWireFraming.cs:5-20`;
  the receive prefixes at `Payload/PregnancySync/PregnancySyncGuard.cs:316-346` and
  `Payload/StashSync/StashSyncGuard.cs:238-267`.

### S17 · Choosing your own choke point when the peer already has one

- **What happened** — Stash sync needs a commit point that is UI-driven and does not fire mid-drag.
- **Lesson** — Reuse the choke point the host mod already patches — `InventoryLogic.DoneLogic`, which
  BT patches for the workshop warehouse — and the same private `_inventoryMode` discriminator. You
  inherit its correctness argument and stay phase-aligned with the other mod.
- **Now** — `Payload/StashSync/StashSyncGuard.cs:16-24,71-79`.

> **Good to know — the shapes that make this safe.**
> **Consuming a foreign packet**: a Harmony prefix on the peer's accept hook that sets
> `ref __result = false` and returns `false` for your own frames — with the deliberate choice to
> return **true** on any exception, so the host mod handles anything you failed to parse
> (`Payload/PregnancySync/PregnancySyncGuard.cs:316-346`; `Payload/StashSync/StashSyncGuard.cs:238-267`).
> **Never-throw parsers**: return null rather than throwing, drop on an exact format-version
> mismatch (mixed-version co-op is the normal case), and validate semantics at parse time
> (`Payload/PregnancySync/BirthPayloadData.cs:81-126`; `Payload/StashSync/StashPayloadData.cs:67-114`).
> **Topology**: a host relay with an apply-never-sends invariant on a client→server-only star
> (`CoopSession.Client.SendRaw` reaches only the host) gives N-peer convergence with no peer list and
> no per-peer addressing, and the loop-freedom is one line you can check
> (`Payload/StashSync/StashSyncGuard.cs:29-31,371-376,440-448`).
> **Liveness**: both features' send gates read BT session state through the tri-state helper, because
> a confident `false` there silently disables replication — see **E18** and
> `Payload/BattleMode.cs:396-416,511-554`, consumed at
> `Payload/PregnancySync/PregnancySyncGuard.cs:251-258` and `Payload/StashSync/StashSyncGuard.cs:148-157,373`.

> **Good to know — testing a wire format with no game.**
> The engine-free wire model is `<Compile Include>`d into a headless net472 console test — the
> **shipping** source, never a copy, which structurally prevents the stale-copy failure mode. The
> stash suite links all four wire files because cross-discrimination between the two magics is part
> of the contract, and both suites exit 1 on failure
> (`tests/BirthPayloadTest/BirthPayloadTest.csproj:15-19`;
> `tests/StashPayloadTest/StashPayloadTest.csproj:19-24`; `tests/BirthPayloadTest/Program.cs:108`;
> `tests/StashPayloadTest/Program.cs:92`). In-game, a loopback self-test runs the real
> serialize → frame → receive-path parse against **real** engine data (real StringIds, real
> `BodyProperties` XML, real unicode names), creates nothing, is registered even when the feature is
> disabled, and reports PASS-with-explanation when the probe object is legitimately absent — with
> negative cross-feature and cross-protocol assertions, because "nothing else parses as mine" is the
> half that protects the host mod's traffic
> (`Payload/PregnancySync/PregnancySyncGuard.cs:48-49,486-523`; `Payload/StashSync/StashSyncGuard.cs:65,459-499`).
> Diagnostics are deliberately separated from the feature flag: the conception observer installs
> regardless of `pregnancySync`, because turning a feature off should not blind you to the game
> behaviour you need in order to debug it — scoped to the player's own clan so it is not a log bomb
> (`Payload/PregnancySync/PregnancySyncGuard.cs:50-53,188-191`).

---

## 12. Tooling, build and deploy

Distribution here is deliberately zero-infrastructure: `install.cmd` `curl`s three files out of the
tracked `dist/` folder on `raw.githubusercontent.com`, so **a push is a release**. That one fact is
behind most of this section.

### T1 · Forgetting that `dist/` is the release

- **What happened** — After the v1.2.0 split into two assemblies, `dist/` still held the v1.1
  **monolithic** DLL and `install.cmd` downloaded only the harness. Every README one-liner install got
  zero v1.2.x fixes — and, on the harness alone, no payload at all. Local builds and local tests were
  all fine.
- **Lesson** — After an architecture change, the **distribution artefacts are part of the change**.
  All three files (both DLLs and `SubModule.xml`) move together and are hash-verified across build
  output, the live game module and `dist/`. The corollary every contributor has to internalise: do not
  push mid-investigation, because a push ships to players.
- **Now** — `install.cmd:9,58-60`; `.gitignore:1-3` (only `bin/`, `obj/`, `.runner/` are ignored, so
  `dist/` is tracked); CHANGELOG.md:265-271; the deploy checklist in `CLAUDE.md`.

### T2 · The build stamps only one of the two `SubModule.xml` files

- **What happened** — `StampSubModuleVersion` pokes
  `XmlInputPath="$(MSBuildThisFileDirectory)SubModule.xml"` — the repo root copy. It never touches
  `dist/SubModule.xml`, so copying it across is a manual step, and it is the easiest of the three
  artefacts to forget.
- **Lesson** — Know which artefacts your build actually stamps, and treat the rest as an explicit,
  checklisted copy.
- **Now** — `Directory.Build.props:12-19`.

### T3 · Writing the version number anywhere but the single source

- **What happened** — `SubModule.xml`'s launcher-visible version drifted to v1.0.0 while the
  assemblies carried a different number.
- **Lesson** — One source (`Directory.Build.props` `<Version>`), stamped into both assemblies and
  poked into `SubModule.xml` by the build, and **read back from the assembly identity** at runtime for
  the log banner. A log or crash report then cannot lie about which build produced it — which is the
  prerequisite for any field report being usable.
- **Now** — `Directory.Build.props:3-9`; `Harness/Diag.cs:15-30,58-61`; CHANGELOG.md:260-263.

### T4 · Installing or deploying one of the two assemblies

- **What happened** — Since v1.2.0 the mod is two assemblies. A module with only the harness loads and
  runs with **zero** guards.
- **Lesson** — "Both must be installed together." The installer downloads harness + payload +
  `SubModule.xml` and aborts on any single failure; replicate that invariant in every manual deploy.
- **Now** — `install.cmd:46-60`.

### T5 · A folder check that accepts the wrong folder

- **What happened** — The installer's validity test is only `exist "%GAME%\Modules"`, even though the
  prompt asks for the folder containing `bin\` and `Modules\`. Any folder with a `Modules` subfolder
  passes, and the installer happily creates
  `Modules\BLTDeploymentCrashGuard\bin\Win64_Shipping_Client` somewhere useless and reports success.
- **Lesson** — Validate against something that can only be true of the real target.
- **Now** — `install.cmd:32,36-39`.

### T6 · Three player-facing scripts with three different auto-detect lists

- **What happened** — `install.cmd` and `share-log.cmd` each scan the same 11 candidate paths;
  `collect-diagnostics.cmd` scans only 6 — `D:\Steam`, `E:\Steam`, `F:\Steam` and both `G:` paths are
  missing from the collector. A player whose install was auto-found at install time is dropped to a
  manual prompt when they try to collect diagnostics: the worst possible moment.
- **Lesson** — Duplicated detection logic drifts. One shared helper, or one list, and a documented
  env-var override (`BANNERLORD_DIR`) so CI or a dev can target a non-standard install.
- **Now** — `install.cmd:13-27`; `share-log.cmd:13-24`; `collect-diagnostics.cmd:13-20`.

### T7 · A PowerShell dependency inside a player-facing script

- **What happened** — `collect-diagnostics.cmd` shells out to
  `powershell -NoProfile -Command "Compress-Archive …"` — the only PowerShell dependency in the repo's
  tooling. Its output is swallowed (`>nul 2>&1`), so if PowerShell is restricted or blocked the zip
  step fails silently and the player is left with a staged folder and an error.
- **Lesson** — A player-facing script should depend only on what every install has, and a failure in
  it must be visible. This one also means the collector cannot be exercised from an agent environment
  that forbids PowerShell.
- **Now** — `collect-diagnostics.cmd:46-47`.

### T8 · Detecting upload success from the first characters of the response body

- **What happened** — Both upload scripts decide success with `findstr /b "https://"` on the response.
  Any host response that merely *starts* with a URL — an error page, a rate-limit notice — is treated
  as a successful link and handed to the player. There is no HTTP status check beyond `curl -f`.
- **Lesson** — Copy the shape knowingly: a body prefix is not a status code. The rest of the design is
  worth keeping — stage with `>nul 2>&1` per copy so missing files are skipped rather than fatal, try
  a second host, `echo %URL%| clip`, and print the local path if both hosts fail, so the flow never
  dead-ends.
- **Now** — `share-log.cmd:46,51`; `collect-diagnostics.cmd:53,56`.

### T9 · Uploading logs unredacted to a public host

- **What happened** — `CrashGuard.log`, `guardconfig.json`, BT's sync logs and the crash report all go
  up as-is to an anonymous public file host with a 24h/72h link; the in-game streamer additionally
  publishes to a **public** `filebin.net` bin whose id lives in a plain-text sidecar
  (`logstream.txt`) written by the installer from `BLTGUARD_BIN`, and tags uploads with the sanitized
  machine name.
- **Lesson** — Zero-touch remote diagnostics trade privacy for convenience. Anything written to the
  log — file paths including the Windows user name, save names, hero names — becomes publicly
  fetchable once a bin is configured. Say so where the feature is enabled.
- **Now** — `share-log.cmd:45,50`; `collect-diagnostics.cmd:33-38,52-54,66-67`;
  `Payload/LogStreamer.cs:8-17,44-74,150,184-195`; `install.cmd:62-64`.

### T10 · Path values that carry embedded quotes

- **What happened** — Both the `for` auto-detect loop and the `set /p` prompt can leave a value with
  embedded double quotes, which then breaks every subsequent path concatenation.
- **Lesson** — Strip them once, immediately after the value is obtained: `set "GAME=%GAME:"=%"`. All
  three scripts do it.
- **Now** — `install.cmd:35`; `share-log.cmd:34`; `collect-diagnostics.cmd:25`.

### T11 · Overwriting a DLL the running game has locked

- **What happened** — A plain overwrite of the mod DLLs fails while Bannerlord is running, because it
  locks the loaded module assemblies — so an update while the game is open just failed.
- **Lesson** — Windows still permits **renaming** a file that is locked for write: move the old file
  aside to `.prev` and write the new one at the original name. The `:fail` path still tells the player
  to close the game, because `curl` can fail for other reasons.
- **Now** — `install.cmd:49-56,76-80`.

### T12 · Expecting the manifest to order the load

- **What happened** — `<DependedModule Id="BannerlordTogether" Optional="true" />` makes the
  dependency non-fatal but does **not** order it; ordering is the launcher list order, and ticking
  this mod *before* BT is wrong.
- **Lesson** — The manifest looks like it guarantees ordering and does not, so the instruction has to
  reach the player: the installer prints "tick it in the Singleplayer mods list, **AFTER**
  BannerlordTogether". A BT companion also declares `SingleplayerModule=true` /
  `MultiplayerModule=false` (BT co-op runs off the singleplayer list) and `IsTWCompatible=false` as an
  unsigned Harmony mod.
- **Now** — `SubModule.xml:5-7,13`; `install.cmd:70-71`.

### T13 · Shipping the runtime code-loading capability enabled

- **What happened** — Hot-reload watches the filesystem and loads assemblies at runtime. Enabled on a
  player install, that is a code-injection surface.
- **Lesson** — Gate it behind **two** conditions: the config flag *and* a developer-only marker file
  (`.hotreload-dev`) in the module root. Shipped config defaults get flipped and copied around; a file
  a developer must create by hand does not survive a normal install.
- **Now** — `HOTRELOAD.md:15-21`; `Harness/HotReload.cs:69-71`; `Harness/GuardConfig.cs:111`.

### T14 · Reloading while foreign patches are lifted — a known open gap

- **What happened** — `BattleMode`'s stash of **BT's** lifted patches is a payload static, and every
  static is fresh per generation. Reloading while in `battleMode=solo` can therefore leave BT's battle
  patches permanently lifted for that session. Reloading in `battleMode=coop` is unaffected, because
  nothing is lifted.
- **Lesson** — Any component that stashes **foreign** state must serialize that stash across
  generations or refuse to reload. Until it does, the honest instruction is "restart if battle mode
  misbehaves after a reload".
- **Now** — `HOTRELOAD.md:65-68`; `Payload/BattleMode.cs:75`; CHANGELOG.md:329-331; the cross-generation
  bag that should hold it at `Harness/SharedState.cs:6-48`.

### T15 · Testing a harness or load-time change in a stale process

- **What happened** — The harness DLL is locked while the game runs, and a fix that must beat the CLR
  touching a type (`MovementOrderTypeInitGuard`) cannot be delivered by a payload reload at all.
- **Lesson** — Know which changes a hot-reload can carry. Payload guard/fix/tracer edits: yes. Harness
  edits and load-time fixes: a fresh launch, every time. A load-time fix that arrived too late must
  say so in its own log line rather than appearing to work.
- **Now** — `HOTRELOAD.md:5,63-64`; `Payload/MovementOrderTypeInitGuard.cs:36-39,67-71`;
  `CLAUDE.md`, "While the game is running".

### T16 · A config file that is cached for the whole session

- **What happened** — `GuardConfig` reads `guardconfig.json` once behind a `_loaded` latch and holds
  the text for the session. Editing the file does **not** take effect on a hot-reload — so code
  changes could land mid-session but the flag that selects them could not, and enabling tracing meant
  restarting the game and losing the live repro.
- **Lesson** — Any flag you will need to flip **during** a reproduction must be read fresh from disk
  at apply time, with the cached accessor only as a fallback on read failure. Everything else can stay
  cached.
- **Now** — `Harness/GuardConfig.cs:26-48`; the bypass at `Payload/PayloadEntry.cs:77-93,211-232`.

### T17 · An empty string in the shipped config defeating a code-side fallback

- **What happened** — `GuardConfig.String()` treats an explicitly **empty** JSON value as a successful
  match, so the shipped `"payloadSourceDir": ""` returns `""` and overrides the caller's fallback.
  `HotReload` passes `<moduleRoot>/PayloadSource` as its fallback, receives `""`, and logs
  `sourceDir=(none)`.
- **Lesson** — A regex "key present" test is not the same as "key has a usable value". Either treat
  empty as absent, or do not ship empty-string defaults for keys whose fallback lives in code. The
  same reader has two further documented quirks worth knowing: a commented-out or duplicated key still
  matches, and only exact literals are honoured.
- **Now** — `Harness/GuardConfig.cs:66-80,113` against `Harness/HotReload.cs:72,74-75`.

### T18 · Leaving a superseded diagnosis in the log message

- **What happened** — The LoadFrom-dedup warning still blames a missing "unique `AssemblyVersion`
  revision (Deterministic build?)", a few lines below the field-proven root cause that LoadFrom dedups
  by **name** only.
- **Lesson** — When a diagnosis is superseded, update the log text too. A stale message sends the next
  investigator down the version path instead of the name path — the same weeks that were already paid
  for once.
- **Now** — `Harness/HotReload.cs:288-293` against `:321-323`.

### T19 · A "summary" getter with a side effect

- **What happened** — `Diag.HealthSummary()` calls `Log.Screen` when a critical component is missing,
  so every call re-warns the player.
- **Lesson** — A function named like a read is expected to be idempotent. Either make it one, or call
  it exactly once per generation — which is what the reload engine does.
- **Now** — `Harness/Diag.cs:87-104`; called once at `Harness/HotReload.cs:380-381`.

### T20 · Not knowing which lifecycle points can recover a failed load

- **What happened** — `EnsureLoaded` is wired only into `OnGameStart` and
  `OnBeforeInitialModuleScreenSetAsRoot`. `Tick()` and `OnMissionInit()` never retry, so a payload that
  fails at both wired points stays off for the whole session.
- **Lesson** — Write down the recovery points you actually have, and make the terminal failure loud:
  the first payload-load failure was **file-log only**, and a whole unprotected session was played with
  the game looking completely normal. "Not installed" must be as loud as a crash.
- **Now** — `Harness/HotReload.cs:90-131,247-263`.

### T21 · Unpatching the old generation before the new one is in

- **What happened** — The obvious reload order — lift the old generation's patches, then apply the new
  payload — leaves the game with **no patches at all** if the new payload throws.
- **Lesson** — Apply new **first**, swap, and only then `UnpatchAll` the old owner; on any throw keep
  the previous generation and announce it on screen. The worst outcome of a bad build becomes "you are
  still on the previous fix set", never "every guard silently off". Per-generation Harmony owner ids
  (`bltogether.crashguard.gen{N}`) are what make that selective lift possible at all — and are why
  "is this patch mine?" must be a **prefix** question (**H19**).
- **Now** — `Harness/HotReload.cs:14-16,358-360,366-378,387-394`; `Payload/PayloadEntry.cs:108-112`;
  `HOTRELOAD.md:10-13`.

### T22 · Doing the reload on the watcher thread

- **What happened** — A `FileSystemWatcher` callback runs off the game thread, and Harmony patching
  there would run off the main thread. A single file save also raises several
  `Changed`/`Created`/`Renamed` events, so reacting to each one reloads mid-build.
- **Lesson** — The watcher callback only sets a volatile flag and a `TickCount` stamp; the reload
  happens on the game tick after a ~400 ms debounce — with the usual wraparound clause
  (`now < _debounceTick`) on that debounce, since it is a `TickCount` comparison like any other
  (**N16**).
- **Now** — `Harness/HotReload.cs:37,90-103,480-484`.

### T23 · Per-generation registries that accumulate across reloads

- **What happened** — A reload re-runs every `Diag.Report` and every `SelfHealing.RegisterTest`, so
  health entries and self-tests pile up duplicates generation after generation.
- **Lesson** — Reset the per-generation registries before each `Apply` — but deliberately keep the
  **fire counters**, whose survival across a reload is itself the proof that shared state persisted.
  Be able to say which mechanism carries which (**N20**).
- **Now** — `Harness/HotReload.cs:362-364`; `Harness/SelfHealing.cs:94-96`.

### T24 · Uploading the whole log

- **What happened** — Uploading the full multi-MB `CrashGuard.log` blew the request timeout, observed
  in a live test at 21:16:08.
- **Lesson** — Stream only the **tail** (the last 2 MB — recent diagnostics live at the end), read it
  with `FileShare.ReadWrite` so the live logger keeps writing, rate-limit the upload (~60 s), skip
  entirely when the file has not grown, and do it on a ThreadPool worker with explicit timeouts,
  because a synchronous upload on the game thread stalls the game. Name the file
  `blt-<RoleTag>-<MachineName>.log` so two machines' logs can be told apart at a glance — co-op bugs
  are only diagnosable when both sides are read side by side.
- **Now** — `Payload/LogStreamer.cs:92-149,151-159`; the role tag computed at
  `Payload/PayloadEntry.cs:161-187`.

> **Good to know — the dependency-free config reader, and its edges.**
> Config is anchored-regex-scraped out of the JSON text rather than parsed, which avoids shipping an
> assembly (Newtonsoft) that can itself bind-conflict in-process — the same class of problem as
> **N21**. The default file is written on first run with a `_<key>` doc string beside every knob, so
> every setting is discoverable without documentation, and a renamed key can be migrated in place
> (`soloVanillaBattles=false` ⇒ `battleMode=coop`, logged) so old player configs keep working. The
> edges to state out loud: a commented-out or duplicated key still matches, only exact literals are
> honoured, an empty string counts as a hit (**T17**), and the whole text is cached for the session
> (**T16**).
> `Harness/GuardConfig.cs:7-11,26-48,50-80,82-115`; `Payload/BattleMode.cs:349-383`.

> **Good to know — the IL probes, and what they are for.**
> Five self-contained net472 console exes read the **installed** assemblies by path, resolving
> dependencies out of the game `bin` plus module folders: `NameSearch` (find every name containing a
> term), `Inspect` (a type's methods with signatures, fields, properties, enum members **with
> values**), `IlDump` (disassemble a method — `.cctor` and `.ctor` supported), `Callers` (who calls
> this member), `VerCheck` (assembly identity). They read the exact bytes the player is running, so a
> mismatch between your reference assemblies and the deployed game cannot fool you; no licence, no
> GUI, and scriptable from an agent loop. The worked example in the README is the whole
> `MovementOrder` root cause in two commands: dump the `.cctor` (six defaults via `newobj`), dump the
> `.ctor` (`call Mission::get_Current; callvirt Mission::get_CurrentTime`), then a reflection check
> that the type is a `beforefieldinit` value type. `tools/il-probes/README.md:1-44`.

> **Good to know — the Bannerlord module layout and load contract.**
> Engine DLLs live in `<Game>/bin/Win64_Shipping_Client`; Harmony is its own module
> (`<Game>/Modules/Bannerlord.Harmony/bin/Win64_Shipping_Client/0Harmony.dll`); `SandBox.View.dll` is
> in the SandBox module. A module targets **net472** for game 1.4.8. The launcher loads only
> `<DLLName>` and instantiates `<SubModuleClassType>` — with an empty `<Assemblies />`, any second
> assembly must be loaded by the module itself, which is exactly what the harness does. A module
> reaches its own root as the assembly directory plus two `..` levels, which is how `guardconfig.json`
> and `hero-identity.json` are found with no hardcoded path.
> `SubModule.xml:15-22`; `Harness/BLTDeploymentCrashGuard.csproj:6,13-14,30-49`;
> `Payload/BLTDeploymentCrashGuard.Payload.csproj:6,37-38`; `Harness/GuardConfig.cs:17-24`;
> `tools/il-probes/README.md:30-32`.

---

## 13. Process and diagnosis discipline

The habits, not the code. Every one of these was learned by getting it wrong first, and each has cost
at least one session.

### P1 · "The crash is gone" is not "the bug is fixed"

- **What happened** — Suppressing the `DeploymentMissionController` NRE removed the CTD and left
  battles unplayable: every player formation 0/0, with a 105-member unwounded party. The guard worked
  perfectly and the game did not.
- **Lesson** — Verify the **gameplay outcome**, not the absence of an exception. Ship the guard *and*
  file the root cause upstream, and record explicitly which components are reactive safety nets versus
  root fixes, so nobody mistakes a suppressed symptom for a solved bug. Track suppressions as debt
  with the root cause named — a working suppression must not close the investigation.
- **Now** — `UPSTREAM_BUG_REPORT.md:86-92,104-108`; `Payload/PlayerIdentityGuard.cs:16-23`;
  CHANGELOG.md:381.

### P2 · Fixing a report before investigating it

- **What happened** — Two field reports turned out to be by-design behaviour. "Sneak in spawned me as
  a soldier and I cannot command my army" is vanilla's stealth ambush: your own hero re-dressed in
  `Hero.StealthEquipment` with the enemy's colours, orders withheld until the stealth→battle
  transition. Changing the spawn would have broken the designed mission.
- **Lesson** — Prove what the behaviour **is** before deciding it is wrong. The correct ship for a
  discoverability failure is an on-screen explainer plus a guarantee for the part that genuinely is
  fragile — not a behaviour change.
- **Now** — `Payload/StealthHideoutAdvisor.cs:8-26`; CHANGELOG.md:106-118,180-183. `Agent.Main` is
  still your hero throughout; `ChangeHideoutMissionModeToBattle`,
  `StartBossFightBattleModeInternal` and `StartBossFightDuelModeInternal` are where the order
  controller is selected, which is what the command-ownership repair hooks instead.

### P3 · Reaching for exception tooling on a hang

- **What happened** — During the whole-game freeze **nothing threw**. BT's own exception and cooldown
  machinery never engaged; no crash log was produced; every exception-based diagnostic stayed silent.
- **Lesson** — A hang is not a crash. Frame starvation produces no exception, so the instrument is a
  live debugger attach and **repeated managed stack samples of the main thread** — which found the
  budget-free background tick in one session — and the fix is a **time** budget, not a guard.
- **Now** — `UPSTREAM_BUG_REPORT.md:134-138,152-154`; `Payload/BackgroundTickBudgetGuard.cs:8-30`;
  CHANGELOG.md:229-233.

### P4 · Treating a public bug tracker as a diagnosis

- **What happened** — The 66 open Nexus reports contain **no stack traces**. They corroborate only by
  scenario.
- **Lesson** — A public report is a lead, not a diagnosis; prove it in *these* logs. The useful output
  of reading them was a mapping — which reports match a locally-proven root cause, and which BT must
  own.
- **Now** — `UPSTREAM_BUG_REPORT.md:180-191`; the house rule in `CLAUDE.md`, "Never guess a root
  cause".

### P5 · Trusting the changelog or the spec over the code

- **What happened** — Three live drifts. (a) CHANGELOG v1.3.2 documents the first-chance observer as
  armed "only while a character is being created" and "capped per activation"; the shipped code arms
  it session-wide at `Apply` with one global cap of 400. (b) `docs/SPEC-pregnancy-coop-sync.md:58`
  names the file `Payload/PregnancySync/PregnancySync.cs`; the file is `PregnancySyncGuard.cs`. (c)
  The SPEC says the wire suite is 16/16 (`:47`) and that `pregnancySync` defaults **off** (`:61`),
  while the guard header says 24/24 and `Harness/GuardConfig.cs:94` ships `true` — and the guard's own
  header still says "Default OFF until validated live" beside a
  `GuardConfig.Bool("pregnancySync", true)` call.
- **Lesson** — When the implementation improves on the plan, the plan's stale claims become future
  landmines. Trust the code; when a diagnostic's scope is widened or a default is flipped, the
  changelog entry, the spec **and** the file header are all things that silently go stale, so fix them
  in the same change.
- **Now** — CHANGELOG.md:20-24 against `Payload/CharacterCreationTrace.cs:19-27,38-49,133-150`;
  `docs/SPEC-pregnancy-coop-sync.md:47,58,61` against
  `Payload/PregnancySync/PregnancySyncGuard.cs:28-30,45` and `Harness/GuardConfig.cs:94`.

### P6 · Believing a "log-only" header

- **What happened** — `TracePatches`' own header promises "Never changes behavior — every hook is a
  void prefix/postfix that appends a `[TRACE]` line". Three of its hooks call into behaviour-carrying
  code: `BattleMode.DecideAndApply("mission-open")`, `BattleMode.DecideAndApply("start-battle")` and
  `EncounterLoopGuard.NoteEncounterFinish()`.
- **Lesson** — Turning tracing on is **not** behaviour-neutral in this mod: battle-mode re-decisions
  happen at extra points and the encounter-loop guard sees extra finish notifications. Do not treat a
  tracing-on reproduction as identical to a tracing-off one, and do not trust a header over the code.
  Keep decision hooks out of debug-only code paths in the first place — see **E30**.
- **Now** — `Payload/TracePatches.cs:15-16` against `:86-91,179-183,185-189`.

### P7 · Reading a quiet log as proof that nothing happened

- **What happened** — The first-chance capture skips any exception with no SandBox/StoryMode/TaleWorlds
  frame, skips this mod's own namespace, and stops after 400 emissions. Elsewhere, whitelist filters,
  change-only logging and bounded caps do the same thing by design.
- **Lesson** — Know your instrument's blind spots before concluding "it did not happen". Pure-framework
  NREs and anything after the cap are invisible; a quiet log is not proof nothing threw. Every cap —
  frames, argument text, total emissions, throttle keys, upload size, intervals — is what makes it safe
  to ship a tracer to a player at all, and each one must therefore be documented as a blind spot.
- **Now** — `Payload/CharacterCreationTrace.cs:163-176,217-246`; the caps at
  `Payload/TracePatches.cs:228-231,279`, `Payload/ControlTrace.cs:381`,
  `Payload/RuntimeDiagnostics.cs:29,185`, `Payload/TraceThrottle.cs:31-32`,
  `Payload/LogStreamer.cs:101,132`.

### P8 · Not printing what you actually managed to instrument

- **What happened** — With by-name reflection everywhere, a silent hook miss is indistinguishable from
  "the bug did not happen".
- **Lesson** — The **load line is the oracle**. Every component prints exactly what it resolved —
  "tracer active on N method(s)", "type not found: X", "no patchable method X" — and a load-bearing fix
  prints which of two competing hypotheses the run confirmed. Where targets were recovered from runtime
  stack traces rather than IL, the patched **count** is the only drift detector you have, so log it and
  treat zero as drift, never as absence of the problem. This is the single most transferable habit in
  the codebase.
- **Now** — `Payload/TracePatches.cs:46,69,74`; `Payload/ControlTrace.cs:45,56,79`;
  `Payload/CoopBattleTrace.cs:46,63,84`; `Payload/CharacterCreationTrace.cs:47-48`;
  `Payload/MovementOrderTypeInitGuard.cs:64-71,108-111`; `Payload/TimeEnforcementGuard.cs:22-25,133-137`.

### P9 · Leaving the most central component unpinned

- **What happened** — House convention is that every guard reports via `Diag.Report` and registers a
  `SelfHealing.RegisterTest`. `BattleMode.cs` and `PayloadEntry.cs` — the two most central files — do
  neither, and they are not alone: `TimeFlowPatch`, `PartyAiCrashGuard`, `EncounterLoopGuard`,
  `MapClickSpeedKeeper`, `ClientHeroCreationGuard`, `TimeEnforcementGuard`, `ShareTimeControl`,
  `MovementOrderTypeInitGuard`, `PlayerIdentityGuard`, `BootstrapWatch` and `DeploymentCrashGuards`
  report no health entry and pin no self-test either — thirteen of the thirty non-diagnostic files in
  `Payload/`. Today a renamed `BattleTargets` type is a silent `continue`.
  The ten diagnostics files also register nothing, deliberately.
- **Lesson** — The component everyone depends on is the easiest one to leave unpinned. Where the
  convention is deliberately broken, say what replaces it: for the tracers, the **load line is** the
  health report, and only the load-bearing fix (`MovementOrderTypeInitGuard`) prints an outcome that
  distinguishes "fixed" from "too late".
- **Now** — absence of `Diag.Report`/`SelfHealing.RegisterTest` in `Payload/BattleMode.cs` and
  `Payload/PayloadEntry.cs` (the entry point only *consumes* the two subsystems, at
  `Payload/PayloadEntry.cs:102,105`), against `Payload/ClanModeSoloFix.cs:54-55` and
  `Payload/ClientBootstrapFix.cs:85-86`; the silent `continue` at `Payload/BattleMode.cs:231-234`.

### P10 · Assuming tests would have caught it

- **What happened** — The two worst bugs in this repo's history were caught by a **commit review**,
  not by a test and not in play: replacing `ClanPartiesVM`'s candidate iterator would have crashed the
  leader popup (**H6**), and the naive full-roster stash apply would have silently wiped a
  player-crafted item (**S1**). A third — the `WeaponDesign` predicate — was caught on a second review
  pass. In the stash case a test was actively **pinning** the bug by asserting that negative counts
  round-trip.
- **Lesson** — Data-destructive and UI-substituting paths need an adversarial read by someone who is
  not the author, before they ship. And when a review finds a bug, check whether a test is certifying
  it.
- **Now** — CHANGELOG.md:101-103,204-209; `.git/commit-review-cache.json:248,254,285` (machine-local,
  not in a clone — see **H6**); `tests/StashPayloadTest/Program.cs:37-53`.

### P11 · Shipping a trade-off without naming its cost

- **What happened** — `CoopCommandSplit` gives each player four formations while a remote player is in
  the battle, because the eight-formation budget has to cover two parties: the finer troop preferences
  (`Skirmisher`, `HeavyInfantry`, `LightCavalry`, `HeavyCavalry`) fold into infantry / archers /
  cavalry / horse archers.
- **Lesson** — State the capability cost in the changelog and the README **at the moment you ship it**,
  and pin the folding rule in a self-test so it cannot drift silently. An accepted limit that is
  written down is a decision; one that is not is a bug report waiting to happen.
- **Now** — `Payload/CoopCommandSplit.cs:151-168,430-436`; CHANGELOG.md:47-49.

### P12 · Retrying a dead end because nobody wrote it down

- **What happened** — Scoping the solo time neutralizer to the campaign map (a 2026-09-04 hypothesis
  for the sideways/folded character) did not move the symptom at all; the sideways character is a
  separate, likely GPU-side vanilla issue. It was reverted.
- **Lesson** — A speculative narrowing that does not move the symptom must be reverted **and recorded**
  as a dead end with its date, or the next session pays for it again. That is what §0 of this file is
  for.
- **Now** — `docs/ENGINE-NOTES.md`, the time-control dead end; §0 above.

### P13 · Reading coalesced output as an exact timeline

- **What happened** — Coalesced tracer output is not strictly ordered against plain `Log.Info` lines: a
  run's tail count flushes on its next repeat or window, not instantly, and a repeat that never recurs
  never gets a final count. `TraceThrottle` also clears its **entire** key dictionary when cardinality
  exceeds 512, discarding in-flight counts.
- **Lesson** — Accept the tradeoff explicitly — it is what stops the flood — and read `[repeat]`
  rollups as approximate frequency, never as an interleaved timeline. Build throttle keys from **stable
  identity** (exception type plus throwing frame), never from timestamps or values: an unstable key both
  defeats coalescing and periodically wipes the counters.
- **Now** — `Payload/TraceThrottle.cs:31-32,34-37,54-57,60-83`.

### P14 · Log evidence with no build identity

- **What happened** — A log or a crash report that cannot say which build and which launch produced it
  is not evidence.
- **Lesson** — Print a per-launch banner: the version read **back** from the assembly identity (never
  hardcoded), the build time from the DLL's last-write time, and a session id from
  `TickCount ^ (pid << 8)` as hex. It also catches "you deployed to the wrong folder" instantly. In
  co-op, add a per-line role tag (H/C/S) so two machines' logs can be merged — with the honest caveat
  that the tag starts as `?` and `S` doubles as "unreadable".
- **Now** — `Harness/Diag.cs:15-61`, logged first thing at `Harness/SubModule.cs:19`; role tagging at
  `Payload/PayloadEntry.cs:161-187` and `Harness/Log.cs:19,69`.

### P15 · Chasing a crash where it was logged

- **What happened** — Covered in full at **N2**; it belongs here as a habit, not just as a CLR fact.
  The `MovementOrder` crash was chased at `Formation.ResetAux` inside `Mission.AfterStart`, where
  `Mission.Current` is already live. Only collateral was ever captured; the origin never was.
- **Lesson** — The manifested location is not the root-cause location — find **both**. Ask what could
  make the logged context differ from the origin's (a cached type-init re-throw, a queued apply, a
  deferred tick), and instrument the earlier moment directly rather than reading the stack harder.
- **Now** — `Payload/MovementOrderInitProbe.cs:7-23`; `docs/DIAGNOSTICS.md`; `CLAUDE.md`, "How to
  investigate".

### P16 · Reading the exception's own stack as "who triggered it"

- **What happened** — ButterLib wrote **no** crash report for the 2026-09-04 battle-load crash, and
  the crash logger that did exist printed only the **outer** exception. Separately, an exception's own
  stack is truncated to the throw point, and for a cached type-init re-throw it belongs to a different
  moment entirely (**N2**).
- **Lesson** — Three habits, all cheap. Capture the **live** stack —
  `new StackTrace(skip, false)` at the instant of the throw — alongside the exception's own, because
  the live one is what names who triggered it. Walk the **full** `InnerException` chain (bounded,
  depth 8), printing each type, message and trimmed frames: a `TypeInitializationException`'s real
  cause is always its inner, and printing only the outer is what made that crash undiagnosable for
  days. And keep Harmony frames when filtering: a patched caller appears as a dynamic method with a
  **null `DeclaringType`**, named `DMD<Namespace.Type::Method>` — a naive filter drops exactly the
  frame that identifies the interfering mod.
- **Now** — `Payload/RuntimeDiagnostics.cs:159-196`; `Payload/CharacterCreationTrace.cs:185,198-215`;
  `Payload/MovementOrderInitProbe.cs:66,86`; the DMD decoding at `Payload/TracePatches.cs:271-278`,
  `Payload/ControlTrace.cs:377-380` and `Payload/TimeTrace.cs:166-212`.

### P17 · A self-test that proves the wiring but not the decision

- **What happened** — A test that only asserts "my target still resolves" misses the case where the
  **selection logic** silently widened or narrowed, and a test that reuses the `MethodInfo` cached at
  `Apply` passes forever on a game version that no longer exists.
- **Lesson** — Pin four things, not one. **Re-resolve** members by name inside the test rather than
  reusing the cached handle. Pin the **negative half**: `MapIncidentCrashGuard` requires ≥1 lambda
  matched *and* ≥1 lambda deliberately **not** matched, so both over- and under-selection fail. Pin
  the **decision** as a pure function with its full truth table — `Decide(bool,bool,bool,bool)` and
  `ComputeBlockMs(e)` are verifiable offline with no game state, which is how the logic that could
  destroy a peer's in-flight join is proven at startup, including all of its "never act" rows. And
  prove **invocability**, not just non-null handles: invoking a pure read is the cheapest live proof
  of the whole reflection path, and reporting `targets` / `queryReads` / `logic` separately turns a red
  self-test straight into a diagnosis. Where a numeric layout matters, pin the number itself
  (`(int)OrderType.AIControlOn == 36`); where no campaign is loaded, a degenerate input
  (`default(TroopRosterElement)`, null arguments) is the only testable one and it pins the fail-open
  contract.
- **Now** — `Payload/MapIncidentCrashGuard.cs:309-337`; `Payload/BackgroundTickBudgetGuard.cs:143-156`;
  `Payload/JoinSyncPauseEscape.cs:269-278,339-364`; `Payload/SiegeCommandGuard.cs:523-553`;
  `Payload/CoopCommandSplit.cs:416-444`; `Payload/DeadHeroReactivationFix.cs:157-179,163`;
  `Payload/IllnessDeathGuard.cs:136-148`; `Payload/ClanScreenCrashGuard.cs:68-81`;
  `Payload/CoopHeroIdentityLock.cs:316-327`.

> **Good to know — the one-key kill switch, and why it earns its place.**
> `safeMode` returns from `PayloadEntry.Apply` **before any patch is installed**, so a player — or a
> bisecting developer — can prove this mod is or is not the cause by editing one JSON key instead of
> deleting the module. Any mod that patches aggressively should ship one. The companion habit is
> dual-channel reporting: a technical line to the log plus one plain-language on-screen sentence for
> anything the player can perceive ("your sickness was cured", "marriage barter cancelled BEFORE any
> gold moved"), because a silent intervention is indistinguishable from a bug — and, in the other
> direction, screen messages must be gated on a real transition, or a mode that re-decides at several
> chokepoints spams the player.
> `Payload/PayloadEntry.cs:31-36`; `Payload/IllnessDeathGuard.cs:125-126`;
> `Payload/MarriageBarterGuard.cs:89-90`; `Payload/DeadHeroReactivationFix.cs:81-82`;
> `Payload/BattleMode.cs:168-176,216-224`; `Harness/Log.cs:122-127`.

> **Good to know — what makes a fix retirable.**
> Every guard reports at `Apply` through `Diag.Report(component, ok, detail)` and records its first
> intervention through `SelfHealing.RecordFire`, so the health report distinguishes **active** from
> **inactive** from **never fired**. A crash finalizer that has never fired is a retirement signal, not
> dead weight; a behaviour patch that never re-checks the bug is the opposite — it will fight an
> upstream fix forever, which is why `ClientBootstrapFix` asks "is the bug still present?" before
> overriding (**B4**). Reporting "no BT present" as ok=true while an unresolved member is **critical**
> is exactly the distinction a non-technical player's health line has to make, and reporting
> "disabled by config" as ok=true keeps a deliberate off-switch from looking like a breakage.
> `Harness/SelfHealing.cs:9-21,43-81`; `Harness/Diag.cs:71-104`;
> `Payload/ClientBootstrapFix.cs:65,71,78,85`; `Payload/IllnessDeathGuard.cs:38-52`.
