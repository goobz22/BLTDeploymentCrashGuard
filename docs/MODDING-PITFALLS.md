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
| Scope the solo time neutralizer to the campaign map (2026-09-04 hypothesis for the sideways/folded character) | Did not move the symptom at all; the sideways character is a separate, likely GPU-side vanilla issue | Reverted. `docs/ENGINE-NOTES.md:55-57` records it as a dead end |
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
| Check the log size once per launch | The check ran while the file was still small; `CrashGuard.log` reached **283 MB** | Amortised re-check every N writes (`Harness/Log.cs:84-93`) |

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
  is NEVER replaced"); `.git/commit-review-cache.json:285` holds the BLOCK verdict; CHANGELOG.md:101-103.

### H7 · `AccessTools.Field` returns null for an auto-property

- **What happened** — The backing field is `<Name>k__BackingField`, so a field-only read of a view
  model member returned null and every card read `IsDisabled == false` — a silent false negative that
  also killed the "no clan member can lead a party" hint. No exception anywhere.
- **Lesson** — Read foreign members field-first with a **property fallback**, and make the self-test
  assert that each member resolves as *either* shape, so an upstream field→property refactor becomes
  loud instead of degrading every read to a default.
- **Now** — `Payload/ClanPartyCreationAdvisor.cs:157-167,332-336`; `.git/commit-review-cache.json:285`.

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
- **Now** — `Payload/PayloadEntry.cs:47-70`; `Payload/IllnessDeathGuard.cs:36-63`;
  `Payload/ClanPartyCreationAdvisor.cs:61-98`.

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
  `Payload/PayloadEntry.cs:36-46`; full IL proof in `docs/ENGINE-NOTES.md:9-35`.

### N2 · A logged type-init throw can be a **cached re-throw** from a different moment

- **What happened** — The `MovementOrder` crash was chased where it was *logged*
  (`Formation.ResetAux` inside `Mission.AfterStart`, where `Mission.Current` is already live). .NET
  runs a type initializer once, caches the failure, and re-throws the **original** exception with the
  original stack on every later access. Only collateral was ever captured; the origin never was.
- **Lesson** — To find the origin, patch the **instance constructor the static ctor calls** — its
  first-ever call happens inside the static ctor. This inverts the usual "read the stack" instinct,
  and it explains how `Mission.Current` can be live in the logged throw of a null-at-init crash.
- **Now** — `Payload/MovementOrderInitProbe.cs:7-23`; `Payload/MovementOrderTypeInitGuard.cs:21-24`;
  `docs/DIAGNOSTICS.md:80-85`; mission load order proven at `docs/ENGINE-NOTES.md:37-44`.

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
  `<AssemblyVersion>`.
- **Now** — `tests/StashPayloadTest/StashPayloadTest.csproj`.

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
