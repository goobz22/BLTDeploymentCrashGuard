# Modding Mount & Blade II: Bannerlord and BannerlordTogether — techniques learned building BLT Deployment Crash Guard

BLT Deployment Crash Guard is a companion mod for **Mount & Blade II: Bannerlord** that fixes crashes
and co-op bugs in the **BannerlordTogether (BT)** multiplayer mod, using Harmony patches and by-name
reflection into the game and into BT (`CLAUDE.md:8-10`). Almost nothing it does is specific to those
two bugs: the constraints — no source for the target, no test harness, a second mod patching the same
methods, a player who cannot attach a debugger — are the normal constraints of Bannerlord modding.

This guide is the reusable half. Every technique below is in production in this repository, and every
claim carries a repo-relative `file:line` so you can read the real thing instead of trusting prose.

---

## 0. How to use this guide

Each technique is written as: **what it is**, **why it matters**, **where it is used here**, and a
minimal code shape where a shape helps. Citations are the authority — this document summarises code
that changes; the code does not summarise this document. When the two disagree, the code is right.

### Companion documents

| Document | What it holds | Read it when |
|---|---|---|
| `docs/MODDING-PITFALLS.md` | What bit us: reverted attempts, silent failure modes, gotchas | Before trying something that "should obviously work" |
| `docs/ENGINE-NOTES.md` | Bannerlord engine facts proven from IL, with evidence | Before diagnosing anything about the engine's own behaviour |
| `docs/BT-INTERNALS.md` | BannerlordTogether internals as observed from IL (unofficial) | Before reflecting into or patching BT |
| `docs/DIAGNOSTICS.md` | How to investigate without guessing | At the start of any bug hunt |
| `docs/FIX-REFERENCE.md` | Per-fix developer table: class, tag, config key, patched members, limits, self-test | When you need to know what a specific guard does |
| `HOTRELOAD.md` | The dev hot-reload workflow, end to end | When setting up the edit loop |
| `tools/il-probes/README.md` | The IL/reflection probe tools and a worked example | When you need to read installed assemblies |
| `README.md` / `CHANGELOG.md` | Player-facing behaviour / the chronology of every fix | For context on why a technique exists |

This guide is about **technique**. `docs/MODDING-PITFALLS.md` is its companion in the other direction:
this file says "do it this way", that file says "here is what happened when we did not". A technique
that exists only because of a specific failure names that failure here and gives the full story there.

### The rule that governs all of it

**A crash guard that crashes the game is worse than no mod.** Every pattern in this guide is downstream
of that: fail open, resolve by name, bound every loop, tri-state every uncertain read, and make the
worst case "the fix did nothing" rather than "the fix broke the game". The second rule follows from the
first: **a fix that silently stops working is indistinguishable from a fix that was never needed**, so
everything that can go inert reports its own health (§5) and says so in the log (§6).

---

## 1. Toolchain

### 1.1 Framework, SDK, and where the DLLs actually are

Bannerlord modules are .NET Framework assemblies. Both projects here target `net472` with the reference
assemblies package rather than an installed targeting pack
(`Harness/BLTDeploymentCrashGuard.csproj:6,22`), and the manifest declares `v1.4.8` game dependencies
(`SubModule.xml:10-12`). Build with the .NET SDK (`dotnet build -c Release`); nothing here needs Visual
Studio.

Reference paths that catch people out:

- **Engine assemblies**: `<Game>/bin/Win64_Shipping_Client/TaleWorlds.*.dll` (plus `SandBox.*`,
  `StoryMode.*`).
- **Do not reference the game bin's Harmony.** The process holds *two* copies: the game bin ships
  `0Harmony 2.4.2.0`, and the `Bannerlord.Harmony` module ships `0Harmony 2.3.6.0`
  (`CHANGELOG.md:215-220`; `Harness/HotReload.cs:146-151`). Mods bind the **module's** copy:
  `<Game>/Modules/Bannerlord.Harmony/bin/Win64_Shipping_Client/0Harmony.dll`
  (`Harness/BLTDeploymentCrashGuard.csproj:31`). That two-copy split is exactly the type-identity
  hazard §4.4 and §10.2 are about.
- **SandBox *view* code is also in a module**:
  `<Game>/Modules/SandBox/bin/Win64_Shipping_Client/SandBox.View.dll` (`tools/il-probes/README.md:30-31`).
- **BannerlordTogether**: `<Game>/Modules/BannerlordTogether/bin/Win64_Shipping_Client/BannerlordTogether.dll`
  — and this repo never references it at compile time (§3).

Keep the game path in one overridable MSBuild property so a contributor with a different install can
build without editing the project:

```xml
<GameDir>C:\Program Files (x86)\Steam\steamapps\common\Mount &amp; Blade II Bannerlord</GameDir>
<GameBin>$(GameDir)\bin\Win64_Shipping_Client</GameBin>
```

(`Harness/BLTDeploymentCrashGuard.csproj:13-14`, mirrored at
`Payload/BLTDeploymentCrashGuard.Payload.csproj:37-38`.) Override it per build with
`-p:GameDir="…"`. This mod's harness binds only `TaleWorlds.Library` / `Core` / `Engine` /
`MountAndBlade`; the payload adds `TaleWorlds.DotNet`, `Localization`, `ObjectSystem` and
`CampaignSystem` (`Harness/…csproj:30-49`, `Payload/…csproj:54-89`).

**Never let a reference copy into your module's bin.** Every `<Reference>` to a TaleWorlds assembly or
to `0Harmony` carries `<Private>false</Private>`, and so does the payload's `<ProjectReference>` to the
harness (`Harness/…csproj:32,36,40,44,48`; `Payload/…csproj:45-51,56,60,64,68,72,76,80,84,88`).
Copying an engine or Harmony DLL into `Modules/<Mod>/bin/Win64_Shipping_Client/` makes the process load
a *second* copy of those types and nothing matches; copying your own harness would give you a second set
of statics.

Two smaller conventions that keep the deploy commands short: `<DebugType>none</DebugType>` (no PDB) and
`<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>`, so output lands directly
in `bin/Release` (`Harness/…csproj:11-12`, `Payload/…csproj:26-27`).

**Pin your NuGet feeds.** Machine- and user-level `NuGet.config` files are *additive*; an inherited
private or dead corporate feed changes what a contributor's build resolves and looks like a code
problem. A repo-root `NuGet.config` with `<clear />` before the single `nuget.org` entry makes restore
reproducible on any box (`NuGet.config:3-6`). There is no lock file here — versions are pinned by the
`PackageReference` declarations themselves (`Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 and, for
the Roslyn build, `Microsoft.CodeAnalysis.CSharp` 4.8.0).

### 1.2 The module manifest

```xml
<SubModule>
  <Name value="BLTDeploymentCrashGuard" />
  <DLLName value="BLTDeploymentCrashGuard.dll" />
  <SubModuleClassType value="BLTDeploymentCrashGuard.SubModule" />
  <Assemblies />
</SubModule>
```

Facts worth knowing (`SubModule.xml:5-7,9-14,15-22`):

- `<DependedModule Id="X" Optional="true" />` makes your module load *without* X, but it does **not**
  guarantee load order after X. Order is the launcher list order — which is why this mod's installer
  tells the player to tick it **after** BannerlordTogether by hand (`install.cmd:70-71`), and why every
  BT-facing guard still needs the late-load retry in §2.14.
- A BT co-op companion declares `SingleplayerModule=true` / `MultiplayerModule=false` (BT co-op runs off
  the singleplayer list) and `IsTWCompatible=false` (an unsigned Harmony mod).
- The launcher loads only `<DLLName>` and instantiates `<SubModuleClassType>`. With `<Assemblies />`
  empty, a **second** assembly is invisible to the launcher and must be loaded by the module itself at
  runtime — which is exactly what the hot-reload harness does (§4).

### 1.3 Module entry points and layout

`MBSubModuleBase` is the entry point. This mod overrides five members, and the access modifiers differ
in the base class — a detail that costs an hour if you guess (`Harness/SubModule.cs:16,24,33,42,51`):

| Override | Modifier | Used here for |
|---|---|---|
| `OnSubModuleLoad()` | `protected` | Banner line, start the reload engine |
| `OnBeforeInitialModuleScreenSetAsRoot()` | `protected` | Retry guards whose dependency loaded late |
| `OnGameStart(Game, IGameStarter)` | `protected` | Retry + campaign-time wiring |
| `OnMissionBehaviorInitialize(Mission)` | **`public`** | Per-mission state reset |
| `OnApplicationTick(float)` | `protected` | Per-frame pump (self-throttled) |

A module's DLL lives at `<moduleRoot>/bin/Win64_Shipping_Client/`, so **the module root is reliably the
assembly directory plus `../..`**. This repo derives the log path, `guardconfig.json` and the
`.hotreload-dev` marker that way (`Harness/Log.cs:49-51`, `Harness/GuardConfig.cs:21-22`,
`Harness/HotReload.cs:65-66`); a further level up reaches the `Modules` directory and therefore sibling
mods' folders (`Payload/BootstrapWatch.cs:105-107,134-138`). No hardcoded Steam path, works for any
install location, and per-machine state lands beside the config the player already edits.

On-screen player messaging is `InformationManager.DisplayMessage(new InformationMessage(text, color))`
with `TaleWorlds.Library.Color`'s float RGB constructor. It **can throw** if called too early in
startup — wrap it (`Harness/Log.cs:122-131`).

### 1.4 Harmony

Harmony is the patch library (`0Harmony.dll` from the `Bannerlord.Harmony` module). The four hook kinds
and when each is right are §2. Two facts to carry from the start:

- Harmony **keys patches by owner string**, which is what makes a whole patch set removable later
  (`new Harmony(id).UnpatchAll(id)`) — see §2.12.
- Harmony runs **all** prefixes even when one of them returns `false`
  (`Payload/TimeTrace.cs:17-19`). That is both a hazard (your prefix runs even when the call is already
  vetoed) and a tool (§6.8).

### 1.5 The ButterLib / Roslyn caveat, and optional features in a csproj

This mod can compile the payload from source in-process (mode B of the hot-reload loop), but that
capability is **compile-time optional**, because Roslyn on .NET Framework inside Bannerlord can
bind-conflict with ButterLib's older `System.Collections.Immutable` / `System.Reflection.Metadata`
(`HOTRELOAD.md:46-48`). The shipped binary therefore contains no compiler at all:

```xml
<PropertyGroup Condition="'$(Roslyn)' == 'true'">
  <DefineConstants>$(DefineConstants);ROSLYN</DefineConstants>
</PropertyGroup>
<ItemGroup Condition="'$(Roslyn)' == 'true'">
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" />
</ItemGroup>
```

(`Harness/BLTDeploymentCrashGuard.csproj:17-19,25-27`.) Built with `-p:Roslyn=true` you get the
in-process compiler; the default release build has neither the code nor the dependency. The
MSBuild-property → `DefineConstants` → conditional-`PackageReference` triple is the idiomatic way to
make any feature optional in a mod csproj, and it matters most for dependencies that could bind-conflict
inside the game process. If the runtime compile fails, the engine logs it and falls back to the prebuilt
DLL, so the fragile path can never wedge the dev loop (`HOTRELOAD.md:47-48`).

### 1.6 The IL probes: reading the installed assemblies without a decompiler

`tools/il-probes/` holds five self-contained `net472` console executables that load a target DLL by path
and resolve dependencies out of the game's `bin` and module folders (`tools/il-probes/README.md:1-33`).
They read the **installed** assemblies — the exact bytes the player is running — so a version mismatch
between your reference assemblies and the deployed game cannot fool you. No decompiler licence, no GUI,
and they are scriptable from an agent loop.

| Tool | Purpose |
|---|---|
| `NameSearch` | Find every type/method/field whose name contains a term — the first step when you do not know exact names |
| `Inspect` | Dump one type's methods (with signatures), fields, properties; enum members **with values** |
| `IlDump` | Disassemble a method to IL; supports `.cctor` and `.ctor`. This is what proves control flow and null-deref sites |
| `Callers` | Find methods that call a given member (substring match on the callee) |
| `VerCheck` | Print an assembly's version identity |

**The `.cctor`-then-`.ctor` pattern for type-initializer crashes.** When a `TypeInitializationException`
points at a type, dump the static constructor to see what it constructs, then dump the instance
constructor it calls and look for the single line that can dereference null; confirm with a reflection
check of the type's attributes (`beforefieldinit`, value type). Verbatim from the README, two IL dumps
plus that reflection check produced the whole root cause of the 2026-09-04 `MovementOrder` crash — "No
decompiler, no guessing" (`tools/il-probes/README.md:34-44`):

```
IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.cctor"
IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.ctor"
```

The fix that came out of it is §2.11.

---

## 2. Harmony patterns

### 2.1 The four hooks, and when each is right

| Hook | Signature shape | Use it when |
|---|---|---|
| **Prefix** | `static bool Prefix(...)` — `true` runs the original, `false` skips it | You must stop, redirect, or **correct the inputs of** a call |
| **Postfix** | `static void Postfix(... __result)` | You need to observe or extend a completed call |
| **Finalizer** | `static Exception Finalizer(Exception __exception, ...)` | You must observe or **suppress an escaping exception**, or run something that must happen even on the throw path |
| **Transpiler** | `static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction>)` | Nothing else can express the change — a single call site, or a value-typed result no postfix can rewrite |

Order of preference in this repo: correct the inputs (prefix with `ref` args) > observe (postfix) >
suppress the exception (finalizer) > rewrite the IL (transpiler). Each step down costs more coupling to
the exact shape of the target.

### 2.2 A finalizer that turns a CTD into a log line

**What.** In Bannerlord an exception escaping a mission or campaign tick unwinds into native engine code
where there is no managed catch — it is a guaranteed crash to desktop. A Harmony finalizer is the only
hook that can intercept that without rewriting the method: returning `null` suppresses the escaping
exception.

```csharp
[HarmonyPatch(typeof(DeploymentMissionController), "SetupTeams")]
internal static class SetupTeamsCrashGuardPatch
{
    private static Exception Finalizer(Exception __exception)
    {
        if (__exception == null) return null;          // inert on the success path
        SelfHealing.RecordFire("setup-teams-guard");
        Log.Info("SUPPRESSED crash in DeploymentMissionController.SetupTeams: " + __exception);
        Log.Screen("prevented a deployment-setup crash (details in … CrashGuard.log)");
        return null;                                   // returning null swallows it
    }
}
```

(`Payload/DeploymentCrashGuards.cs:13-26`.) Three rules, learned the hard way:

1. **Always short-circuit on `__exception == null`.** The finalizer runs on every call; the guard must
   be completely inert — and record no fire — while nothing is throwing. Every finalizer in this repo
   opens that way (`Payload/DeploymentCrashGuards.cs:16-26,37-80`;
   `Payload/PartyAiCrashGuard.cs:101-123,131-147`; `Payload/MapIncidentCrashGuard.cs:279-292,294-307`;
   `Payload/ClanScreenCrashGuard.cs:46-66`; `Payload/ConversationCameraCrashGuard.cs:57-66`).
2. **Swallowing is not enough when the method has side effects.** If the crashed method left the world
   half-configured, replay its remaining tail — each step in its own `try/catch` so one failing step
   cannot abort the rest. The `FinishDeployment` guard replays agent handover, `AllowAiTicking`,
   `DisableDying`, fall-avoid, `OnAfterDeploymentFinished`, the non-public `AfterDeploymentFinished`
   resolved by name, and `RemoveMissionBehavior` (`Payload/DeploymentCrashGuards.cs:45-77`). Without it
   you trade a crash for a freeze: AI ticking stays off and the player agent stays non-detachable.
3. **A method that returns through `ref`/`out` parameters must be *answered*, not just silenced.** A
   finalizer may take those parameters by ref and write a domain-neutral value — the party-AI guard
   forces `AiBehavior.Hold` at the party's own position
   (`Payload/PartyAiCrashGuard.cs:101-114`) — and must normalise `__result` too (a non-null empty
   `List<TextObject>`, `Payload/MapIncidentCrashGuard.cs:294-307`). Swallowing an exception from a
   method with out-params otherwise leaves the caller reading uninitialised values.

**Substitute the method's own edge-case value, not an invented one.** `ClientHeroCreationGuard` sets
`ref TResult __result` to the value the method already returns in *its* edge cases before returning
`null` (`Payload/ClientHeroCreationGuard.cs:38,47-76`). Inventing a state the game has never seen is
worse than the crash.

**Finalizers that recover.** Where swallowing alone would leave the UI unusable, add a recovery step in
its own `try/catch`: the clan-screen guard reflectively invokes
`TaleWorlds.ScreenSystem.ScreenManager.PopScreen()` (a static, parameterless method — `Invoke(null,
null)`) so the player lands back on the map instead of staring at a half-built screen
(`Payload/ClanScreenCrashGuard.cs:55-65`).

**Finalizers that observe and never swallow.** `return __exception;` unchanged logs the throw with live
context and lets it propagate exactly as before — a breakpoint you can ship
(`Payload/CharacterCreationTrace.cs:116-123`, `Payload/MovementOrderInitProbe.cs:73-93`).

**A finalizer is self-retiring.** It does literally nothing while the bug is absent, so "never fired" in
the health report is the signal that it can be removed once upstream fixes the cause
(`Payload/ClanScreenCrashGuard.cs:14-18`; §5.4).

**But "the crash is gone" is not "the bug is fixed."** The deployment finalizer removed the CTD and left
battles unplayable with an empty player side (`UPSTREAM_BUG_REPORT.md:104-108`). Verify the **gameplay
outcome**, not just the absence of an exception.

### 2.3 Prefixes: the return protocol, `ref` arguments, and failing open

A `bool` prefix returns `true` to run the original and `false` to skip it (having set `ref __result`
where the method returns a value). Wrap the whole body so that **any** exception returns `true`
(`Payload/ClientBootstrapFix.cs:147-191`): the worst case becomes "the fix did nothing", never "the fix
broke the host method".

**Mutate `ref` arguments instead of skipping the method.** `SetControlledByAIPrefix` takes
`ref bool isControlledByAI` and flips it to `false`; `SetPlayerRolePrefix` rewrites
`ref bool isPlayerGeneral, ref bool isPlayerSergeant` (`Payload/SiegeCommandGuard.cs:280-307,337-363`).
Vanilla still executes, with corrected inputs, so all of its downstream bookkeeping — events, UI
refresh, order-controller state — still runs.

**Return `false` only when the outcome must simply not happen.** `TransferUnitsPrefix` cancels
`Formation.TransferUnits` outright when either side is a formation the player commands
(`Payload/SiegeCommandGuard.cs:309-335`) — and its `catch` returns `true`, so a bug in the guard fails
open into vanilla behaviour.

**Prefix-cancel an entire transaction when a downstream gate will reject it.** Rather than trying to
un-apply gold after BT rejects a marriage, the guard cancels the whole
`ApplyAndFinalizePlayerBarter` before *any* barterable applies, restoring the atomicity a foreign patch
broke (`Payload/MarriageBarterGuard.cs:17-21,54-91`). This is the reusable shape for any "mod A
suppresses one leg of a transaction, the other legs still commit" state-loss bug.

**Fail-safe predicates.** `roster.RemoveIf(predicate)` deletes rows. If the predicate's own reflection
throws, returning `true` would delete a living troop — so the `catch` returns `false`, "never remove on
uncertainty" (`Payload/DeadHeroReactivationFix.cs:91-104`), and the self-test pins exactly that with
`default(TroopRosterElement)` (:163). For any destructive predicate, the exception path must be the
conservative one.

### 2.4 Normalise the inputs; do not skip the method

A prefix returning `false` suppresses the original **and every event it would have raised**. Sometimes
that is the point: blocking `dead → Active` in `Hero.ChangeState` means no `OnHeroActivatedEvent`, which
is the NRE source (`Payload/DeadHeroReactivationFix.cs:139`). Often it is a hidden regression: skipping
`AgingCampaignBehavior.DailyTickHero` while a hero is ill — the standalone *NoSickness* mod's approach —
also drops the aging and come-of-age events that method owns, and leaves the ill flag permanently stuck.

Prefer repairing the state and then returning `true`: ill days back to `-1`, the pending death mark
cleared, vanilla runs normally on healthy inputs (`Payload/IllnessDeathGuard.cs:19-23,116-127`).

The same instinct one level up: prefer blocking the **root** event to guarding each downstream symptom.
The illness guard blocks the *roll* (`IsItTimeOfDeath`) so the illness is never caught in the first
place, removing the whole downstream state machine — ill days, HP drain, death mark, extra-life
consumption — instead of chasing each stage (`Payload/IllnessDeathGuard.cs:17-19,79-100`). And when a
caller bug has a class-level invariant behind it, fix both: strip dead heroes from the returning roster
*and* prefix `Hero.ChangeState` so `dead → Active` is impossible for every caller, choosing the
invariant so a legitimate revive (which clears `IsDead` first) is never blocked
(`Payload/DeadHeroReactivationFix.cs:21-31,43-67,108-147`).

### 2.5 Read-only postfixes over a generic result

Harmony's patch-time check is `paramType.IsAssignableFrom(returnType)`, so declaring
`void Postfix(IEnumerable __result)` against a method returning `IEnumerable<T>` **installs cleanly** —
but Harmony emits `Ldloca` on a result slot typed with the *real* return type and writes back with a raw
`stind.ref` and **no cast**. Assign a non-generic collection (an `ArrayList`, say) and the caller's
`foreach (T x in …)` interface-dispatches on it and throws: a crash guard introducing a crash in the
exact path it instruments.

Enumerate for logging and never replace the value. It is also unnecessary: a C# `yield` iterator returns
a fresh enumerator per `GetEnumerator()` with no side effects
(`Payload/ClanPartyCreationAdvisor.cs:119-155`). Commit review caught the unsafe draft before it shipped
(`CHANGELOG.md:101-103`).

### 2.6 `[ThreadStatic]` depth counters: telling explicit calls from implicit ones

**What.** A prefix increments a `[ThreadStatic] int`; a **finalizer** decrements it (guarded against
going negative); the main guard stands down while the counter is `> 0`.

```csharp
[ThreadStatic] private static int _explicitAiDepth;

private static void SetOrderPrefix(OrderType orderType)
{
    if (orderType == OrderType.AIControlOn) _explicitAiDepth++;
}

private static Exception SetOrderFinalizer(OrderType orderType, Exception __exception)
{
    if (orderType == OrderType.AIControlOn && _explicitAiDepth > 0) _explicitAiDepth--;
    return __exception;   // never swallow
}
```

(`Payload/SiegeCommandGuard.cs:61-66,453-496`, consumed at :275 and :345.)

**Why it matters.** It lets one blanket patch on a low-level API (`Formation.SetControlledByAI`) refuse
the AI's *implicit* hand-offs while still allowing the three call paths that are supposed to hand off —
the player's F6 (`OrderController.SetOrder(AIControlOn)`), `Team.DelegateCommandToAI` death hand-off, and
BT's host player-down release — without enumerating callers or walking stack traces. Three separate
counters, one per path.

**The decrement must be a finalizer, not a postfix.** A postfix does not run on the exception path; a
leaked depth counter silently disables the guard for the rest of the mission
(`Payload/SiegeCommandGuard.cs:461-468,475-482,489-496`). Zero all counters in the per-mission reset
(:157-166).

The same shape as a **scope flag** rather than a counter: set a `[ThreadStatic] bool` in the prefix,
clear it in the finalizer, and have downstream setter prefixes distinguish writes that came from that
method from every other writer (`Payload/TimeEnforcementGuard.cs:35-36,168,180-184`;
`Payload/MapClickSpeedKeeper.cs:25-26,68-77`). `[ThreadStatic]` keeps unrelated threads unaffected —
and, by the same token, an asynchronous write from another thread is *not* covered.

Where the flag guards a re-entrancy hazard rather than a scope, a plain static can be enough — but only
with the constraint written down. `PregnancySyncGuard` uses a plain static around the reconstruct call
because reconstruction is confined to one thread; a `[ThreadStatic]` would be required otherwise
(`Payload/PregnancySync/PregnancySyncGuard.cs:36,247-250,369-390`).

### 2.7 Run but neutralize; veto a transition, not a setter

**Do not prefix-return-`false` a third-party method whose job includes state-machine bookkeeping.** Let
it run in full and block only its unwanted side-effect *writes*, by prefixing the setters it calls and
gating them on a flag set for the duration of the method
(`Payload/TimeEnforcementGuard.cs:14-21,70-73,84-92,186-189`). Version 1 of that guard skipped BT's
`EnforcePlaySpeed` outright, which let BT's internal time state machine go stale and plausibly produced
a stuck shared pause where host unpause clicks were vetoed while a peer was connected.

**In a setter prefix, veto the exact transition.** Return `false` only when the `(old, new)` pair matches
the unwanted change — read the old value from `__instance`, the new from the `value` parameter
(`Payload/MapClickSpeedKeeper.cs:79-100` blocks `UnstoppableFastForward → StoppablePlay` only, and only
while `_inMapClick`). Everything else in that hot engine setter keeps working, including click-to-unpause.

**Only install a dependent hook when its scoping hook landed** (`if (count > 0)`), or you leave a
permanently-armed setter blocker with no owner (`Payload/TimeEnforcementGuard.cs:81-92` — the
`set_TimeControlMode` / `SetTimeControlModeLock` / `set_TimeControlModeLock` blockers are installed only
inside the `count > 0` gate).

### 2.8 Patch by name via AccessTools, so updates degrade gracefully

Resolve **everything** at runtime — `AccessTools.Method`, `AccessTools.Field`,
`AccessTools.PropertyGetter`, `AccessTools.PropertySetter`, `AccessTools.TypeByName` — null-check the
result, and on a miss log `<tag> inactive — members not resolved (game update?)`, report
`Diag.Report(component, false, …)` and **return without patching**
(`Payload/SiegeCommandGuard.cs:93-109`; `Payload/SiegeGatePromptFix.cs:42-49`;
`Payload/CivilianGateCloseFix.cs:40-48`; `Payload/CoopCommandSplit.cs:88-94`;
`Payload/BackgroundTickBudgetGuard.cs:57-68`). The failure mode of a crash-guard mod must never be a
crash. `Payload/PartyAiCrashGuard.cs:37-57` shows the same resolution discipline without the reporting
half — it null-checks each member and counts, but on a total miss it logs "active on 0 method(s)" and
carries on, never calling `Diag.Report`, so a drift there never reaches the health board. That is the
gap §5.4 describes.

**Enumerate overloads by name.** `AccessTools.Method` ambiguates on overloads and cannot see all
binding-flag combinations at once, so loop instead — this is the only reliable way to reach private,
static and overloaded members of an obfuscated third-party assembly:

```csharp
foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance |
                                         BindingFlags.DeclaredOnly))
{
    if (m.Name != target || m.IsAbstract) continue;
    try { harmony.Patch(m, prefix, postfix); patched++; }
    catch (Exception ex) { Log.Info("[TAG] could not patch " + m.Name + ": " + ex.Message); }
}
Log.Info("[TAG] " + (patched > 0 ? "active on " + patched + " method(s)" : "no patchable method " + target));
```

(`Payload/TracePatches.cs:49-82`; `Payload/ControlTrace.cs:48-87`; `Payload/CoopBattleTrace.cs:55-92`;
`Payload/CharacterCreationTrace.cs:51-90`; `Payload/RoleTrace.cs:49-59`;
`Payload/EncounterLoopGuard.cs:61-76`; `Payload/TimeFlowPatch.cs:44-53`;
`Payload/MapClickSpeedKeeper.cs:40-51`.) Per-method `try/catch` means one renamed overload does not cost
you the others; the logged count is a free drift detector, and the self-test can assert it is still
`> 0` (§5). `TimeTrace` wraps the whole thing in one `PatchByName(typeName, methodName, prefix, postfix)`
helper that returns the count, so one tracer can target four members across two assemblies with zero hard
type references (`Payload/TimeTrace.cs:38-79`).

**Disambiguate by shape, not by parameter list.** Patch `Incident.InvokeOption` only when
`ReturnType == typeof(List<TextObject>)` (`Payload/MapIncidentCrashGuard.cs:73,94`); patch every
`Mission` method named `SpawnTroop` that returns `Agent`
(`Payload/CoopCommandSplit.cs:79-87,97,423-429`). Cheap, update-resistant, and no spawn path silently
escapes the guard.

**Validate the return type before patching.** A postfix taking `bool __result` breaks if the target
becomes `void` — check `ReturnType` and refuse to install with a named reason instead
(`Payload/JoinSyncPauseEscape.cs:83-93`, which appends `" ToggleHostManualPause(bool-return)"` to a
`missing` string and reports `Diag.Report("join-sync-pause-escape", false, "missing:" + …)`; the
shape-filter variants are `Payload/MapIncidentCrashGuard.cs:94` and `Payload/CoopCommandSplit.cs:81`).

**Field-first or property-first, and know which trap you are avoiding.** TaleWorlds' VM info types mix
public fields and auto-properties, and `AccessTools.Field(t, "IsDisabled")` returns **null** for an
auto-property (the backing field is `<IsDisabled>k__BackingField`), which degrades a bool read to
`false` — a silent false-negative, not an exception. Try `AccessTools.Field` then `AccessTools.Property`
and make the self-test accept *either* shape, so an upstream field→property refactor turns the test red
instead of quietly blinding the code (`Payload/ClanPartyCreationAdvisor.cs:157-167,332-336`). In the
other direction, for reading a foreign mod's state, try the **property first and fall back to the
field**, because third-party code freely converts public fields to properties and back between releases
(`Payload/BattleMode.cs:586-621`; `Payload/CoopBattleTrace.cs:174-193`; `Payload/RoleTrace.cs:145-164`;
`Payload/ShareTimeControl.cs:37-38,142-144,172-173`).

**Write state that has no public mutator.** `AccessTools.PropertySetter(typeof(Hero), "DeathMark")?
.Invoke(hero, new object[] { KillCharacterAction.KillCharacterActionDetail.None })` — the `?.` makes a
missing setter a no-op instead of an NRE (`Payload/IllnessDeathGuard.cs:121-122`; `Hero.DeathMark` is a
`KillCharacterActionDetail`, and the enum is nested in
`TaleWorlds.CampaignSystem.Actions.KillCharacterAction`). For a get-only auto-property, write the backing field:
`AccessTools.Field(type, "<IsPlayerGeneral>k__BackingField")`, cached at `Apply` and written with
`SetValue` (`Payload/SiegeCommandGuard.cs:101-102,373-380`) — and treat that hook as **optional** with an
announced degradation, because the name is a compiler detail that can vanish (:118-126,132).

**Make the bad case unrepresentable at the call site.** When a type is resolved by name, wrap the method
lookup so a null `Type` cannot reach `AccessTools.Method`: a one-line private struct whose constructor
returns a null `Method` for a null `Type` beats remembering a guard clause
(`Payload/ClanScreenCrashGuard.cs:27-29,83-91`).

**Against obfuscation.** Find a member by *signature* — return type plus parameter types, where a
parameter type can itself be a type you discovered at runtime
(`Payload/JoinSyncPauseEscape.cs:140-157`). Find an obfuscated *type* by requiring **two independent
fingerprints** on the same type: it references a known packet type in some method's parameters **and** it
declares the exact target signature (`Payload/JoinSyncPauseEscape.cs:159-226`). A single-marker search
("any `static void A(string,string,bool)`") collides across an obfuscated assembly; two markers make a
false positive very unlikely. Log the resolved type name so drift is diagnosable from the log alone
(:214).

**`Assembly.GetTypes()` throws on a partially loadable assembly.** Catch
`ReflectionTypeLoadException` and continue with `loadEx.Types`, skipping the null entries it pads in for
unloadable types (`Payload/BattleMode.cs:468-478`; `Payload/JoinSyncPauseEscape.cs:174-189`). In a modded
Bannerlord AppDomain this is common, not exotic.

**Pin values, not just names.** The siege self-test asserts `(int)OrderType.AIControlOn == 36`
(`Payload/SiegeCommandGuard.cs:534`). A member can resolve by name while an enum's numeric layout shifts
underneath you, and a depth counter keyed on an `OrderType` comparison would then silently pass through
the wrong order. Where you can, resolve the enum value **live** instead of hard-coding an ordinal:
`TypeByName` on the nested type + `Enum.Parse` + `Convert.ToInt32`, keeping the known ordinal only as a
labelled fallback and logging loudly when the live value differs
(`Payload/StashSync/StashSyncGuard.cs:52-55,95-115`).

### 2.9 IL inspection: patching the *right* compiler-generated lambda

Lambda numbering (`<SiegeProgressChange>b__1` vs `b__2`) is not a stable contract — it changes on any
recompile of the game. Select by what the IL **does**:

1. Enumerate the target type's nested display types, filter by name prefix and return type
   (`Payload/MapIncidentCrashGuard.cs:66-80`).
2. Read `method.GetMethodBody().GetILAsByteArray()`, scan for `0x28` (`call`), resolve the 4-byte operand
   with `method.Module.ResolveMember(BitConverter.ToInt32(il, i + 1))`, and match on `target.Name` and
   `target.DeclaringType` (`Payload/MapIncidentCrashGuard.cs:120-158`).
3. Put each `ResolveMember` in its **own** `try/catch` — a byte inside a metadata token can look like an
   opcode — and advance `i += 4` only after a *successful* resolve.

Then keep the fix honest:

- **Check the exact dereference chain first and return `true` when it is intact**, so the healthy path
  runs vanilla untouched; the patch is live only in the state that would have crashed
  (`SiegeChainIntact()`, :160-175, called at :215-218).
- **Read the captured state instead of guessing it.** `displayClass.GetType().GetField("amountGetter",
  Instance|Public|NonPublic)` yields the same `Func<float>` vanilla would have called, so the replacement
  applies the identical amount (:248-259) — and can rebuild vanilla's own localized text
  (`{=C0kUpB48}{?AMOUNT > 0}Increased{?}Decreased{\?} siege progress by {ABS(AMOUNT)}%.` with
  `AMOUNT = MathF.Round(amount * 100f)`, :231-234), keeping every shipped translation. The player cannot
  tell the fix from vanilla.
- **One null can have two causes.** Probe an ordered array of alternate derivations
  (`main.SiegeEvent`, `main.CurrentSettlement.SiegeEvent`, `main.AttachedTo.SiegeEvent`,
  `main.Army.LeaderParty.SiegeEvent`, …) with the **same validity predicate** the crash site needs, so
  the object you return cannot re-trigger the original NRE (:177-211). Repair when you find a live
  object; report the truth ("The siege has already ended.") only when there genuinely is none
  (:22-31,213-246). Never collapse both into "skip the effect" — that silently deletes a game feature for
  co-op players.
- **Pin the negative half in the self-test**: require ≥1 lambda matched *and* ≥1 deliberately not matched
  (:309-337), so both over- and under-selection fail.

### 2.10 Transpilers: the two shapes that earn their cost

**(a) Collapse one call site into a helper, preserving labels.** Scan the instruction list for
`list[i].Calls(get_CurrentTime)` preceded by `list[i-1].Calls(get_Current)`; rewrite the **first**
instruction in place and `Nop` the second:

```csharp
list[i - 1].opcode  = OpCodes.Call;
list[i - 1].operand = safeHelper;   // labels/exception blocks on this instruction survive
list[i].opcode      = OpCodes.Nop;
list[i].operand     = null;
_patchedSites++;
```

(`Payload/MovementOrderTypeInitGuard.cs:85-113`.) Rewriting the first — not the second — is what keeps
any labels and exception blocks attached to that instruction valid, so branch targets still resolve. The
pair pushed one float; the helper pushes one float, so the net stack effect is unchanged. Count patched
sites and **log when the count is zero** ("game changed?") rather than assuming the patch landed.

**(b) Inject a preamble to override a value-typed foreign result.** A Harmony postfix cannot rewrite a
value-typed result of a foreign *internal* enum — three quieter approaches failed **silently** in a
purpose-built rig. Emit `Call <decider> / Brfalse <label> / Ldc_I4_1 / Ret` ahead of the original IL and
attach the label to the **first original instruction**, so the untouched body still runs when the decider
says no (`Payload/ClanModeSoloFix.cs:64-82`).

**The decider must live in a `public` static class.** The emitted call site ends up inside the foreign
assembly, and an internal target is an accessibility violation at JIT time that no compiler warns about
(`Payload/ClanModeSoloFix.cs:122-123`).

**Beat the JIT.** Callers jitted *before* your patch keep the inlined original, so an IL-rewriting patch
that "applied successfully" can be invisible to existing callers — the single most confusing failure mode
when a transpiler seems to do nothing. Apply at module load, before campaign code compiles
(`Payload/ClanModeSoloFix.cs:26-28`, applied from `Payload/PayloadEntry.cs:55` and retried at :118).
**Verify by reading the value back through `MethodInfo.Invoke`** — a reflection call always goes through
the Harmony detour and can never hit an inlined copy
(`Payload/ClanModeSoloFix.cs:84-101`, consumed by `Payload/MarriageBarterGuard.cs:83`).

### 2.11 The `beforefieldinit` type-init hazard, and safe pre-initialization

`beforefieldinit` lets the CLR run a type's static constructor at any point up to the first static field
access — in practice, whenever the JIT feels like it. If that `.cctor` can throw (here: it builds
defaults via an instance ctor that dereferences `Mission.Current`), you get a
`TypeInitializationException` at an unpredictable moment, and **the failure is cached for the process**:
every later touch re-throws the *same* exception with the *original* stack.

The fix has two halves. First make the throwing line safe (§2.10a). Then **choose when the static ctor
runs**:

```csharp
try
{
    RuntimeHelpers.RunClassConstructor(typeof(MovementOrder).TypeHandle);
    Log.Info("[MO-INIT] MovementOrder initialized safely (patched " + _patchedSites + " site(s))");
}
catch (TypeInitializationException tie)
{
    Log.Info("[MO-INIT] MovementOrder was ALREADY poisoned before this guard could patch it … "
           + "the fix must move into the harness SubModule");
}
```

(`Payload/MovementOrderTypeInitGuard.cs:59-71,80-83`.) The type is then cached as *successfully*
initialized for the rest of the process, and the `catch` doubles as a diagnostic that says "too late,
move the fix earlier" — it distinguishes "fixed" from "you lost the race".

**Ordering is part of the fix.** `MovementOrderTypeInitGuard.ApplyEarly` is the **first patch installed**
by `PayloadEntry.Apply` — before `harmony.PatchAll` and every other guard, with only the `safeMode` kill
switch ahead of it (§7.5) — and a comment states why: patching `Formation`/`OrderController` is itself
what makes the CLR prepare `MovementOrder` (`Payload/PayloadEntry.cs:31-46`). Your own instrumentation can trigger the type preparation you are
trying to make safe. This repo states it as a convention: a fix that must run before the game touches a
type goes first in `PayloadEntry.Apply` (`CLAUDE.md:77-78`).

### 2.12 Per-generation Harmony ids and `UnpatchAll`

Harmony keys patches by **owner string**. Give each hot-reload generation its own instance and id —
`new Harmony("bltogether.crashguard.gen" + N)` — and the previous generation's hooks come off with
`new Harmony(prevId).UnpatchAll(prevId)` (`Harness/HotReload.cs:14-16,358-360,374-378`;
`Payload/PayloadEntry.cs:14-16`). On .NET Framework, where assemblies cannot be unloaded, this is the
only clean way to swap a patch set at runtime.

It also changes the shape of the question "is this patch mine?" from an identity test to a **prefix**
test. Every owner id this mod uses starts with the literal `bltogether`, so `IsOwnOwner` is a
`StringComparison.Ordinal` `StartsWith` (`Payload/BattleMode.cs:91-97,286`). An equality test against one
id would let the mod unpatch its own previous generation — or fail to recognise it and rip out its own
guards.

**Apply new before unpatching old.** The payload's `Apply` rethrows after logging, and the harness applies
the new generation *before* swapping and unpatching the old one
(`Payload/PayloadEntry.cs:108-112`; `Harness/HotReload.cs:366-378,387-394`). A failed reload therefore
keeps the previous generation: the worst outcome of a bad build is "you are still on the previous fix
set", not "all crash guards silently off".

### 2.13 Lifting and restoring another mod's patches

`Harmony.Unpatch` is normally irreversible — you cannot put another mod's patch back, because you do not
have its `Harmony` instance. You can, if you capture enough metadata first.

From `Harmony.GetPatchInfo(method)` record, per patch: `owner`, kind (prefix/postfix/finalizer/
transpiler), `PatchMethod` (`MethodInfo`), `priority`, `before[]`, `after[]`
(`Payload/BattleMode.cs:249-319`). Then `harmony.Unpatch(method, HarmonyPatchType.All, owner)` once per
distinct foreign owner.

To restore, build `new HarmonyMethod(patchMethod) { priority, before, after }`, construct
`new Harmony(originalOwnerId)` and dispatch by kind — mind the argument positions
(`Payload/BattleMode.cs:200-206`):

```csharp
case 0: h.Patch(orig, patch); break;                     // prefix
case 1: h.Patch(orig, null, patch); break;               // postfix
case 2: h.Patch(orig, null, null, null, patch); break;   // finalizer
case 3: h.Patch(orig, null, null, patch); break;         // transpiler
```

Guard rails that matter in practice:

- **Know what "mine" is** before mass-unpatching (§2.12).
- **Check before re-patching.** Re-apply only if the same owner + `PatchMethod` is not already on that
  kind, and refresh `GetPatchInfo` after each success — otherwise a mod that re-applied its own patch
  meanwhile ends up with a duplicate postfix running its side effects twice
  (`Payload/BattleMode.cs:186-190,208,321-347`).
- **Dedupe the stash** on `(kind, owner, PatchMethod)`; the decision runs many times per session and an
  ever-growing stash would multiply restores (`Payload/BattleMode.cs:297-317`).
- `GetPatchInfo` can return null, and each of `Prefixes`/`Postfixes`/`Finalizers`/`Transpilers` can be
  null — null-check both levels.
- **Unpatch is per-owner but the stash is per-kind**, so the restore path must be able to rebuild several
  kinds for one owner.
- **The stash is state that must survive a hot reload** — a payload static will not do it. This repo's
  known gap is exactly that: reloading while BT's battle patches are lifted can leave them lifted
  (`HOTRELOAD.md:65-68`). See §4.5.

### 2.14 Installing patches: attributes, explicit `Apply`, latches and retries

**Attribute `[HarmonyPatch]` + `PatchAll` for stable engine targets; explicit `Apply(Harmony)` for
anything conditional.** Attribute patches fail hard when a type is missing, so every guard that inspects
the world first (is BT loaded? does the type exist?) and may decide to stay inactive exposes
`internal static void Apply(Harmony)` and is called in a deliberate order
(`Payload/DeploymentCrashGuards.cs:13,34` with `Payload/PayloadEntry.cs:46`, versus
`Payload/PayloadEntry.cs:48-72`).

**Scope `PatchAll` to your own assembly**: `harmony.PatchAll(typeof(PayloadEntry).Assembly)`, not the
parameterless overload, which uses the *calling* assembly — not necessarily what you mean in a
hot-reload/shadow-copy setup (`Payload/PayloadEntry.cs:44-45`).

**Idempotent `Apply` + lifecycle retry for late-loading assemblies.** Module load order is not
guaranteed, so a BT-facing guard that tries once is permanently inactive on exactly the sessions it
exists for:

```csharp
internal static void Apply(Harmony harmony)
{
    if (_applied) return;                 // hot-reload / retry safe
    Type bt = AccessTools.TypeByName("BannerlordTogether.…");
    if (bt == null) return;               // silently: BT may load later
    …
    _applied = true;                      // latch only after success
}
```

(`Payload/BackgroundTickBudgetGuard.cs:42,50-53,72`; `Payload/EncounterLoopGuard.cs:30,49-52,79`;
`Payload/ClientBootstrapFix.cs:36-38,83`; `Payload/ClanModeSoloFix.cs:32-34,52`;
`Payload/SiegeCommandGuard.cs:81-84`; `Payload/CoopCommandSplit.cs:66-69`.) `PayloadEntry` re-calls those
`Apply` methods at `OnBeforeInitialModuleScreen` and `OnGameStart`
(`Payload/PayloadEntry.cs:115-124,129`). Without the retry the guard silently never applies; without the
latch the retry double-patches. A one-shot late hook for a *sub-set* of patches works the same way:
`PatchBtReleases` returns a count and `RetryBt` runs it a second time, guarded by
`_applied && _btPatched == 0 && !_btRetried` (`Payload/SiegeCommandGuard.cs:142-155,168-203`).

**Wrap every `Apply` in `try/catch`** and report `Diag ok=false` with the message, because the entry
point calls them in sequence and a throw in one would otherwise skip every later guard
(`Payload/IllnessDeathGuard.cs:36-63`; `Payload/MarriageBarterGuard.cs:33-51`;
`Payload/ClanScreenCrashGuard.cs:24-43`; `Payload/PayloadEntry.cs:47-70`).

**Re-decide at every chokepoint when the answer depends on external state.** A latch is right when the
decision can only be made once; it is wrong when the world can change underneath it. Whether BT's battle
patches should be lifted depends on whether a friend is connected *right now*, so the same
`DecideAndApply(harmony, reason)` is called at apply, module-screen, game-start, mission-init,
mission-open and start-battle (`Payload/BattleMode.cs:99-225`, from `Payload/PayloadEntry.cs:99,123,130,137`
and `Payload/TracePatches.cs:89,181`). Two properties make that affordable: re-deciding is cheap and
idempotent, and a `_lastVanilla` change latch plus a "did anything actually move" count restrict logging
and on-screen messages to **real transitions** (`Payload/BattleMode.cs:77,168-170,216-218`) — otherwise
six chokepoints produce six identical lines per mission.

**Reset per-mission state in one place.** `OnMissionInit()` zeroes counters, one-shot flags and all three
depth counters, and clears resolved parties, hero names and the ghost id
(`Payload/SiegeCommandGuard.cs:157-166`; `Payload/CoopCommandSplit.cs:108-120`), driven from
`Payload/PayloadEntry.cs:138-139`.

**Subscribe to campaign events keyed on identity.** `CampaignEvents` is per-campaign and null at load.
Guard the subscribe with `ReferenceEquals(_subscribedCampaign, Campaign.Current)` and store the campaign
after subscribing: re-entry is a no-op, a *new* campaign re-subscribes, and hot-reload generations do not
multiply listeners (`Payload/PregnancySync/PregnancySyncGuard.cs:76,84-89`).

### 2.15 Choosing what to patch

- **A narrow layer for the proven state, a broad layer for everything else.** Layer 1 skips only the one
  shape proven from IL; layer 2 catches any other escaping exception from the same method and answers
  `Hold`; layer 3 covers the sibling organ of the same disease
  (`Payload/PartyAiCrashGuard.cs:22-25,77-99,101-123,131-147`). The narrow layer keeps normal behaviour
  untouched; the broad layer guarantees no CTD for shapes not yet diagnosed; each layer's fires are
  counted separately as evidence.
- **Class-level safety nets above a specific fix.** Patching the class-wide entry points
  (`IncidentEffect.Consequence`, `Incident.InvokeOption`) turns any *other* stale-state throw of the same
  family into a logged skip — containing an unknown blast radius without pretending it is fixed. Label
  each fire a root-fix candidate (`CHANGELOG.md:257-259`).
- **Pick the API the AI uses, not the one the player uses.** `Formation.TransferUnits` is patched (tactic
  path); `OrderController.TransferUnits` deliberately is not (order-UI path)
  (`Payload/SiegeCommandGuard.cs:48-50,94`). Identifying which of two similar APIs each actor uses lets
  you block the AI without taking a capability from the player.
- **Correct with vanilla's own rule.** Re-derive the engine's decision rather than inventing behaviour:
  the gate fix re-derives the excluded tag exactly as vanilla does (state `Closed` → exclude `"close"`,
  else exclude `"open"`) and acts only inside the band vanilla's own test misses
  (`Payload/SiegeGatePromptFix.cs:92-107`); the siege take-over issues a MOVE order to the position the
  formation **already holds** (`Payload/SiegeCommandGuard.cs:427-432`); the civilian-gate fix leaves
  closing itself to `CastleGate.CloseDoor` (`Payload/CivilianGateCloseFix.cs:23-25`). The fix then stays
  correct on content you never saw.
- **Reuse the vanilla entry point rather than reimplementing the flow** —
  `PartyScreenHelper.OpenScreenAsManageTroops`, `CastleGate.CloseDoor`/`OpenDoor`,
  `ChangePlayerCharacterAction`, `SetProgress` with the same report text
  (`CHANGELOG.md:95-98,169-170,142-144,251-253`). You inherit animation, nav-mesh, colliders,
  screen-stack behaviour and report text for free, and stay compatible with whatever else patches that
  path.
- **Write diff-only.** The gate fix calls `SetIsDeactivatedSynched` only when the value actually changes,
  and counts only the points it turned on (`Payload/SiegeGatePromptFix.cs:98-107`) — which avoids
  re-broadcasting synchronised state every tick and makes "activated > 0" a true edge trigger.
- **Add a tick finalizer the moment you activate a previously dormant tick path.** Civilian gates tick
  for the first time because of this mod, so `CastleGate.OnTick`/`ServerTick` got a pre-emptive
  suppressor with a 5 s-throttled line (`Payload/CivilianGateCloseFix.cs:55,100-114`). A dormant path may
  carry assumptions that only held in the mode where it used to run.
- **Explain, do not "fix", when the behaviour is intentional.** Where IL proved the reported bug was
  designed behaviour (the hideout "soldier" is the player's own `StealthEquipment` disguise), the
  response was an on-screen explainer plus a guarantee for the part that genuinely is fragile — not a
  behaviour change (`Payload/StealthHideoutAdvisor.cs:20-26,69-79`; `CHANGELOG.md:106-118,180-181`). A
  large share of "the game is broken" reports are discoverability failures; explaining costs nothing and
  cannot regress.
- **Coexist deliberately.** The illness guard never increments ill days and only ever cures, so a
  standalone *NoSickness* mod's prefix always sees a healthy hero and passes through — a compatibility
  property written into the header (`Payload/IllnessDeathGuard.cs:25-27`). When two mods patch the same
  method, the safe composition is for one to be strictly state-reducing, and that reasoning has to be
  written down or the next change breaks it. Where the other mod owns the behaviour outright, **stand
  down** (`CHANGELOG.md:303-304`).

### 2.16 Patches that live in a per-tick hot path

- **Fail open.** Wrap every prefix body in `try/catch` and return the vanilla-preserving value on any
  internal error — `return;` in a void prefix, `return true;` in a bool prefix, `return __exception;` in
  a finalizer (`Payload/SiegeCommandGuard.cs:302-306,331-334,359-362,384-386,447-450`;
  `Payload/EncounterLoopGuard.cs:128-131`; `Payload/PartyAiCrashGuard.cs:94-98,117-121,143-146,164-166`;
  `Payload/MapIncidentCrashGuard.cs:146-157,171-174,206-209,273-276`;
  `Payload/CoopCommandSplit.cs:208-211,225-227,374-378`).
- **Extract the decision into a pure, internal, testable function.** Keep the branching in
  `internal static` functions with no engine types — `ShouldRefuseHandoff(...)`,
  `SiegeGatePromptFix.Decide(float)`, `CoopCommandSplit.BasicSlot`/`TargetIndex`/`IsOutOfBlock`,
  `BackgroundTickBudgetGuard.ComputeBlockMs(long)`, `JoinSyncPauseEscape.Decide(bool,bool,bool,bool)` —
  and let the patch body just call them (`Payload/SiegeCommandGuard.cs:270-276`;
  `Payload/SiegeGatePromptFix.cs:143-146`; `Payload/CoopCommandSplit.cs:153-184`;
  `Payload/BackgroundTickBudgetGuard.cs:88-95`; `Payload/JoinSyncPauseEscape.cs:269-278`). Patch plumbing
  cannot be unit-tested in-process; the decision logic can (§5).
- **One scope predicate, shared by the patches on the hot path.** `InScope(out Mission mission)` answers
  "is this the situation I exist for?" once — `Mission.Current != null`, `IsSiegeBattle`,
  `!IsSallyOutBattle`, `PlayerTeam.Side == Defender`, not a BT client — with
  `IsGuardedFormation(formation, mission)` as its per-formation companion
  (`Payload/SiegeCommandGuard.cs:209-228`, used at :289 and :314). Those two formation patches share one
  definition of scope: a scope bug is fixed in one place, and the whole condition is legible on one
  screen. The guard installs six patches in all (:110-117,121), and two of them still re-derive a
  narrower scope inline — the role prefix at :349 and the deployment postfix at :394-395 — which is
  exactly the drift this pattern exists to prevent.
- **Rate-detect without allocating.** A fixed `int[TripCount]` ring buffer with a rolling index: each call
  overwrites the oldest slot and compares `now - oldest < WindowMs`. O(1), no lists, no timers, no GC
  pressure inside a game loop (`Payload/EncounterLoopGuard.cs:31,114-117`).
- **Gate a rate limiter on the pathological *signature*, not on rate.** A pure-rate breaker can suppress a
  partner's legitimate join storm; this one only counts applications that closely follow a locally
  observed `PlayerEncounter.Finish` (within 4 s, stamped by `NoteEncounterFinish`), which makes false
  positives structurally impossible for traffic with no preceding local Finish
  (`Payload/EncounterLoopGuard.cs:37-45,109-112`). Corollary: a guard that depends on a signal produced
  by an optional subsystem is only as live as that subsystem.
- **Trip *and* auto-retry.** After tripping, suppress and slide `_lastSuppressedTick` forward; after
  `RetryAfterMs` (60 s) of suppression, un-trip and let exactly **one** call through — which re-trips if
  the loop is still live, or resumes normal operation if the stuck entry cleared
  (`Payload/EncounterLoopGuard.cs:94-107`). Permanent suppression breaks the feature forever after one
  bad minute.
- **Throttle, do not disable.** `ComputeBlockMs(e) = e <= Budget ? 0 : Math.Min(e, MaxBlock)` — block the
  call site for exactly as long as the last call took (capped), so the foreground is guaranteed roughly
  half of wall time no matter how heavy the background work becomes, and the guard is *inert* under
  normal load (`Payload/BackgroundTickBudgetGuard.cs:36-40,85-95`; the BT case at
  `CHANGELOG.md:236-240`). It degrades proportionally with the pathology and needs no per-scenario tuning
  constant.
- **A prefix/postfix timing pair needs a zero sentinel.** The postfix still runs when the prefix skipped
  the call: stamp `_startTimestamp = Stopwatch.GetTimestamp()` only when the call proceeds, return early
  from the postfix when it is `0`, and reset it to `0` right after measuring
  (`Payload/BackgroundTickBudgetGuard.cs:97-121`). Elapsed ms is
  `(GetTimestamp() - start) * 1000 / Stopwatch.Frequency` — high-resolution, allocation-free.
- **Cap corrective loops.** Five corrections per mission, the counter reset by
  `ReferenceEquals(Mission.Current, _lastMission)`, and each repair step in its **own** `try/catch` so one
  throwing property cannot abandon the rest (`Payload/PlayerIdentityGuard.cs:27,49-57,91-135`). A
  corrector that fights another system otherwise loops forever and tanks the frame rate.
- **Cache and throttle external-state resolution.** `ResolveParties` returns immediately once both parties
  are cached and otherwise retries at most every 2 s, so a solo game costs one reflection probe every two
  seconds instead of one per spawned agent (`Payload/CoopCommandSplit.cs:51,327-338`).
- **`Environment.TickCount` wraps.** Every delta comparison is paired with a direction check —
  `now - last < Window && now >= last`, or `now - last > Retry || now < last` — so a wrap degrades to
  "allow/log now" rather than latching a breaker forever. This appears in essentially every timed path in
  the repo (`Payload/EncounterLoopGuard.cs:96,109,117`; `Payload/PartyAiCrashGuard.cs:155`;
  `Payload/BackgroundTickBudgetGuard.cs:130`; `Payload/SiegeCommandGuard.cs:512-521`;
  `Payload/TraceThrottle.cs:63-65`; `Payload/PayloadEntry.cs:166,194`). See §10.6 for the exact
  arithmetic.

### 2.17 Never do UI work on a call stack that is still unwinding

Opening a screen from a VM postfix runs while the originating popup and inquiry are still unwinding.
Record pending state — leader, party, `Environment.TickCount`, an "open not before" tick — and return; do
the real work from a per-frame `Tick()` pumped by the module entry point
(`Payload/ClanPartyCreationAdvisor.cs:48-58,185-197,199-267`, pumped at `Payload/PayloadEntry.cs:154`).

Around that pump:

- **A settle window** when a networked peer may replace the object you are waiting on (3 s on a BT
  client), because a client-side provisional object can be replaced by the host-authoritative one.
  Re-check **identity** with `ReferenceEquals` and restart the window on a swap, logging "party instance
  changed (co-op reconciliation)" (:226-233).
- **A bounded timeout** (`PendingTimeoutMs = 15000`) that exits with a user-visible fallback note —
  "could not open the troop exchange automatically — click the party on the map to fill it" — never wait
  forever for state a peer may never confirm (:51,209-216).
- **Screen-state gating before pushing a screen**: refuse if `Mission.Current != null`, refuse if the
  active state is already a `PartyState`, pop a `ClanState` with `PopState(0)` to land on the map, and
  refuse if the active state is not `MapState` — the same preconditions vanilla's own manage-troops flows
  satisfy (:235-256). Pushing a screen over a mission or another party screen is the classic way a
  helpful mod wedges the UI.
- **Defer one tick and pop the owning screen** when opening a screen from inside another screen's confirm
  handler, so the screen stack matches what vanilla's equivalent flows produce and back-navigation
  behaves (`CHANGELOG.md:96-98`).
- **Re-apply a derived arrangement on the event *and* on a timer.** A formation layout computed once
  decays: the Order of Battle screen re-sorts by class and reinforcements arrive later, so
  `CoopCommandSplit` re-applies at `OnDeploymentFinished` and then every half second
  (`CHANGELOG.md:42-44`).

---

## 3. Reflecting into another mod

This mod has **no compile-time reference to BannerlordTogether at all**. Everything it knows about BT it
learns at runtime by name. That is a deliberate compatibility strategy, not a shortcut: a BT update that
renames a member turns one guard off instead of failing the module load, and the health line (§5) says
which member vanished.

### 3.1 Finding the other mod's types

Scan `AppDomain.CurrentDomain.GetAssemblies()` for the assembly whose `GetName().Name` matches, call
`GetTypes()`, and match on the simple type name (`Payload/BattleMode.cs:456-490`, consumed by
`Payload/CoopBattleTrace.cs:37,60` and `Payload/RoleTrace.cs:39`). Two mandatory details:

- **`ReflectionTypeLoadException` is recoverable.** An assembly with one unresolvable dependency throws
  on `GetTypes()`; `loadEx.Types` still carries everything that *did* load, with nulls interleaved for
  what did not — so filter `type != null` and keep scanning
  (`Payload/BattleMode.cs:468-478`; `Payload/JoinSyncPauseEscape.cs:174-189`).
- **Try a priority list of candidate names.** Resolve by an ordered list of fully-qualified names — new
  namespace first, legacy names as fallbacks — and patch wherever the member is found
  (`Payload/PregnancySync/PregnancySyncGuard.cs:225-239`;
  `Payload/StashSync/StashSyncGuard.cs:117-131`). This is not theoretical: BT moved
  `CoopNetworkBase`/`CoopServer` into `BannerlordTogether.Network.*` and silently killed both sync
  features until the health line surfaced it (`CHANGELOG.md:129-132`). The same idea applies to engine
  types that move between assemblies — `RoleTrace` tries `TaleWorlds.Core.MBSaveLoad` then
  `TaleWorlds.SaveSystem.MBSaveLoad` (`Payload/RoleTrace.cs:44-45`), and `ClientBootstrapFix` walks
  `TaleWorlds.Core` / `.Engine` / `.MountAndBlade` candidates
  (`Payload/ClientBootstrapFix.cs:105-138`).

**Decide deliberately whether "absent" is cached.** `PeerDetection` caches even a negative `CoopSession`
lookup behind a `_searched` latch to keep a hot path cheap (`Payload/BattleMode.cs:493-504`) — which
means a dependency assembly that loads *later* is never found for the rest of that payload generation,
while other guards explicitly retry at `OnBeforeInitialModuleScreen` (§2.14). A negative cache and a
retry policy in the same codebase will disagree; know which one you want for each call site.

**Refuse to activate on partial resolution.** Require **every** member a fix depends on to resolve before
activating; missing any means engine drift, so return `false` and report a *critical* health failure
rather than force-passing with dead reflection (`Payload/ClientBootstrapFix.cs:68-80,135-138`). A
half-resolved reflection set silently skips checks — here it would have meant force-verifying BT's
bootstrap without actually proving the catalog was ready.

### 3.2 Reading the other mod's static session state

Use one helper that tries `GetProperty(name, Public|NonPublic|Static)` and falls back to `GetField` with
the same flags, catching everything to `null`, mirrored for instance members
(`Payload/BattleMode.cs:586-621`; `Payload/ShareTimeControl.cs:37-38,142-144,172-173`). Third-party code
converts public fields to properties and back between releases; one helper survives both shapes without
a version check.

**When invoking by reflection, trust the out parameters, not the return value.** Size an `object[]` to
the parameter list, ignore the `MethodInfo`'s return (it may be void), and read the by-ref results back
out of the args array — `args[0] is bool && (bool)args[0]`, `args[1] as string`
(`Payload/ShareTimeControl.cs:121-136`). That also keeps the call working unchanged if the return type
changes between versions.

**A silent read has no failure signal.** These helpers swallow everything to `null`, so a renamed member
surfaces only as "unknown" downstream. If the fix is load-bearing, pin the members with a self-test (§5)
— and when a best-effort chain that gates a *safety* behaviour cannot be resolved, distinguish
"legitimately absent" from "chain broken" and log **once** with a latch flag, naming the likely cause
("game update?") and the consequence ("peer updates apply immediately")
(`Payload/StashSync/StashSyncGuard.cs:51,390-400`). A bare `catch {}` that returns the permissive answer
is invisible forever.

### 3.3 Unknown is a third value

Every read returns `bool?` / `string` / `object` where **null means "could not read"**, and the
*consumer* decides which way unknown fails (`Payload/BattleMode.cs:506-555` produce,
:120-144 consume with the direction documented). In this mod unknown fails **toward co-op**, because
being wrong in the other direction sabotages a live session. Collapsing unknown into `false` is exactly
how the 2026-08-19 mid-session desync happened, and `Server == null` alone was the culprit — a confident
`false` now requires corroboration (`isHost == false && isClient == false`,
`Payload/BattleMode.cs:528-539`).

Consumers state the direction in the expression itself: `AnyRemotePeerConnected() != false` treats
unknown as connected (`Payload/TimeEnforcementGuard.cs:155-156`);
`ReadCoopStaticBool("IsActive") != true` passes through on unknown;
`IsClient() == true` acts only on a confident yes (`Payload/MarriageBarterGuard.cs:79-87`;
`Payload/ClanPartyCreationAdvisor.cs:190`); `Decide(null, isHost:true)` must be `false`
(`Payload/ClanModeSoloFix.cs:161-165`). Because the tri-state is a value, "never fight a real co-op
session" becomes a **self-testable** property rather than a hope.

Always give the user an explicit override for the case where the safe side is the wrong side —
`battleMode=solo` (§7).

### 3.4 Observed behaviour beats declared state

Rather than trusting a flag, stamp `Environment.TickCount` whenever the other mod's traffic is observed
and treat recent traffic as authoritative liveness, checked **before** any reflection is attempted
(`Payload/BattleMode.cs:396-416,513-516`). The co-op mod's own packet handlers firing is proof of a live
session. Two details make it safe:

- **Stamp only from a genuinely network-driven path.** `Payload/TimeEnforcementGuard.cs:228-234` stamps
  only when `CalledFromPacketHandler()` is true, because `SetPaused`/`ApplyTimeState` also fire once
  during a **solo** game load and would fake a connected session. That check walks the stack from depth 2
  testing each frame's method name for `"Packet"` (`OrdinalIgnoreCase`), bounded to 12 examined frames
  (:191-222) — cheap enough for a hot path.
- **The freshness test carries the wrap guard**: `now - last < 15000 && now >= last`
  (`Payload/BattleMode.cs:415`).

### 3.5 Working with the peer mod rather than against it

- **Prime the state a peer's self-check reads instead of patching the check out.** BT's client audit
  reads unprimed static `ActionIndexCache` mirrors; filling them from the live catalog makes the audit
  legitimately pass so BT's own deferred patches proceed. Suppressing the abort would have left those
  patches unapplied anyway — that is the difference between a workaround and a root fix
  (`CHANGELOG.md:358-360`; `docs/UPSTREAM_CONTRIBUTION.md:25-27`).
- **Reproduce the upstream gate you are bypassing.** Re-implement the foreign code's own preconditions
  (num action codes > 0, num animations > 0, four probe actions resolve, no disk load in flight) and
  remove only the one over-strict requirement, so the safety intent survives
  (`Payload/ClientBootstrapFix.cs:250-284`).
- **Ask whether the bug still exists before overriding upstream** — and probe the **whole** state, not
  one sentinel. Judge "already primed" only when *every* static mirror has a valid index; a single-field
  check can see a partially-primed state (sentinel ok, others still `-1`) and wrongly stand down into the
  very bug you fix (`Payload/ClientBootstrapFix.cs:155-170,216-248`). Log which of the two explanations
  applies — we primed it earlier, or it was never broken.
- **Reuse the peer's own sanctioned recovery routine.** Rather than inventing a teardown, invoke the exact
  method the peer's own watchdog/timeout calls, and cite that precedent in the header, so all of its
  cleanup happens the way the mod intends (`Payload/JoinSyncPauseEscape.cs:22-33,313-322`).
- **Gate a destructive recovery on consent: explain, arm, then act on the second press.** When the peer
  mod swallows the player's input and leaves the game held, the first press explains *who and what* is
  holding it and arms a bounded 6 s window; a second press inside that window performs the destructive
  recovery; any other outcome clears the arm — all of it routed through one pure `Decide(pressHandled,
  stillPaused, joinHoldActive, cancelArmed)` that is self-tested row by row
  (`Payload/JoinSyncPauseEscape.cs:29-33,240-278`). It turns a silently-swallowed keypress into
  feedback and gives the player an exit from a permanent freeze, without ever destroying a peer's
  in-flight join on one accidental press.
- **Choose the same choke point the peer already patches.** `StashSync` postfixes
  `InventoryLogic.DoneLogic` — "the same commit point BT patches for the warehouse"
  (`Payload/StashSync/StashSyncGuard.cs:22-24,73-78`) — inheriting its correctness argument (it commits,
  it is UI-driven, it does not fire mid-drag) and keeping the two mods phase-aligned.
- **Auto-clear a poisoned cache and tell the player to restart** when the peer's failure loop can never
  resolve itself (`restartRequired=True` with no mechanism to become true) — and **rename, never delete**
  (§8.6).
- **Equal-time throttle a foreign per-frame job** instead of disabling it (§2.16).
- **When there is nothing worth patching, do not patch.** If the fix is "call a shipped API until a flag
  is on", run a throttled poll from the module tick, resolve once, act only on the authority, and latch
  when done — zero patch surface, nothing to break on an update beyond the two reflected members
  (`Payload/ShareTimeControl.cs:52-119`, driven from `Payload/PayloadEntry.cs:147`).

Sending on the peer mod's own network channel is §8.3.

---

## 4. Hot-reload architecture

The iteration loop for a Bannerlord mod is otherwise launch → reproduce → crash → relaunch, three to five
minutes per attempt. This repo turns that into roughly 400 ms (`HOTRELOAD.md:34`). Everything below is
`Harness/HotReload.cs` plus two csproj tricks.

### 4.1 The harness/payload split

Keep the assembly the game loads (the `SubModule.xml` target) tiny and stable — lifecycle, logging,
config, health, the reload engine — and put **all** guards, fixes and tracers in a second assembly loaded
by the first (`Harness/SubModule.cs:6-11`; `Harness/Contracts.cs:5-10`; `HOTRELOAD.md:3-8`). Changing the
harness needs a restart; changing the payload does not. The harness DLL is also **locked while the game
runs**, so only the payload can be redeployed live — which is itself an argument for moving anything you
might need to hot-fix into the payload. `TraceThrottle` lives there for exactly that reason
(`Payload/TraceThrottle.cs:16-21`): a log-flood fix that requires a game restart cannot be applied during
the live repro you are trying to keep.

The contract is two small interfaces (`Harness/Contracts.cs`): `IPayload` (`Apply(Harmony, ISharedState)`,
`Tick`, `OnGameStart`, `OnMissionInit`, `OnBeforeInitialModuleScreen`) and `ISharedState` (a key/value bag
owned by the harness). **Keep the dependency arrow one-way, payload → harness.** Where the stable layer
needs something the reloadable layer computes, expose a setter and let the payload push: `Log.SetRoleTag`
is called each tick with the computed role, so the harness needs no reference to any payload type
(`Harness/Log.cs:8-10,32-39,69`).

**Total exception isolation at the boundary.** Every forwarded lifecycle call goes through a
`Safe(Action, name)` helper, the tick is wrapped separately, engine start is wrapped, and the logger
itself swallows everything (`Harness/HotReload.cs:84-87,104-114,265-269`; `Harness/Log.cs:62-76,122-131`).

### 4.2 Loading a generation: `LoadFrom`, a shadow copy, and a unique name

Three facts, each of which cost a release to learn (`CHANGELOG.md:119-128,211-225,272-281`):

1. **Load with `Assembly.LoadFrom` against a shadow copy, never `Assembly.Load(byte[])`.** A byte-loaded
   assembly has no load path, so its dependency probing falls back to the application base — inside
   Bannerlord that resolves `0Harmony` to a *different* copy than the one the harness patched with, the
   two Harmony instances do not see each other's patches, and `IPayload.Apply(Harmony …)` fails with
   `TypeLoadException: Method 'Apply' … does not have an implementation`
   (`HOTRELOAD.md:10` for the binding mechanism; `CHANGELOG.md:219` and `Harness/HotReload.cs:60` for
   the exception text; `Payload/BLTDeploymentCrashGuard.Payload.csproj:16-18`). Concretely: probing
   finds the game bin's `0Harmony 2.4.2.0`, while the harness is bound to the module-loaded
   `0Harmony 2.3.6.0` (`CHANGELOG.md:215-220`).
2. **Copy the canonical DLL to a unique sibling path in the *same* directory and load the copy**
   (`Harness/HotReload.cs:276-314`). Same directory keeps `LoadFrom` dependency probing pointed at the
   harness's own module folder; the canonical file stays unlocked so a build can overwrite it and trigger
   the next reload. `LoadFrom` locks its file for the process lifetime — without the shadow, the
   build-and-drop loop dies on a sharing violation and no reload ever fires.
3. **The `LoadFrom` context dedups by *simple name only*.** A fresh build under the same name comes back
   as the already-loaded generation even with a new `AssemblyVersion` — field-proven here as
   `LoadFrom deduped to already-loaded 1.2.7.42191`. Compile every build under a **unique assembly name**
   and republish under the fixed file name (§4.3), then **verify** `candidate.Location == the shadow
   path` and warn + fall back on a mismatch (`Harness/HotReload.cs:288-293,315-324`). A silent dedup
   re-applies old code while the log claims the new generation applied.

Also make the shadow path unique **per attempt**, not per generation: include the process id, the next
generation number *and* `DateTime.UtcNow.Ticks` in hex (`Harness/HotReload.cs:307-312`). `LoadFrom` caches
path → assembly, so a retried generation number would hand back the first (failed-context) attempt's
assembly without ever reading the newly dropped file.

### 4.3 Per-build assembly identity, in MSBuild

`csc` names the assembly after its **output file**, so the stamp must be the compile-time output name and
the fixed file name is restored afterwards
(`Payload/BLTDeploymentCrashGuard.Payload.csproj:9-24,28-36,92-97`):

```xml
<PayloadBuildStamp>$([System.DateTime]::UtcNow.ToString("yyMMddHHmmss"))</PayloadBuildStamp>
<AssemblyName>MyMod.Payload.b$(PayloadBuildStamp)</AssemblyName>
<Deterministic>false</Deterministic>          <!-- the wildcard silently does nothing without this -->
<AssemblyVersion>$(Version).*</AssemblyVersion>
<FileVersion>$(Version).0</FileVersion>       <!-- wildcards are ILLEGAL here, and it would inherit the one above -->
```

```xml
<Target Name="PublishFixedPayloadName" AfterTargets="Build">
  <Copy SourceFiles="$(TargetPath)" DestinationFiles="$(OutDir)$(PayloadFixedFileName)" />
  <Delete Files="$(TargetPath)" />
</Target>
```

Deleting the stamped copy keeps `bin/` unambiguous for the copy-to-game step. **Nothing may then depend
on the internal name**: the harness finds `PayloadEntry` by type name, the tests link source files, and
`SubModule.xml` lists only the harness.

**`InternalsVisibleTo` is matched by exact assembly name**, so it can never cover a stamped name. The
shared harness surface (`Log` / `Diag` / `GuardConfig` / `SelfHealing`) has to be `public`; keep the
attribute only for the fixed-name case (`Harness/AssemblyInfo.cs:1-9`). This is the .NET constraint that
forces the design, and it surprises everyone once.

### 4.4 Binding: pin the boundary assemblies, and dump evidence when it still fails

Install an `AppDomain.AssemblyResolve` handler that (a) returns `null` for your dynamically-named payload
family so it is never redirected, (b) **hard-pins every assembly whose types cross the interface** — here
`0Harmony`, because `IPayload.Apply` takes a `HarmonyLib.Harmony`, and the harness itself — to
`typeof(X).Assembly`, and (c) otherwise returns the first already-loaded assembly of that simple name,
logging `AMBIGUOUS` when more than one copy exists. Wrap the whole handler in an empty catch: a resolver
must never throw into the binder (`Harness/HotReload.cs:63,134-192`).

The two copies that can split here are concrete: the game bin's `0Harmony 2.4.2.0` and the
`Bannerlord.Harmony` module's `0Harmony 2.3.6.0`, of which the harness is bound to the latter
(`Harness/HotReload.cs:146-151`; `CHANGELOG.md:215-220`).

**Pinning is necessary but not sufficient.** `AssemblyResolve` only fires when probing *fails*, and
byte-loading probes successfully (against the wrong copy), so the pin can never run. Change the **load
context**; do not just add a resolver (`CHANGELOG.md:213-221`; `Harness/HotReload.cs:281-287`). The
resolver pin was in fact shipped first as the fix (`CHANGELOG.md:274-278`) and did not work, which is
why it is worth stating this way round. Generation 1 always worked, which masked
the bug for three releases — a path that works by accident on the first iteration hides the defect until
iteration N.

On a type-load failure, dump an **evidence pack**: the exception, the host assembly's identity and
location, the boundary assembly's identity and location with an explanation of why it matters, every
loaded copy of the names that could have split (annotated with `ReferenceEquals` markers), and the failing
assembly's own `GetReferencedAssemblies()` entries for those names
(`Harness/HotReload.cs:194-233,348`). It converts "the mod silently did nothing" into a log that answers
"who supplied the duplicate" with no decompiler and no debugger.

### 4.5 Generations: fresh statics, shared state, and a fail-safe swap

- **Fresh statics per generation are the reload-cleanliness contract.** Every per-session cache — config
  mode, mode latch, throttle ticks, the patch stash — is a plain static that resets on reload
  (`Payload/PayloadEntry.cs:8-11,19-21`; `Payload/BattleMode.cs:75-77,392-394`). That is why reload is
  clean; it is also the trap: **state that must survive a reload cannot live in a payload static.**
- **Harness-owned shared state.** Create one `ISharedState` in the stable layer and pass the *same*
  instance into every generation's `Apply`, for fire counts, the launch session id, and the foreign-patch
  stash (`Harness/Contracts.cs:25-37`; `Harness/SharedState.cs:6-48`; `Harness/HotReload.cs:36,367`).
- **Reset accumulating registries per generation, but keep the counters.** Clear the health lists and the
  self-test registry immediately before each `Apply` — otherwise they double on every reload and the
  health report is meaningless after gen2 — while deliberately keeping the fire counters, which prove
  shared state survived (`Harness/HotReload.cs:362-364`; `Harness/Diag.cs:63-69`;
  `Harness/SelfHealing.cs:94-106`).
- **Apply new, then unpatch old** (§2.12). A failed apply keeps the previous generation running.
- **Known gap, stated honestly**: `BattleMode`'s foreign-patch stash does not yet survive a reload, so
  reloading while BT's battle patches are lifted can leave them lifted (`HOTRELOAD.md:65-68`). If your
  design lifts foreign patches, that stash belongs in `ISharedState`.

### 4.6 Triggering a reload safely

The `FileSystemWatcher` callback only flips a `volatile bool` and stores `Environment.TickCount`; the
actual reload happens in the game's per-frame tick after ~400 ms of quiet, with a `now < _debounceTick`
clause for wraparound (`Harness/HotReload.cs:37,90-103,448-484`). **Harmony patching off the main thread
is unsafe**, and a single file save raises several watcher events — without the debounce you get repeated
reloads mid-build. Watch either the source directory (`*.cs`, `IncludeSubdirectories`) or the prebuilt
DLL depending on mode, with `NotifyFilter = LastWrite | Size | FileName` and `Changed`/`Created`/`Renamed`
all wired.

**Double-gate the dev capability.** Runtime code loading requires **both** `"hotReload": true` in
`guardconfig.json` **and** an empty `.hotreload-dev` marker file in the module root
(`Harness/HotReload.cs:27-29,69-71`; `Harness/GuardConfig.cs:111`; `HOTRELOAD.md:15-21`). Either alone
does nothing. A shipped config default can be flipped by a curious player or by copying someone's config;
a file only a developer would create cannot survive a normal install. This is the general pattern for any
dangerous dev-only capability in a shipped mod — a mod that can load arbitrary code from disk is a
code-injection surface.

The optional Roslyn path (§1.5) is gated behind the same two conditions plus a compile symbol, builds
`MetadataReference`s from every loaded assembly with a real `Location`, logs at most 15 error
diagnostics, and returns `null` on any failure so the caller falls back to the prebuilt DLL
(`Harness/PayloadCompiler.cs:3-11,25-105`; `Harness/HotReload.cs:71,415-432`).

### 4.7 Failing loudly when the mod is not actually active

Retry the payload load at multiple lifecycle points and, if it still is not loaded, show an in-game
message — `[Deploy Guard] CRASH GUARD NOT ACTIVE — payload failed to load, all fixes are OFF (see
CrashGuard.log)` — not just a log line (`Harness/HotReload.cs:261`, with the `[Deploy Guard]` prefix
added by `Harness/Log.cs:126`; the retry points are `Harness/HotReload.cs:247-263`). This exists because a whole session was played unprotected when the only
evidence of the failure sat in a file nobody opened. The same principle is recommended upstream for BT's
silent `BootstrapAborted` (`docs/UPSTREAM_CONTRIBUTION.md:68-70`).

**Cost of the design**: roughly 1–3 MB leaked per reload, because a .NET Framework assembly cannot be
unloaded — restart every few dozen reloads (`HOTRELOAD.md:63`). See §10.1.

---

## 5. Self-tests and health

By-name reflection degrades gracefully **and silently**. These two mechanisms are what make the silence
visible.

### 5.1 `RegisterTest` / `TestResult`

```csharp
SelfHealing.RegisterTest(SelfTest);

private static SelfHealing.TestResult SelfTest()
{
    // 1. re-resolve every member this guard depends on, by name, NOW
    // 2. exercise the pure decision logic against a truth table
    return SelfHealing.TestResult.Of("siege-cmd.contract", pass, detail);
}
```

The registry lives in the harness (`Harness/SelfHealing.cs:22-24,83-141`). With `"selfTest": true` in
`guardconfig.json` every registered test runs at startup and logs `[SELFTEST] PASS/FAIL <name> — <detail>`
plus a `[SELFTEST] N passed, M failed` summary, with an on-screen warning on any failure. A test that
throws is recorded as a `(threw)` FAIL rather than aborting the run. The registry is cleared before each
payload generation applies, so reloads do not accumulate duplicates (:94-106).

### 5.2 What a self-test worth having asserts

Both halves are runnable with **no campaign loaded** — which is the whole point, because the only inputs
testable outside a game are degenerate ones.

1. **Re-resolve, do not reuse.** Call the same `ResolveRoll()`/`ResolveTick()` helpers again at test time
   — "the resolve at Apply time is not reused" — otherwise the test passes forever on a `MethodInfo`
   cached from a game version that no longer exists (`Payload/IllnessDeathGuard.cs:136-148`;
   `Payload/ClanScreenCrashGuard.cs:73-74`; `Payload/MarriageBarterGuard.cs:114`;
   `Payload/DeadHeroReactivationFix.cs:159-172`; `Payload/StealthHideoutAdvisor.cs:122-125`;
   `Payload/ClanPartyCreationAdvisor.cs:328-337`).
2. **Invoke the patch body with a degenerate input and assert the fail-open contract.** Prefixes return
   `true` on a null hero/instance/barter; finalizers return `null` on a null exception and record no
   fire; destructive predicates return `false` on `default(TroopRosterElement)`
   (`Payload/IllnessDeathGuard.cs:143`; `Payload/MarriageBarterGuard.cs:115`;
   `Payload/ConversationCameraCrashGuard.cs:72`; `Payload/DeadHeroReactivationFix.cs:163,174`;
   `Payload/ClanScreenCrashGuard.cs:75-76`).
3. **Pin the decision table, not just the members.** `ShouldRefuseHandoff(...)` against a hand-written
   truth table (`Payload/SiegeCommandGuard.cs:523-553`); `JoinSyncPauseEscape.Decide` with all five rows
   including the three "never act" rows (`Payload/JoinSyncPauseEscape.cs:354-359`);
   `ClanModeSoloDecider.Decide(null, isHost:true) == false`, `Decide(false, isHost:false) == false`,
   `Decide(false, isHost:true) == true` (`Payload/ClanModeSoloFix.cs:105-119`);
   `ComputeBlockMs` at four boundary values (`Payload/BackgroundTickBudgetGuard.cs:143-156`);
   `CoopCommandSplit`'s host-I–IV / client-V–VIII block mapping (`CHANGELOG.md:45-46`).
4. **Pin values as well as names** — `(int)OrderType.AIControlOn == 36`
   (`Payload/SiegeCommandGuard.cs:534`).
5. **Pin the negative half.** The IL discriminator must find ≥1 patched lambda **and** ≥1 deliberately
   untouched lambda (`Payload/MapIncidentCrashGuard.cs:309-337`); a birth frame must not read as stash, a
   stash frame must not read as birth, and a real BT packet (first byte 13 = `PacketType.PlayerHeroData`)
   must match neither (`Payload/StashSync/StashSyncGuard.cs:479-483`;
   `Payload/PregnancySync/PregnancySyncGuard.cs:507-508`). "My packet parses" is the easy half.
6. **Prove invocability, not just non-null handles.** A non-null `MethodInfo` is not proof the signature
   still works — invoke a read-only query against live state and report which of member-resolution,
   invocation or logic failed, with `(BT update?)` in the detail
   (`Payload/JoinSyncPauseEscape.cs:339-364`).
7. **Test against real engine data where you can, creating nothing.** Take a live object
   (`Hero.MainHero`), serialize it as if it were the synced entity, run it back through the exact
   receive-path parser in loopback, and assert field-for-field equality — no object created, no network
   involved, real `StringId`s, real `BodyProperties` xml, real unicode names
   (`Payload/PregnancySync/PregnancySyncGuard.cs:486-523`; `Payload/StashSync/StashSyncGuard.cs:459-499`).
   Report PASS with an explanatory detail when the probe object is legitimately absent (main menu) rather
   than a false red.
8. **Register the test even when the feature is disabled** — `SelfHealing.RegisterTest` is called before
   the enabled check (`Payload/PregnancySync/PregnancySyncGuard.cs:48-49`;
   `Payload/StashSync/StashSyncGuard.cs:65`). A feature that has been off for weeks is exactly the one
   that has silently rotted; keeping its proof running means turning it on is not a leap of faith.

### 5.3 MOD HEALTH

`Diag.Report(component, ok, detail, critical)` accumulates two lists; `HealthSummary()` prints
`MOD HEALTH: N active` and, when anything is degraded, `, M NOT resolved -> <component> (<detail>); …
(likely a BannerlordTogether update renamed a method — check for a mod update)`, escalating to an
on-screen warning when a component marked `critical` is missing (`Harness/Diag.cs:63-104`, printed at
`Harness/HotReload.cs:380-381`). By-name reflection means an upstream rename produces a silently missing
fix; this line is the tripwire. It caught BT's `BannerlordTogether.Network.*` namespace move
(`CHANGELOG.md:130-132`).

Conventions that keep the board honest:

- **"Not applicable" is OK, not broken.** `BT not present` reports `ok = true` with detail
  `"no BT present"`; an unresolved BT/engine member reports a failure
  (`Payload/ClientBootstrapFix.cs:65,71,78,85`; `Payload/ClanModeSoloFix.cs:48,54,60`).
- **"Disabled by config" is OK.** Report `ok = true` with detail `"disabled by config"`, and log what
  vanilla will now do instead (`Payload/SiegeCommandGuard.cs:87-92`; `Payload/CoopCommandSplit.cs:72-77`).
  An intentionally-off guard is not a red — but resolve the targets **before** the config check so a
  game-update breakage is still reported for a feature the user may re-enable
  (`Payload/IllnessDeathGuard.cs:38-52`).
- **Report the count, not a bare boolean.** Where a guard patches a set of optional methods, make
  `ok = patched > 0` and print the count — "active on N method(s)" — in the guard's active log line
  (`Payload/ConversationCameraCrashGuard.cs:46-47`; `Payload/StealthHideoutAdvisor.cs:58-59`;
  `Payload/ClanPartyCreationAdvisor.cs:90-91`). Note what those three do *not* do: each passes an empty
  health detail on success, so a 1-of-3 partial resolve is visible in the log but indistinguishable from
  a full one on the board. Put the count in the detail as well if you want the board to show the
  difference.
- **Announce a degraded scope explicitly.** When the optional role-controller members do not resolve, the
  siege guard still applies its six core patches and logs "owner-is-general promotion limited to
  `Team.SetPlayerRole`", with `role controller unresolved` in the health detail
  (`Payload/SiegeCommandGuard.cs:118-126,132`). Partial capability beats all-or-nothing, and the report
  says *which* leg is missing. The same guard's other optional leg is quieter and shows the cost:
  `PatchBtReleases` logs a per-name miss ("BT release method not found (BT update?): " + name,
  `Payload/SiegeCommandGuard.cs:185-186`) but nothing about it reaches `Diag` — the health detail carries
  only `roleHooked`, and the `_btPatched` count goes to the log line alone (:129-132). A missing BT
  release hook is therefore invisible on the board.
- **Print what actually resolved** in the active line — lambda count, `consequence=`, `invokeOption=`
  (`Payload/MapIncidentCrashGuard.cs:103-110`).

### 5.4 Fire tracking: separating "wired" from "working", and knowing when to retire

`Diag.Report` says *this guard installed, and how completely*. `SelfHealing.RecordFire(component)` says
*this guard actually did something* (`Harness/SelfHealing.cs:9-14,43-81`). Both are used on most guards,
and the pair is what makes the health board diagnostic:

| Health | Fires | Meaning |
|---|---|---|
| active | ≥1 | The bug is still present; the guard is earning its place |
| active | 0 across sessions | The upstream bug may be fixed — candidate for retirement |
| NOT resolved | 0 | Drift: a member was renamed or moved. Fix the resolution |

The ids in use here are `setup-teams-guard`, `finish-deployment-guard`, `party-ai-guard`,
`encounter-loop-guard`, `map-incident-guard`, `bg-tick-budget-guard` and one per gameplay fix
(`Payload/DeploymentCrashGuards.cs:22,43`; `Payload/PartyAiCrashGuard.cs:110,139`;
`Payload/EncounterLoopGuard.cs:121`; `Payload/MapIncidentCrashGuard.cs:227,242,285,300`;
`Payload/BackgroundTickBudgetGuard.cs:128`).

**The exceptions are worth knowing, because the table above cannot be read for them.** Twelve guards and
fixes here record fires but never call `Diag.Report` (and never `SelfHealing.RegisterTest`):
`DeploymentCrashGuards`, `PartyAiCrashGuard`, `EncounterLoopGuard`, `MapClickSpeedKeeper`,
`ClientHeroCreationGuard`, `MovementOrderTypeInitGuard`, `PlayerIdentityGuard`, `TimeEnforcementGuard`,
`TimeFlowPatch`, `ShareTimeControl`, `BattleMode` and `BootstrapWatch`. Four of the six ids listed above
— `setup-teams-guard`, `finish-deployment-guard`, `party-ai-guard` and `encounter-loop-guard` — belong to
that set, including both flagship deployment crash finalizers, so a drift in them shows only as a log
line and never as `NOT resolved` on the board. The convention stated in `CLAUDE.md:71-73` is
aspirational rather than enforced; if you adopt this pattern, know which of your own guards are still
invisible to it.

Safety-net logs label themselves "root-fix candidate", which
turns a symptom suppressor into an instrument: the log lines become the evidence for the upstream bug
report and for deciding which net to promote to a real fix.

**Behaviour patches need a probe, not just a counter.** Any patch that changes behaviour (rather than
merely swallowing a crash) must test for the bug signature before acting and stand down when upstream has
fixed it; register those probes so the health report shows them (`Harness/SelfHealing.cs:15-21`;
implemented at `Payload/ClientBootstrapFix.cs:155-170`). Otherwise your "fix" silently reintroduces the
wrong behaviour the day the upstream mod fixes its own bug.

**Log the healthy case too.** A guard that only logs when it repairs something cannot tell you whether it
was unnecessary or absent. `StealthHideoutAdvisor` asserts two ownership invariants, counts repairs,
records a fire **only** when `repaired > 0`, and still logs the clean outcome ("already general +
order-controller owner") (`Payload/StealthHideoutAdvisor.cs:81-118`).

### 5.5 Headless tests for the parts that do not need a game

Bannerlord modding has no test harness, but the riskiest code — parsing hostile bytes — does not need
one. Keep the serialization type in a file with **zero** engine dependencies and `<Compile Include>` the
**shipping source file** into a plain `net472` console project — not a copy:

```xml
<Compile Include="..\..\Payload\PregnancySync\BirthPayloadData.cs" />
<!-- so a wire-format change that breaks round-trip fails this test, not a stale copy -->
```

(`Payload/PregnancySync/BirthPayloadData.cs:9-12`; `tests/BirthPayloadTest/BirthPayloadTest.csproj`;
`tests/StashPayloadTest/StashPayloadTest.csproj` links all four wire files because cross-discrimination
is part of the contract.) That structurally prevents the stale-copy failure mode.

**Derive a test's framing constants from behaviour** instead of duplicating them: the corrupt-body test
computes `headerLength = framed.Length - source.ToBytes().Length` rather than hard-coding `5`
(`tests/BirthPayloadTest/Program.cs:128-141`). A test that re-states a production constant silently stops
testing what it claims the day the constant changes.

What headless tests cannot cover is stated plainly in the changelog: the wire format and loopback are
proven; the two-machine hop is validated live on first fire (`CHANGELOG.md:202-203,306-307`).

---

## 6. Logging and diagnostics

### 6.1 Tags, two channels, and one-shot latches

Every guard logs under its own bracketed **tag** — `[SIEGE-CMD]`, `[GATE]`, `[INCIDENT-GUARD]`,
`[TICK-GUARD]`, `[MO-INIT]`, `[HOTRELOAD]`, `[SELFTEST]` — so a 20 MB log is greppable by subsystem
(`CLAUDE.md:71-73`). Two channels:

- `Log.Info` → the file, timestamped and role-tagged (`Harness/Log.cs:62-76`).
- `Log.Screen` → one short plain-language line the player can act on
  (`Harness/Log.cs:122-131`): "your sickness was cured", "marriage barter cancelled BEFORE any gold
  moved", "prevented a deployment-setup crash (details in … CrashGuard.log)"
  (`Payload/IllnessDeathGuard.cs:125-126`; `Payload/MarriageBarterGuard.cs:89-90`;
  `Payload/DeploymentCrashGuards.cs:24,78`).

Silent intervention makes a mod indistinguishable from a bug; screen spam is worse. So every
player-visible line is gated by a change latch or a one-shot flag — mode flips and SAFE MODE only
(`Payload/BattleMode.cs:174,222`; `Payload/PayloadEntry.cs:34`), one summary per battle for a
behaviour-changing guard (`Payload/SiegeCommandGuard.cs:441-445`; `Payload/CoopCommandSplit.cs:381-391`).
The same latch discipline applies to the file log for anything on a hot path: `_rollBlockLogged` makes a
per-day prefix log exactly once with the wording "logged once, active every day"
(`Payload/IllnessDeathGuard.cs:32,87-92`), and `_primeLogged`, `_standDownLogged`, `_forcingLogged`,
`_guidanceLogged`, `_warned` and "log only when verdict != lastVerdict" do the same elsewhere
(`Payload/ClientBootstrapFix.cs:160-183`; `Payload/ClanModeSoloFix.cs:145-151`;
`Payload/CoopHeroIdentityLock.cs:150-154`; `Payload/BootstrapWatch.cs:31-34`).

### 6.2 TraceThrottle: coalescing without losing the first instance

A guard that blocks a write which a peer mod retries every tick produces a ~60 Hz stack-trace flood that
fills an 8 MB log in minutes and rotates the real evidence off the end — the tracer destroys the thing it
exists to capture (`CHANGELOG.md:4-12`). The fix is a keyed throttle
(`Payload/TraceThrottle.cs:20-93`):

- The **first** occurrence of a key logs in full, with its stack.
- Identical repeats are counted and flushed as `[repeat] <key> ×N in Ys (collapsed)` at most once per
  5 s window.
- **The key deliberately omits the volatile part** (the stack), so repeats actually collapse
  (`Payload/TimeTrace.cs:92-95,121-123`); keys are built from the cheap identity of the event — exception
  type + first game frame (`Payload/CharacterCreationTrace.cs:177`).
- Key cardinality is bounded (`MaxKeys = 512`, `Clear()` on overflow) and the window check carries the
  wraparound guard (:63-65).

The simpler coalescing shape, where a full throttle is overkill: count every event and emit at most one
line per N ms **including the number since the last report** — plus running totals, so nothing suppressed
is lost (`Payload/PartyAiCrashGuard.cs:149-167`; `Payload/BackgroundTickBudgetGuard.cs:129-136` carrying
worst-ms and total throttled calls; `Payload/SiegeCommandGuard.cs:299-301,324-328,512-521` carrying
"N hand-off(s) refused, M troop shuffle(s) stopped").

### 6.3 Change-only, flip-only, whitelist

For naturally sticky values, filtering beats throttling — and then the *appearance* of a line is itself
the signal (`Payload/TracePatches.cs:103-177,191-202`; `Payload/ControlTrace.cs:91-98,139-154`;
`Payload/RoleTrace.cs:82-93`; `Payload/PayloadEntry.cs:189-209`):

- log a value only when it **changes** (compare against the last emitted string);
- log a setter only when it actually **flips**;
- log only the **interesting** enum value;
- log only when the **main party** (or the player's own clan) is involved — a daily per-couple engine
  callback fires thousands of times across the world, and scoping it to `Hero.MainHero.Clan` is what makes
  the tracer usable instead of a log bomb (`Payload/PregnancySync/PregnancySyncGuard.cs:188-191`);
- and combine both layers where the value is both sticky and hot: a 2-minute time throttle **and** an
  equality check against the previously emitted string (`Payload/PayloadEntry.cs:189-209`).

**Log the deliberate non-intervention.** A destroyed gate is left alone on purpose, but with tracing on a
30 s-throttled line says so: "gate is DESTROYED — vanilla does not allow closing a broken gate (no prompt
is correct here)" (`Payload/SiegeGatePromptFix.cs:70-81`). A carve-out that says nothing looks like a
broken fix to the next investigator.

### 6.4 Seeing exceptions the game hides

Subscribe to `AppDomain.CurrentDomain.FirstChanceException`, filter to exceptions that have a game frame
(`SandBox` / `StoryMode` / `TaleWorlds`, excluding `TaleWorlds.Library` churn), skip your own namespace,
cap total emissions, and route every line through the coalescing emitter
(`Payload/CharacterCreationTrace.cs:19-27,144,152-196`). This is the only way to name a **swallowed**
exception — a bug that produces a visual defect with no crash and no log line, and it works when the crash
reporter writes nothing.

Three rules make it safe:

1. **A `[ThreadStatic]` re-entrancy flag, set on entry and cleared in a `finally`, with an early return
   when already set** (`Payload/CharacterCreationTrace.cs:35-36,152-196`). A first-chance handler that
   throws re-enters itself — this is the difference between a diagnostic and an instant stack overflow,
   and `[ThreadStatic]` keeps it correct across the game's many threads.
2. **A catch-all around the whole handler body.** A tracer must never take the game down.
3. **A bounded walk of the inner chain**: `for (Exception cur = ex; cur != null && depth < 8; cur =
   cur.InnerException)`, printing each type, message and its own trimmed frames prefixed `<- INNER:`
   (`Payload/CharacterCreationTrace.cs:198-215`). A `TypeInitializationException`'s real cause is
   **always** its inner; printing only the outer is what made the 2026-09-04 crash undiagnosable for so
   long.

**Arm a narrow window when you can — and know which you actually shipped.** A window scoped to the
activity under investigation is the cheaper design. The `[CHARGEN]` observer here is *not* that: `Apply`
calls `Arm()` unconditionally, `Arm()` subscribes once via an AppDomain slot and never unsubscribes, and
the cap is a session total, not a per-activation one — `FirstChanceCap = 400` against a
`_firstChanceEmitted` counter that is only ever incremented (`Payload/CharacterCreationTrace.cs:33,46,
133-144,172-176`; the header says so at :19-27, and the active line logs "session-wide first-chance
exception capture ARMED"). It is filtered to game frames and coalesced by exception type + throwing
frame, and the cap is what keeps a session-wide observer safe (§6.6). If you want the narrow window,
arm and disarm it explicitly; do not assume a tracer is narrow because it was written for one screen.

**One handler across hot reloads.** Before subscribing to an AppDomain event, check
`AppDomain.CurrentDomain.GetData("BLTCG_FirstChanceArmed")`; if unset, `SetData` then subscribe
(`Payload/CharacterCreationTrace.cs:31,127-150`). Because the slot lives in the AppDomain rather than in
the reloaded assembly's statics, a new payload generation sees the previous generation's arming —
otherwise every reload adds another handler and each exception is logged N times.

### 6.5 LIVE stack versus exception stack, and reading Harmony frames

Capture `new StackTrace(skip, false)` at the instant of the throw — the currently executing frames — **in
addition** to the exception's own `StackTrace`, and label those lines `LIVE`
(`Payload/RuntimeDiagnostics.cs:159-196`, consumed at `Payload/CharacterCreationTrace.cs:185` and
`Payload/MovementOrderInitProbe.cs:66,86`). The exception's stack is truncated to the throw point and,
for a cached type-init re-throw, is the *original* stack from a different moment; only the live stack
shows who triggered it now.

When filtering frames, drop those whose `DeclaringType` starts with `HarmonyLib`, your own namespace, or
`System.` — but **keep frames whose `DeclaringType` is null**. Those are Harmony's `DMD<Namespace.Type::
Method>` dynamic methods, and they name the patched original that made the call, which is how you
attribute a call to another mod's patch (`Payload/TracePatches.cs:242-290`;
`Payload/ControlTrace.cs:347-392`; `Payload/TimeTrace.cs:166-212`;
`Payload/CharacterCreationTrace.cs:248-272`). Filtering turns a 60-frame plumbing stack into ~10
meaningful lines while preserving the single most valuable one.

### 6.6 State context, memory heartbeat, and never becoming the crash

- **`SafeGet<T>(Func<T>)`** — a generic wrapper returning `default(T)` on any exception, used for every
  property read in a state dump, plus per-property `try/catch` in snapshots
  (`Payload/ControlTrace.cs:299-345`; `Payload/RuntimeDiagnostics.cs:108-157,198-206`). During deployment
  or a mission transition engine getters throw; without this, a diagnostic dump becomes the crash it was
  meant to explain.
- **A memory + state heartbeat** every 15 s while tracing is on, plus a `Mark(label)` to force a labelled
  line at mission init, battle start or a stage change (`Payload/RuntimeDiagnostics.cs:25-60`). It is
  gated on the tracing flag so a normal player's session never carries it.
- **Universal `object[] __args` prefixes** let one hook body trace a method whose signature you do not
  know or that varies across builds; a small `FormatArgs` helper `ToString()`s each argument, prints
  `null`, truncates at 80 chars, and falls back to `<TypeName>` if `ToString` throws
  (`Payload/TracePatches.cs:86-149,206-240`; `Payload/CoopBattleTrace.cs:96-126`;
  `Payload/RoleTrace.cs:100-110`; `Payload/CharacterCreationTrace.cs:99-114`).
- **`MethodBase __originalMethod`** lets one prefix and one finalizer serve five different lifecycle
  methods, each logging which fired (`Payload/CharacterCreationTrace.cs:41-45,94-97,116-123`).
- **Bound everything.** Stack frames (14–20 kept), argument text (80 chars), total first-chance emissions
  (400), ctor probes (12), throttle keys (512), upload size (last 2 MB), heartbeat 15 s, role snapshot at
  most 1/s, upload at most 1/60 s (`Payload/TracePatches.cs:228-231,279`; `Payload/ControlTrace.cs:381`;
  `Payload/CharacterCreationTrace.cs:33,205,267`; `Payload/MovementOrderInitProbe.cs:28`;
  `Payload/RuntimeDiagnostics.cs:29,186`; `Payload/TraceThrottle.cs:31-32`;
  `Payload/LogStreamer.cs:101,132`). Every diagnostic in a game process is one unbounded loop away from
  being the bug; caps are what make it safe to ship tracers to a player.

### 6.7 The log as the oracle

Every component prints exactly what it managed to instrument — "tracer active on N method(s)",
"type not found: X", "no patchable method X" — and the `MovementOrder` guard prints which of two competing
hypotheses the run confirmed (`Payload/TracePatches.cs:46,69,74`; `Payload/ControlTrace.cs:45,56,79`;
`Payload/CoopBattleTrace.cs:46,63,84`; `Payload/RoleTrace.cs:61`;
`Payload/MovementOrderTypeInitGuard.cs:64-71,108-111,128`). With by-name reflection everywhere, a silent
hook miss looks identical to "the bug did not happen"; printing the resolved count converts a silent
failure into a visible one.

**A one-line snapshot of the decision inputs** at the moment a decision flips — `isClient`, `isHost`,
`server`, peer count, `recentPackets` — means a misjudged heuristic can be explained from the log without
a repro under a debugger (`Payload/BattleMode.cs:418-454`, consumed at
`Payload/TimeEnforcementGuard.cs:160`).

### 6.8 Making other mods' vetoes visible

Harmony runs **all** prefixes even when one returns `false`, so a prefix alone always looks like the
change happened. Two-phase capture fixes that: the prefix stores old/new value and the stack in
`[ThreadStatic]` fields and sets a pending flag (skipping no-op sets); the postfix re-reads the live value
and, if it differs from the requested one, appends
`^ change SUPPRESSED/ALTERED by another patch — actual mode now X` before emitting
(`Payload/TimeTrace.cs:83-128`, with the Harmony fact recorded at :20-22). It is the only practical way
to see "someone else blocked this" in a multi-mod Harmony stack.

### 6.9 Multi-machine correlation

A co-op bug is only diagnosable when host and client logs are read side by side.

- **Role tag on every line.** A throttled tick computes host/client/solo (H/C/S) from the tri-state peer
  probes and pushes it into the harness logger, so every subsequent line is attributable
  (`Payload/PayloadEntry.cs:161-187`; `Harness/Log.cs:32-39,69`).
- **Topology suffix on co-op trace lines.** Append a compact snapshot
  (host/client/dedicated/localPlayers/inSpBattle/battleId) to **every** line a co-op tracer emits, instead
  of logging topology separately (`Payload/CoopBattleTrace.cs:149-172`). Interleaved logs from three
  processes can then be reasoned about without cross-referencing an earlier state line.
- **Zero-touch log streaming.** Upload only the log **tail** over `HttpWebRequest` with `bin`/`filename`
  headers, on a ThreadPool worker, rate-limited and skipped when the file has not grown, named
  `blt-<RoleTag>-<MachineName>.log` (`Payload/LogStreamer.cs:92-182`).
- **One-command diagnostics bundle for players.** `collect-diagnostics.cmd` stages `CrashGuard.log`, BT's
  `bt-sync-*.txt` and the newest crash report (each copy `>nul 2>&1` so missing files are skipped), zips
  them, POSTs to an anonymous host, checks `findstr /b "https://"`, falls back to a second host, and puts
  the URL on the clipboard — printing the local path if both uploads fail
  (`collect-diagnostics.cmd:26-68`; `share-log.cmd:41-71`). The three artefacts needed to diagnose
  anything here come from two mods and the game; one command removes the step players get wrong.

### 6.10 When nothing throws

Exception tooling is useless for a hang. Attach a live debugger to the running process and take repeated
managed stack samples of the main thread; the common frames are the culprit. That is how the 2026-08-30
whole-game freeze was root-caused — every sample landed inside BT's `TryBackgroundCampaignTick`
(`UPSTREAM_BUG_REPORT.md:135-146`; `CHANGELOG.md:230-233`).

For flows rather than freezes, **patch native methods with pure logging patches** to capture the real call
order and timestamps, and add an external guard that *holds* a step (90 s) to prove an expected event
never arrives — that is what proved the player agent never spawned in the village-raid empty-roster case
rather than assuming it (`UPSTREAM_BUG_REPORT.md:74-84`).

### 6.11 Rotation: do not destroy the evidence you are collecting

Cap the live log at 8 MB and keep **six** rotated segments (`.1` newest … `.6` oldest, ~48 MB of history)
by delete-oldest-then-shift-down, and re-check the size every **256 writes** rather than once per launch
(`Harness/Log.cs:13-15,78-120`). Both numbers are scar tissue: a single hot tracer can burn an 8 MB cap in
minutes, and with only one backup the flip destroys the session being chased; a once-per-session check let
the file reach 283 MB because the only check ran while it was still small.

### 6.12 Log what an upstream report will need

Make each repair emit a line an upstream maintainer can act on —
`[INCIDENT-GUARD] REPAIRED … (co-op army attach gap)`, `[TICK-GUARD]` with the measured cost per fire
(`UPSTREAM_BUG_REPORT.md:124-127,159-161`). A local workaround that also emits a frequency/severity
dataset is worth far more upstream than a bug report alone.

**Keep diagnostics separate from the feature flag.** Conception visibility is installed regardless of the
`pregnancySync` flag because it is diagnostic, not sync — so "did the roll happen?" is always answerable
from the log even with the feature off (`Payload/PregnancySync/PregnancySyncGuard.cs:50-53`). Turning a
feature off should not blind you to the game behaviour you will need in order to debug it.

---

## 7. Configuration

A Bannerlord mod's config file is not a nicety. It is the off switch a player reaches for when your
mod is suspected, the A/B lever a developer needs to bisect two mods, and the only override available
when a heuristic (§3.3) guesses the safe side wrongly. It has to work with no dependencies, no UI,
and no support call.

### 7.1 A JSON reader with no JSON dependency

**What.** `GuardConfig` reads `guardconfig.json` with two anchored regexes and caches the file text
for the session (`Harness/GuardConfig.cs:26-48,50-64,66-80`):

```csharp
public static bool Bool(string key, bool fallback)
{
    Match m = Regex.Match(Text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)",
                          RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                     : fallback;
}
```

`String(key, fallback)` is the same shape against a quoted value. Both wrap in `try/catch` and return
the fallback, and `Text` returns `""` if the file cannot be read at all — so **every failure path is
the shipped default**, never an exception during module load.

**Why it matters.** The module loads into a process that already contains ButterLib, Harmony and every
other mod's dependency graph. Adding a JSON parser adds one more assembly that can bind-conflict — the
same class of problem that keeps Roslyn out of the shipped build (§1.5). Two regexes have no identity,
no version and nothing to conflict with.

**Know what you gave up.** The reader is flat (no nesting), ignores the surrounding structure entirely,
and resolves a duplicated key to the first match. That is acceptable for a settings file whose schema
you own, and the same shape scales down to a tiny persistent key/value map when you pin the round trip
with a test (§8.6). It is not a parser; do not feed it data you did not write.

Note `Regex.Escape(key)` — a key containing a regex metacharacter would otherwise match the wrong thing
or throw.

The path comes from the assembly location plus `../..` (§1.3), so the file sits in the module root
beside `bin/`, where a player can find it (`Harness/GuardConfig.cs:17-24`).

### 7.2 Write the default file on first read, and document every knob inside it

`Text` writes `DefaultJson` when the file is absent and then reads it back
(`Harness/GuardConfig.cs:35-39`). The default is not a skeleton — every knob carries a sibling
`"_<key>"` doc string:

```json
"battleMode": "auto",     "_battleMode": "auto | solo | coop. auto = vanilla battles when hosting alone, co-op sync when a peer is connected",
"safeMode": false,        "_safeMode": "true disables ALL guards/fixes/tracers (isolate whether this mod or BannerlordTogether is the cause)",
```

(`Harness/GuardConfig.cs:82-115`.) The `_`-prefixed twin is inert — its key never matches a lookup — so
the documentation lives inside the artefact the player already has open, and cannot drift away from the
file the way a wiki page does. The header line says how to get back to a known state: *"Delete this file
to regenerate defaults."*

**Say what vanilla does when the flag is off.** The doc strings for `siegeCommandAll`,
`coopOwnArmyCommand` and `partyTroopsOnCreate` each describe the *unmodded* behaviour the player gets by
turning the knob off (`Harness/GuardConfig.cs:98-102`). A description that only says what turning the
flag on does gives the player nothing to decide with.

### 7.3 The fresh-read pattern for a flag that must be flippable mid-session

The session cache in §7.1 is right for almost everything and wrong for exactly one case: a flag you
want to change **during** a live repro, where restarting the game destroys the state you were chasing.
For those, read the file again and fall back to the cached accessor:

```csharp
private static bool FreshTracingFlag()
{
    try
    {
        string text = File.ReadAllText(GuardConfig.Path);
        var m = Regex.Match(text, "\"tracing\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
    catch { }
    return GuardConfig.Bool("tracing", false);
}
```

(`Payload/PayloadEntry.cs:211-231`, gating all six tracers at :81-92.) Combined with hot-reload (§4),
this is what makes "turn tracing on, now, without losing the repro" possible: edit the JSON, drop a
payload build, and the next generation reads the new value.

Guards that own one flag do the same thing with their own narrow regex rather than routing through the
harness — `Payload/TimeFlowPatch.cs:81-100`, `Payload/ShareTimeControl.cs:191-209`,
`Payload/BattleMode.cs:349-382`, `Payload/LogStreamer.cs:44-73`. The rule for all of them: **the fresh
read is best-effort, and its failure path is the cached or default answer**, never a throw and never a
silent behaviour change.

### 7.4 Decide the direction of an unreadable config, deliberately

Two shapes are in use here, and the difference is the point:

| Shape | Code | An absent, unreadable or misspelt key means |
|---|---|---|
| Symmetric match with an explicit fallback | `GuardConfig.Bool(key, fallback)` | the fallback the call site names |
| Off-only match | `Regex.IsMatch(text, "\"timeAlwaysFlows\"\\s*:\\s*false")` → `false`, else `true` | **on** — the shipped behaviour |

(`Harness/GuardConfig.cs:50-64`; `Payload/TimeFlowPatch.cs:81-100`;
`Payload/ShareTimeControl.cs:191-209`.) The off-only shape says "this feature is on unless the file
explicitly says otherwise", so a truncated or half-edited config cannot silently disable it. Either is
defensible; what is not defensible is not knowing which one you wrote.

The same reasoning applies to a value key. `battleMode` is matched as
`"battleMode"\s*:\s*"(auto|solo|coop)"` (`Payload/BattleMode.cs:363`), so a misspelt mode does not match
at all and the code falls through to `auto` with a logged reason — rather than to a mode nobody
implemented.

**Migrate a renamed key in place.** When the current key is missing, look for the previous release's key
and translate it, logging the migration: `soloVanillaBattles=false` ⇒ `battleMode=coop`
(`Payload/BattleMode.cs:369-374`). A player who configured your mod once keeps the behaviour they asked
for across an update without touching the file.

### 7.5 Off-switch semantics: one global kill switch, one flag per feature

**The global kill switch runs before anything is installed.** `safeMode` is checked at the top of
`PayloadEntry.Apply` — before the load-time guard, before `PatchAll`, before every other `Apply` — and
it announces itself on screen so nobody can be running in it by accident:

```csharp
if (GuardConfig.Bool("safeMode", false))
{
    Log.Info("SAFE MODE — all guards/fixes/tracers DISABLED via guardconfig.json safeMode=true.");
    Log.Screen("SAFE MODE active — this mod is doing nothing (guardconfig.json)");
    return;
}
```

(`Payload/PayloadEntry.cs:31-36`.) That answers "is it this mod or the other one?" in a single launch.
The alternative players reach for — deleting the module — also deletes the log you need and changes the
launcher's load order for everything else.

**Every behaviour-changing feature owns a flag**, defaulting to the behaviour that ships:

| Key | Default | Owner |
|---|---|---|
| `safeMode` | `false` | `Payload/PayloadEntry.cs:31` |
| `battleMode` | `"auto"` | `Payload/BattleMode.cs:79-85` |
| `timeAlwaysFlows` / `shareTimeControl` | `true` | `Payload/TimeFlowPatch.cs:81-100`, `Payload/ShareTimeControl.cs:191-209` |
| `noSickness` | `true` | `Payload/IllnessDeathGuard.cs:38` |
| `pregnancySync` / `stashSync` | `true` | `Payload/PregnancySync/PregnancySyncGuard.cs:47`, `Payload/StashSync/StashSyncGuard.cs:64` |
| `partyTroopsOnCreate` | `true` | `Payload/ClanPartyCreationAdvisor.cs:63` |
| `coopOwnArmyCommand` / `siegeCommandAll` | `true` | `Payload/CoopCommandSplit.cs:72`, `Payload/SiegeCommandGuard.cs:87` |
| `myHero` | `""` | `Payload/CoopHeroIdentityLock.cs:129` (§8.6) |
| `tracing` / `selfTest` | `false` | `Payload/PayloadEntry.cs:231,103` |
| `logStreamBin` | `""` | `Payload/LogStreamer.cs:58-68` |
| `hotReload` / `hotReloadRoslyn` / `payloadSourceDir` | `false` / `false` / `""` | `Harness/HotReload.cs:70-72` (§4.6) |

Three conventions hold across all of them:

- **Resolve the targets before the config check**, so a game update that broke a member is still
  reported for a feature the player has switched off and may switch back on
  (`Payload/IllnessDeathGuard.cs:38-52`).
- **"Disabled by config" is a healthy state**, reported `ok = true` with detail `"disabled by config"`,
  plus a log line saying what vanilla will now do instead (§5.3; `Payload/SiegeCommandGuard.cs:87-92`;
  `Payload/CoopCommandSplit.cs:72-77`).
- **Register the self-test anyway** (§5.2 item 8), so a feature that has been off for a month has not
  silently rotted.

**Diagnostics are not a feature flag.** Conception visibility installs regardless of `pregnancySync`,
because it answers a question about the *game*, not about the sync
(`Payload/PregnancySync/PregnancySyncGuard.cs:50-53`; §6.12).

**A second, simpler file can be the right answer for one value.** The log-stream bin id is read from
`logstream.txt` in the module root first and from `guardconfig.json` second
(`Payload/LogStreamer.cs:44-73`), because the installer can write a one-line file from an environment
variable without parsing or rewriting the player's JSON (`install.cmd:62-65`).

Finally, config is the escape hatch for every heuristic in §3.3: when the tri-state's safe direction is
the wrong direction for this player, `battleMode=solo` ends the argument.

---

## 8. Co-op patterns

Everything here is about adding behaviour to **someone else's** multiplayer mod: no compile-time
reference to it (§3), no control over its threads, no guarantee that both machines run the same version
of anything, and a player on the other end who will blame whichever mod is most recently installed. The
patterns below are what survived contact with that.

### 8.1 Scope every co-op behaviour by role

Three roles matter — host, client, solo — and every co-op behaviour opens with a role gate. The stash
send path is the canonical shape (`Payload/StashSync/StashSyncGuard.cs:148-157`):

```csharp
bool isHost   = PeerDetection.ReadCoopStaticBool("IsHost") == true;
bool isClient = PeerDetection.IsClient() == true;
if (!isHost && !isClient) return;                                     // no session — vanilla needs no sync
if (isHost && PeerDetection.AnyRemotePeerConnected() != true) return;  // hosting alone — nobody to tell
```

The two comparison styles are deliberate and pull in opposite directions (§3.3): `== true` means "act
only on a confident yes", `!= true` means "do not proceed unless confidently connected". Reading them as
plain booleans is how an unknown becomes a wrong answer.

The other two shapes in use:

- **Host-only authority.** The birth broadcaster returns unless `IsHost` is a confident `true` — "only
  the host is authoritative for births" — and again unless a peer is connected
  (`Payload/PregnancySync/PregnancySyncGuard.cs:247-257`).
- **Client-only stand-down.** The hero-identity lock refuses to run as a client at all, because the
  peer mod assigns the client's hero through its own claim flow
  (`Payload/CoopHeroIdentityLock.cs:83-86`).

**Compute the role once, cheaply, and put it on every log line.** A throttled tick (at most every 5 s)
derives H/C/S from the same tri-state probes and pushes it into the harness logger, so three machines'
logs can be read side by side (`Payload/PayloadEntry.cs:161-187`; §6.9).

**Identify the *remote* player by several keys, not one.** Anything that rearranges troops has to know
which agents are player bodies — the local one and the peer's — and the peer mod's id for a remote hero
may name either a `Hero` or a `CharacterObject`. So `IsPlayerHeroAgent` accepts `agent == Agent.Main`,
`hero == Hero.MainHero`, **or** either `hero.StringId` or `character.StringId` matching the peer's ghost
id, and the ghost lookup itself tries `MBObjectManager` as a `Hero` first and then as a
`CharacterObject.HeroObject` (`Payload/CoopCommandSplit.cs:299-323,350-361`). Missing the remote hero
here would shuffle a player's own body into another formation — a much worse outcome than not
recognising an NPC, so the identity test is deliberately generous while the *action* stays narrow (each
player's troops move into their own formation block; player heroes are never moved).

### 8.2 Deciding whether a session is live

Detection itself is §3.3 (the tri-state) and §3.4 (observed traffic beating declared state). Two
consequences belong here:

- **Auto-detect the mode, then name the fail-safe direction.** `battleMode=auto` runs vanilla battles
  while hosting alone and co-op battle sync once a peer is connected, and an unknown reading fails
  *toward* co-op, because being wrong in the other direction sabotages a live session
  (`Payload/BattleMode.cs:107-144`). Removing a config the player would get wrong is worth more than
  the config.
- **Ship the override anyway.** `battleMode=solo` exists precisely for the session where the safe
  direction is the wrong one (§7.5).

### 8.3 Riding the peer mod's network channel

**What.** Carry your own packets on the other mod's existing transport instead of opening a socket:
prepend a marker byte the host protocol provably ignores, then a per-feature magic, then your payload.

**The free byte, and why it is provably free.** BannerlordTogether dispatches on the first byte
(`(PacketType)data[0]`); the enum uses every value 1..255; byte 0 is its empty-packet sentinel, and its
receive path already rejects zero-length packets while the dispatch switch has **no case for 0 and no
`default`**. So a non-empty packet whose first byte is 0 is a guaranteed no-op inside BT *even if your
interception ever missed it* — safe twice over (`Payload/PregnancySync/BirthWireFraming.cs:5-19`; the
IL evidence is `docs/BT-INTERNALS.md`). The general lesson: **derive the safety argument from the host
protocol's own code, not from the absence of a collision in testing.**

**One marker, many features.** The frame is `[0x00 marker][4-byte magic][payload]`, and each feature
gets its own magic — `BTCG` for births, `BTCS` for stash
(`Payload/PregnancySync/BirthWireFraming.cs:21-25`; `Payload/StashSync/StashWireFraming.cs:15-20`).
Each feature's receive hook recognises exactly its own magic and passes everything else through, so
feature N+1 costs one constant and cannot break feature N — and the property is *tested* in both
directions rather than assumed (§5.2 item 5).

**Consuming a packet is a prefix that answers the gate and cancels it.** Patch the peer mod's
accept-gate, set `ref bool __result = false` **and** return `false`, so the original never runs and the
caller sees "reject" — the peer mod neither enqueues nor dispatches your bytes:

```csharp
private static bool ShouldAcceptIncomingPacketPrefix(byte[] data, ref bool __result)
{
    try
    {
        if (!_enabled || !StashWireFraming.IsOurPacket(data)) return true;   // not ours — let BT decide
        StashPayloadData payload = StashWireFraming.TryUnframe(data);
        if (payload != null) { lock (QueueLock) { Pending.Enqueue(payload); } }
        else Log.Info("[STASH-SYNC] received a malformed stash packet — dropped");
        __result = false;                                                    // consume
        return false;
    }
    catch (Exception ex) { Log.Info("[STASH-SYNC] receive error: " + ex.Message); return true; }
}
```

(`Payload/StashSync/StashSyncGuard.cs:238-266`; `Payload/PregnancySync/PregnancySyncGuard.cs:316-345`.)
Note the `catch`: on any doubt it returns `true` and hands the packet back to the peer mod — safe
precisely because the marker byte makes it a no-op there anyway.

**Sending is reflection, per role.** Host: `Server.BroadcastRawReliableOrdered(byte[])`. Client:
`Client.SendRaw(byte[])`. Both resolved by name off `CoopSession`, both returning `false` on any
unresolved member so the caller can log a *consequence* rather than a stack trace — "peers will diverge
until the next stash edit" (`Payload/StashSync/StashSyncGuard.cs:415-453`, consumed at :166-169).

**Patch the receive hook on every candidate type name.** BT moved these types between namespaces once
already, so the hook is installed by walking a priority list — `BannerlordTogether.Network.*` first,
the legacy names as fallbacks — and succeeds if any lands
(`Payload/StashSync/StashSyncGuard.cs:117-131`;
`Payload/PregnancySync/PregnancySyncGuard.cs:225-239`; §3.1).

### 8.4 Wire format: version first, validate at parse, send only what cannot be re-derived

The payload types are plain `net472` classes with **zero engine dependencies**, so the identical source
file compiles into both the mod and a headless test (§5.5).

- **A format-version byte, with an exact-match drop.** `FormatVersion` is the first field; a mismatch
  returns `null` — "a newer/older peer — drop rather than misparse"
  (`Payload/StashSync/StashPayloadData.cs:27,83-86`;
  `Payload/PregnancySync/BirthPayloadData.cs:24,96-99`). Two players on different mod versions is the
  **normal** case in co-op, not an edge case.
- **Parsers never throw; they return `null`.** Every `FromBytes` / `TryUnframe` wraps its whole body in
  a blanket catch (`Payload/StashSync/StashPayloadData.cs:67-114`;
  `Payload/PregnancySync/BirthPayloadData.cs:81-126`;
  `Payload/StashSync/StashWireFraming.cs:53-64`). The parse runs on the *peer mod's* network thread,
  where an escaping exception is an unattributable crash the player will blame on the wrong mod.
- **Validate structure and semantics at parse time, not apply time.** Reject `entryCount` outside
  0..100000, and any entry with an empty `ItemStringId` or `Count <= 0` — "a sane sender never emits
  these — corrupt packet" (`Payload/StashSync/StashPayloadData.cs:89-104`); reject a child count
  outside 0..16 (`Payload/PregnancySync/BirthPayloadData.cs:103-106`). At parse the cost of rejection
  is a dropped packet. At apply, the same corrupt count reaches `AddToCounts` on a roster you have
  already cleared.
- **Send only what the receiver cannot deterministically re-derive.** The birth payload carries only the
  fields the receiver cannot reproduce: the parents' ids (`MotherStringId` on the envelope,
  `FatherStringId` per child) plus each child's id, gender, name and appearance, alongside a format
  version and a stillborn count (`Payload/PregnancySync/BirthPayloadData.cs:26-29,33-43`). Clan, culture
  and birthday are *not* sent, because `DeliverOffSpring(mother, father)` reproduces them identically on
  both machines from the same parents. This shrinks the wire, deletes whole classes of
  serialization problem (types with no round-trippable form), and turns the engine's own determinism
  into part of the protocol. It is also a deliberate divergence from the written spec, which had
  proposed sending clan/culture/birthday (`docs/SPEC-pregnancy-coop-sync.md:25-26`).

### 8.5 Host authority, the relay, and applying safely

**Full snapshots, not deltas.** The stash payload is the whole roster: idempotent to re-apply, immune
to packet ordering, converging in one packet, with last-close-wins on a simultaneous edit
(`Payload/StashSync/StashPayloadData.cs:14-17`). For a container edited a few times an hour, paying in
bytes to delete every ordering, loss and replay bug class is the right trade.

**The relay, and the invariant that makes it loop-free.** The receiver applies, and *then*, if it is
the host with peers connected, re-broadcasts the payload it just applied
(`Payload/StashSync/StashSyncGuard.cs:371-376`). N-peer convergence out of a star topology, with no
peer list and no per-peer addressing. It is safe because of a one-line invariant you can state and
check: **the send path is only the local UI commit hook, and applying never sends** — so the origin
peer simply re-applies its own identical state.

**Guard the echo when you apply a remote event by calling the game's own action.** Reconstructing a
child calls `DeliverOffSpring`, which raises the very birth event the host-side broadcaster listens to.
A flag set in a `try/finally` around the reconstruct call, checked at the top of the handler, breaks the
loop (`Payload/PregnancySync/PregnancySyncGuard.cs:36,247,369-390`). It is a plain static rather than
`[ThreadStatic]` only because reconstruction is confined to one thread, and the constraint is written
down where the field is declared (§2.6, §10.5).

**Never touch engine state from the peer's network thread.** The receive hook parses bytes (thread-safe)
and enqueues under a lock; the game's `Tick` drains under the same lock and does all engine work, each
item in its own `try/catch` so one poisoned item is dropped and draining continues
(`Payload/StashSync/StashSyncGuard.cs:57-58,248-254,270-300`;
`Payload/PregnancySync/PregnancySyncGuard.cs:38-41,326-333,98-127`). Hero creation and roster mutation
off the game thread is silent state corruption, not an exception.

**Be idempotent by existence check.** Before reconstructing, look the id up locally and skip if it is
already there — "already present (idempotent — re-sent packet or shared base save)"
(`Payload/PregnancySync/PregnancySyncGuard.cs:359-362`). Co-op partners share a base save, so the object
may already exist for reasons that have nothing to do with your packet; this makes double delivery,
resend and save-sharing all harmless at once.

**Re-key ids so cross-machine references resolve.** Two machines that independently create "the same"
object disagree forever unless one adopts the other's id: unregister the local object, overwrite its
`StringId` with the authority's, re-register with `RegisterPresumedObject`
(`Payload/PregnancySync/PregnancySyncGuard.cs:413-420`).

**Defer the apply while local UI owns the live structure** — and check *before* dequeuing, still under
the lock, so the update stays queued instead of being lost
(`Payload/StashSync/StashSyncGuard.cs:285-289`). Bannerlord screens bind to the live `ItemRoster`;
clearing it under an open screen is visible corruption.

**Resolve each element, skip what you cannot, and count the skips.** An item id the receiving machine
cannot resolve (a peer-side mod, a crafted item) is skipped with a named reason, and the applied line
reports before → after plus the skip count, so a divergence is visible in the log rather than only in
the game (`Payload/StashSync/StashSyncGuard.cs:347-370`).

### 8.6 Per-machine state files, and rename-never-delete

Some state belongs to the *machine*, not to the save and not to the wire: which hero is mine, and what
one-time remediation this box already performed. Both live next to `guardconfig.json` in the module
root, derived from the assembly location (§1.3), so a reinstall of the game does not lose them and a
shared save cannot carry them to the other player.

**A tiny persistent map, in the same regex-JSON shape as the config.** `hero-identity.json` is keyed by
`Campaign.UniqueGameId`, parsed with `Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"")` and
written back with a hand-rolled `StringBuilder` formatter
(`Payload/CoopHeroIdentityLock.cs:42-45,259-290`). The naive parser is trustworthy for this narrow
shape because the **round trip is pinned by a self-test** — format a two-entry probe map, parse it back,
assert both entries survive (`Payload/CoopHeroIdentityLock.cs:316-323`).

**Explicit claim over inference.** The mod refuses to guess which hero belongs to this machine: a *new*
campaign records `MainHero` automatically (unambiguous — you created it), an *existing* campaign
requires a one-time `"myHero": "Name"` claim in the config, and anything else logs guidance **once** and
does nothing (`Payload/CoopHeroIdentityLock.cs:26-33,128-156`). A wrong guess would reproduce the exact
bug the feature exists to fix, so "ask once" is the safest heuristic available.

**Wait for a safe moment to act.** The claim is deferred while `Mission.Current != null` and retried
each tick — swapping the player's identity inside a mission would leave that mission's agents, teams and
controllers bound to a hero who is no longer the player
(`Payload/CoopHeroIdentityLock.cs:72-89`).

**A handled-offset ledger makes a one-time remedy idempotent.** `bootstrapwatch.state` holds
`logName|offset` lines; a detected event at or below the recorded offset is skipped, and the offset is
written before acting (`Payload/BootstrapWatch.cs:70-80,132-187`). Without it, every startup would
re-run the remedy against the same old line in a log that never shrinks.

**Scan wide once, narrow afterwards.** The startup pass scans the *entire* peer log for the last
occurrence; mid-session ticks read only the last 256 KB where new lines land; both open with
`FileShare.ReadWrite` because another process is writing to the file right now
(`Payload/BootstrapWatch.cs:24-27,29-47,193-250`). The tail-only version was tried first and missed a
real abort that sat at ~50 KB of a 12.7 MB log.

**Rename, never delete.** When the remedy is "make the other mod rebuild its cache", move each file to
`<name>.stale-yyyyMMddHHmmss` with a per-file `try/catch`, and count what moved
(`Payload/BootstrapWatch.cs:97-129`). It is reversible by hand, auditable afterwards, and still forces
the rebuild the peer mod's own `restartRequired` implies — without your mod ever deleting a file it did
not create.

### 8.7 What the wire cannot express: preserve, then clear, then re-apply

Full-snapshot semantics (§8.5) are correct for everything both sides can name and **destructive for
everything the sender cannot express**. The absence of an item from a snapshot is not a withdrawal —
and that asymmetry is the single subtlest bug in a sync design.

Here, a player-crafted item's design exists only on the machine that crafted it, so it can never appear
in a snapshot. A naive `Clear` + apply would therefore delete the other player's crafted sword every
time either of them closed a stash screen. The shape that fixes it
(`Payload/StashSync/StashSyncGuard.cs:324-365`):

1. Build the set of ids the payload **does** mention (`HashSet<string>` with `StringComparer.Ordinal`).
2. Snapshot the local entries that are machine-local **and not named by the payload** — if the peer
   classified an item differently (version skew, differing mods), applying their stack *and* re-adding
   yours would silently duplicate it, so **the payload's word wins for any id it mentions**.
3. `Clear`, apply the remote snapshot, then re-add the preserved entries.
4. Report the preserved count in the log line, so "why is my stash different?" has an answer.

Two supporting rules:

- **Classify with the property that means what you need, not the one that looks similar.** The test is
  `IsCraftedByPlayer`, not a bare `WeaponDesign` check — the latter is also true for ~283 vanilla
  crafted weapons, and using it would have de-synced all of them
  (`Payload/StashSync/StashSyncGuard.cs:35-44,214-233`). The classifier has a **second clause**, and it
  is the one that catches modded content: an item is also machine-local when
  `MBObjectManager.Instance.GetObject<ItemObject>(item.StringId)` does not reference-equal the item —
  i.e. its id does not resolve back to the same object locally (:224 and :228; named in
  `docs/MODDING-PITFALLS.md:81` as "plus a StringId round-trip as a second clause"). Ship only the first
  clause and a peer's modded items, whose ids do not resolve on your machine, are silently deleted.
- **Fail toward preservation.** The classifier's `catch` returns `true` — "unreadable = unexpressible —
  err toward preserving it" (`Payload/StashSync/StashSyncGuard.cs:230-233`). When the two outcomes are
  "this item does not sync" and "this item is deleted", the error direction is not a judgement call.

Both of these were found by adversarial review of the commit, not by testing — which is the argument
for enumerating, in writing, what your wire format structurally cannot say, before you ship an
authoritative snapshot that will overwrite it.

---

## 9. Versioning, deployment and release

A Bannerlord mod has no package manager, no release infrastructure and no telemetry. What it does have
is a player pasting a log into a chat window, and one question you must be able to answer from that log
alone: *which build was this?*

### 9.1 One version source, stamped everywhere, read back from the assembly

**What.** A single `<Version>` in `Directory.Build.props` (`:3-10`). MSBuild stamps it into both
assemblies, and one target pokes it into the launcher-visible manifest:

```xml
<Target Name="StampSubModuleVersion" AfterTargets="Build"
        Condition="'$(MSBuildProjectName)' == 'BLTDeploymentCrashGuard'">
  <XmlPoke XmlInputPath="$(MSBuildThisFileDirectory)SubModule.xml"
           Query="/Module/Version/@value" Value="v$(Version)" />
</Target>
```

(`Directory.Build.props:12-19`.) The `MSBuildProjectName` condition is the part people miss: without it,
a two-project solution pokes the same file twice per build.

**At runtime, read the identity — never a literal.** `Diag.Version` is
`typeof(Diag).Assembly.GetName().Version` reduced to `Major.Minor.Build`
(`Harness/Diag.cs:17-30`), and the banner printed at `OnSubModuleLoad` joins it to the build time and a
per-launch session id (`Harness/Diag.cs:32,45-61`; `Harness/SubModule.cs:19`):

```
===== BLT Deployment Crash Guard v<Version> (harness build <yyyy-MM-dd HH:mm>) session=<8 hex> =====
```

**Why it matters.** `SubModule.xml` had already drifted to `v1.0.0` while the DLLs were far ahead —
which is invisible until someone reports a bug against the launcher's number. A banner that reads its
own assembly identity cannot lie about which build produced the lines beneath it, and the session id
tells you whether two log fragments came from the same launch.

### 9.2 What "deploy" means: three files, two destinations, one hash check

```bash
cd Harness  && dotnet build -c Release
cd ../Payload && dotnet build -c Release
```

| File | Built to | Deployed to |
|---|---|---|
| `BLTDeploymentCrashGuard.dll` (harness) | `Harness/bin/Release` | `<Game>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/` and repo `dist/` |
| `BLTDeploymentCrashGuard.Payload.dll` | `Payload/bin/Release` | same two places |
| `SubModule.xml` | repo root (stamped by the build) | module **root** (not `bin/`) and repo `dist/` |

Then `md5sum` all three across build output, game module and `dist/` — they must match
(`CLAUDE.md:29-46`). The failure this prevents is the most expensive one available: spending an evening
diagnosing a build you did not actually deploy. `<AppendTargetFrameworkToOutputPath>false</…>` and
`<DebugType>none</DebugType>` keep those paths short and the shipped set to exactly the files the
installer downloads (§1.1).

**Pushing is releasing.** `install.cmd` fetches the three artefacts straight from
`raw.githubusercontent.com/…/main/dist/` (`install.cmd:9,58-60`), and `.gitignore` excludes only
`bin/`, `obj/` and `.runner/` — so `dist/` is committed. There is no GitHub Release, no CDN and no
versioned URL: **any push that touches `dist/` ships to every player immediately.** The house rule
follows directly — never push mid-investigation; deploy locally, iterate, push when a fix is proven
(`CLAUDE.md:31-32`, and the working discipline at `CLAUDE.md:84-85`).

### 9.3 Hot-reload versus fresh launch

| You changed | What is needed | Why |
|---|---|---|
| A payload guard/fix/tracer | Copy the payload DLL into the module; ~400 ms | The engine shadow-copies and swaps generations (§4) — `[HOTRELOAD] gen2 applied` in the log |
| Anything in the harness | **Fresh launch** | The harness DLL is the loaded module and is **locked while the game runs** (`CLAUDE.md:48-50`) |
| A load-time fix (e.g. `MovementOrderTypeInitGuard`) | **Fresh launch**, even though it lives in the payload | The type is prepared once per process; a reload cannot un-poison a cached type initializer (§2.11, §10.3) |
| A config flag | Nothing, if the flag is read fresh (§7.3); otherwise a relaunch | The harness caches the file per session (§7.1) |

This table is also an argument about *where code should live*: because only the payload can be
redeployed live, anything you might need to hot-fix during a repro belongs there — which is why
`TraceThrottle` is a payload type (§4.1).

### 9.4 Replacing a file the game holds open: rename-aside

The installer runs while the game may be running, so it moves the locked DLLs aside instead of failing:

```bat
for %%F in (BLTDeploymentCrashGuard.dll BLTDeploymentCrashGuard.Payload.dll) do (
  if exist "%DLLDIR%\%%F" (
    del /f /q "%DLLDIR%\%%F.prev" >nul 2>&1
    ren "%DLLDIR%\%%F" "%%F.prev" >nul 2>&1
  )
)
```

then downloads the new files at the original names (`install.cmd:49-60`). Windows allows **renaming** a
file that is locked for writing, which is what makes this work — and it removes the single most common
"update failed" support ticket for Bannerlord mods. Delete the stale `.prev` first, or the rename fails
on the second update.

This is the same family as two other patterns in this repo: the hot-reload shadow copy, which exists
because `LoadFrom` locks its file for the process lifetime (§4.2, §10.2), and rename-never-delete
remediation of another mod's regenerable cache (§8.6).

### 9.5 Finding the game without parsing a registry

Three tiers, in order (`install.cmd:10-39`, mirrored in `share-log.cmd:10-34` and
`collect-diagnostics.cmd:11-25`):

1. `BANNERLORD_DIR` wins if set — the override that lets a developer or a CI job point the scripts at a
   non-standard install.
2. Otherwise scan a literal list of Steam / SteamLibrary paths across C:–G:, taking the first whose
   `\Modules` folder exists.
3. Otherwise prompt.

Then strip quotes and validate before using the value:

```bat
set "GAME=%GAME:"=%"
if not exist "%GAME%\Modules" ( echo ERROR: ... & exit /b 1 )
```

Both the `for` loop and the `set /p` prompt can leave quotes embedded in the variable, and a quoted path
concatenated into a longer path fails in a way that reads like a missing file. No registry parsing, no
VDF parsing, no dependencies.

Two things the installer does at the end that are easy to forget and matter:

- **It tells the player the load order.** Tick the mod in the Singleplayer list *after*
  BannerlordTogether (`install.cmd:67-73`) — because an `Optional` dependency does not control order
  (§1.2), which is why every BT-facing guard also carries the late-load retry (§2.14).
- **It can enable log streaming without touching the player's config**, writing `logstream.txt` from
  `BLTGUARD_BIN` (`install.cmd:62-65`; §7.5, §6.9).

---

## 10. .NET Framework facts that shape the design

Bannerlord modules are `net472` assemblies (§1.1), and several of the designs in this guide exist only
because of what that runtime does and does not allow. This section collects those facts in one place;
the sections that act on them are cross-referenced rather than repeated.

### 10.1 Assemblies never unload

On .NET Framework an assembly loaded into an AppDomain stays there for the life of that domain — there
is no unloadable load context, and tearing down the domain is not an option inside a running game. The
hot-reload engine therefore *adds* a generation every time; it never removes one. Measured cost here:
**~1–3 MB leaked per reload, so restart every few dozen** (`HOTRELOAD.md:63`).

Three design consequences follow, and every one of them is load-bearing:

- **The reload contract is "new statics, new patches", not "old code gone".** The previous generation's
  code is still in the process and its Harmony patches are still installed until you remove them by
  owner id — which is why per-generation ids and `UnpatchAll` exist (§2.12).
- **State that must survive a reload cannot live in a payload static** (§4.5). Fresh statics per
  generation are what makes reload *clean*; the same property is what loses your data.
- **The leak is acceptable only because the capability is dev-only.** Runtime code loading is
  double-gated behind a config flag *and* an on-disk marker file, so a player never arms it (§4.6).

### 10.2 `LoadFrom`, the load context, and the simple-name dedup

Four facts, each of which cost a release here (evidence and code in §4.2, §4.3, §4.4):

1. **`Assembly.Load(byte[])` has no load path**, so its dependency probing falls back to the application
   base. Inside Bannerlord that resolves `0Harmony` to a *different* copy than the one your harness
   patched with, and the interface call across the boundary fails with
   `TypeLoadException: … does not have an implementation` (`HOTRELOAD.md:10` for the mechanism;
   `CHANGELOG.md:219` and `Harness/HotReload.cs:60` for the exception text). In this game the two copies
   are the game bin's `0Harmony 2.4.2.0` and the `Bannerlord.Harmony` module's `0Harmony 2.3.6.0`
   (`CHANGELOG.md:215-220`).
2. **An `AssemblyResolve` handler cannot rescue that**, because the handler fires only when probing
   *fails* — and a byte-load probes successfully, against the wrong copy. Change the load context; do
   not add a resolver (`CHANGELOG.md:213-221`; `Harness/HotReload.cs:281-287`). The resolver pin was
   shipped first (`CHANGELOG.md:274-278`) and did not fix it.
3. **The `LoadFrom` context dedups by simple name only.** A freshly built DLL with a new
   `AssemblyVersion` but the same name comes back as the already-loaded assembly — field-proven here as
   `LoadFrom deduped to already-loaded 1.2.7.42191`
   (`Harness/HotReload.cs:315-324`; the reasoning is recorded in
   `Payload/BLTDeploymentCrashGuard.Payload.csproj:9-18`). Only a unique assembly **name** per build
   defeats it (§4.3), and the load must then be *verified* by comparing `candidate.Location` to the path
   you asked for.
4. **`LoadFrom` also caches path → assembly, and locks the file it loaded** for the process lifetime.
   So the load path must be unique per *attempt* — process id, generation number and
   `DateTime.UtcNow.Ticks` in hex (`Harness/HotReload.cs:307-312`) — and the canonical DLL must be a
   shadow copy, or a retried generation silently returns the failed attempt's assembly and the next
   build cannot overwrite the file at all.

The general rule behind all four: **in a process that already contains several copies of your
dependencies, assembly identity is something you must control explicitly**, and any part of the system
that "works" without you controlling it is working by accident — here, generation 1 always worked, which
hid the defect for three releases.

### 10.3 A failed type initializer is cached for the whole process

When a static constructor throws, the CLR marks the type as failed and **every later touch re-throws the
same `TypeInitializationException`, carrying the original stack** — captured at a different moment,
possibly on a different thread. Two things follow:

- **A logged type-init throw may be a re-throw.** The exception's own stack describes the first failure,
  not the call you are looking at, so capture the **live** stack alongside it (§6.5) and print the full
  inner chain (§6.4) — a `TypeInitializationException`'s real cause is always its inner exception.
- **The only repair is to get there first.** Make the throwing line safe, then choose when the static
  constructor runs with `RuntimeHelpers.RunClassConstructor`, which caches the type as *successfully*
  initialized for the rest of the process (§2.11). Because a `beforefieldinit` type may be initialized
  at any point up to its first static field access, "before the game touches it" includes "before your
  own patching makes the CLR prepare it" — which is why the load-time guard is the first patch installed
  by `PayloadEntry.Apply`, before `PatchAll` and every other guard, with only the `safeMode` kill switch
  ahead of it (`Payload/PayloadEntry.cs:31-46`).

This is also the reason a load-time fix needs a fresh launch rather than a hot reload (§9.3): the
process-wide cached failure is not something a new generation can undo.

### 10.4 `InternalsVisibleTo` is matched by exact assembly name

There are no wildcards. If your consumer's assembly name varies — as it must when every build is stamped
with a unique name to defeat the `LoadFrom` dedup (§4.3) — no `InternalsVisibleTo` entry can ever cover
it, and the shared surface has to be `public`. That is exactly what happened here: `Log`, `Diag`,
`GuardConfig` and `SelfHealing` are public, and the attribute survives only for the fixed-name case, with
the reason written next to it (`Harness/AssemblyInfo.cs:1-9`).

Decide this **before** you design the identity scheme, not after the rename lands: "the internal name
must vary" and "the consumer uses my internals" are incompatible requirements, and the second one is the
cheaper to give up.

### 10.5 `[ThreadStatic]` gives you one slot per thread — including the threads you did not create

A Bannerlord process runs your code on several threads: the main game loop, the UI, the peer mod's
network thread, and whatever the thread pool hands you. `[ThreadStatic]` state is per-thread, which is
exactly right for four uses in this repo and exactly wrong for a fifth:

| Use | Where | Why per-thread is correct |
|---|---|---|
| Depth counters distinguishing explicit calls from implicit ones | `Payload/SiegeCommandGuard.cs:62-66` | An unrelated thread's call must not open the gate for yours (§2.6) |
| Scope flags set in a prefix, cleared in a finalizer | `Payload/TimeEnforcementGuard.cs:35-36`; `Payload/MapClickSpeedKeeper.cs:25-26` | The scope *is* the call stack (§2.7) |
| A re-entrancy guard inside an exception handler | `Payload/CharacterCreationTrace.cs:35-36` | A handler that throws re-enters itself; per-thread keeps that correct across the game's many threads (§6.4) |
| Two-phase capture slots (intent in the prefix, outcome in the postfix) | `Payload/TimeTrace.cs:26-32` | The pair must belong to one call (§6.8) |

Two rules that keep it honest:

- **Never write a field initializer on a `[ThreadStatic]` field.** The initializer runs when the type is
  initialized — on one thread — so every other thread sees `default(T)`. Every `[ThreadStatic]` field
  here is a counter, a `bool` or a reference whose meaning at `0` / `false` / `null` is "not in scope",
  so no initializer is needed.
- **Per-thread means it also scopes *away*.** An asynchronous write from another thread is not covered
  by your flag — the right answer for a re-entrancy guard, the wrong answer for state a network thread
  and the game thread must share (which is what the lock-and-queue in §8.5 is for). The mirror image:
  a plain static is acceptable only where the work is provably confined to one thread, and where you use
  one, write that constraint down beside the field
  (`Payload/PregnancySync/PregnancySyncGuard.cs:36`).

### 10.6 `Environment.TickCount` wraps

`Environment.TickCount` is a signed 32-bit millisecond counter, so it covers 2^31 ms — a little under
25 days of uptime — and then overflows to `int.MinValue`. Every naive `now - last > window` comparison
straddling that moment produces a nonsense delta, and the failure is not symmetric: a rate limiter can
latch **suppressed forever**, a throttle can go silent for the rest of the session.

The repo pairs every delta with a direction check, chosen so that a wrap degrades to *act now*:

```csharp
// throttle: skip while inside the window — a wrap makes now < last, so we do NOT skip
if (_last != 0 && now - _last < WindowMs && now >= _last) return;

// breaker: retry after the window — a wrap makes now < last, so we retry immediately
if (now - _last > RetryMs || now < _last) { /* let one call through */ }
```

This appears in essentially every timed path here — `Payload/EncounterLoopGuard.cs:96,109,117`;
`Payload/TraceThrottle.cs:63-65`; `Payload/PayloadEntry.cs:166,194`;
`Payload/BootstrapWatch.cs:38`; `Payload/SiegeCommandGuard.cs:512-521`;
`Payload/BackgroundTickBudgetGuard.cs:130`; `Payload/PartyAiCrashGuard.cs:155` — and it is the kind of
bug that never reproduces in a test session, because you would have to leave the machine on for a month
to see it.

**For durations rather than intervals, use `Stopwatch`.** `(Stopwatch.GetTimestamp() - start) * 1000 /
Stopwatch.Frequency` is high-resolution, allocation-free and has no wrap concern at these scales — which
is what the budget guard measures a foreign tick with (`Payload/BackgroundTickBudgetGuard.cs:97-121`;
§2.16).
