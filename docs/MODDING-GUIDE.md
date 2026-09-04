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
- **Harmony is not in the game bin.** It ships as its own module:
  `<Game>/Modules/Bannerlord.Harmony/bin/Win64_Shipping_Client/0Harmony.dll`
  (`Harness/BLTDeploymentCrashGuard.csproj:31`).
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
  (`Payload/TimeTrace.cs:20-22`). That is both a hazard (your prefix runs even when the call is already
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
check of the type's attributes (`beforefieldinit`, value type). Verbatim from the README, three commands
produced the whole root cause of the 2026-09-04 `MovementOrder` crash — "No decompiler, no guessing"
(`tools/il-probes/README.md:34-44`):

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
permanently-armed setter blocker with no owner (`Payload/TimeEnforcementGuard.cs:117-132`).

### 2.8 Patch by name via AccessTools, so updates degrade gracefully

Resolve **everything** at runtime — `AccessTools.Method`, `AccessTools.Field`,
`AccessTools.PropertyGetter`, `AccessTools.PropertySetter`, `AccessTools.TypeByName` — null-check the
result, and on a miss log `<tag> inactive — members not resolved (game update?)`, report
`Diag.Report(component, false, …)` and **return without patching**
(`Payload/SiegeCommandGuard.cs:93-109`; `Payload/SiegeGatePromptFix.cs:42-49`;
`Payload/CivilianGateCloseFix.cs:40-48`; `Payload/CoopCommandSplit.cs:88-94`;
`Payload/PartyAiCrashGuard.cs:37-56`; `Payload/BackgroundTickBudgetGuard.cs:57-68`). The failure mode of
a crash-guard mod must never be a crash.

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
(`Payload/TimeEnforcementGuard.cs`, §2.14 pattern).

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
.Invoke(hero, new object[] { HeroDeathMark.None })` — the `?.` makes a missing setter a no-op instead of
an NRE (`Payload/IllnessDeathGuard.cs:121-122`). For a get-only auto-property, write the backing field:
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

**Ordering is part of the fix.** `MovementOrderTypeInitGuard.ApplyEarly` is the **first** statement of
`PayloadEntry.Apply`, before `harmony.PatchAll` and every other guard, with a comment stating why:
patching `Formation`/`OrderController` is itself what makes the CLR prepare `MovementOrder`
(`Payload/PayloadEntry.cs:38-46`). Your own instrumentation can trigger the type preparation you are
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
  `Payload/TraceThrottle.cs:63-65`; `Payload/PayloadEntry.cs:172-176,193-197`). See §10.6 for the exact
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
- **A bounded timeout** (15 s) that exits with a user-visible fallback note — never wait forever for state
  a peer may never confirm (:185-197).
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
