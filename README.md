# BLT Deployment Crash Guard

Companion mini-mod for **BannerlordTogether** that stops the guaranteed crash-to-desktop when
entering siege (and other deployment-phase) battles.

## The crash it fixes

```
System.NullReferenceException
at TaleWorlds.MountAndBlade.DeploymentMissionController.SetupTeams()
at TaleWorlds.MountAndBlade.DeploymentMissionController.OnMissionTick_Patch1(...)
```

Root cause (verified against game v1.4.8 decompile):

- Native `DeploymentMissionController.OnMissionTick` calls `SetupTeams()` on the first tick where
  `Mission.Scene != null`.
- `SetupTeams()` does `Mission.InitialPlayerAgent.Controller = AgentControllerType.None` with **no
  null check**.
- `Mission._initialPlayerAgent` is only assigned when an agent is built with
  `Controller == AgentControllerType.Player`. BannerlordTogether defers/replicates player-side
  spawns over the network in its SP-native co-op battles (see its
  `ReplayCachedAgentSpawnsToPeer` / deployment ready-gate), so on sieges the player agent does not
  exist yet when the scene finishes loading → guaranteed NRE → CTD.
- BannerlordTogether gates `FinishDeployment` (its `SpNativeDeploymentReadyGatePatch`) but never
  gates the earlier `SetupTeams` tick — that's the gap this mod closes.

## What it does (three layers)

1. **Tick hold (the real fix)** — prefix on `DeploymentMissionController.OnMissionTick`: while team
   setup hasn't run and the scene is ready, skip the tick until `Mission.InitialPlayerAgent`
   exists, then let native setup run against valid state. Held at most 90s so a mission that never
   gets a player agent can't softlock. BannerlordTogether's own postfix on the same method still
   runs while held (Harmony postfixes are not skipped), so its ready-gate keeps working.
2. **`SetupTeams` finalizer** — any escaping exception there is an unconditional CTD; suppress and
   log instead.
3. **`FinishDeployment` finalizer** — same dereference exists there (and `_initialPlayerAgent` is
   re-nulled if the player agent is ever removed). On an escaping exception, best-effort completes
   the method's tail (re-enable AI ticking and dying, `OnAfterDeploymentFinished`, remove the
   controller) so the battle stays playable, then suppresses.

Everything is logged to `Modules/BLTDeploymentCrashGuard/CrashGuard.log`, and a `[Deploy Guard]`
message is shown on screen whenever a crash was actually suppressed.

## Build

```
dotnet build -c Release
```

Game path is set in the `.csproj` (`GameDir`); override with `-p:GameDir="..."` if needed.

## Install

Copy to `<Bannerlord>/Modules/BLTDeploymentCrashGuard/`:

```
SubModule.xml
bin/Win64_Shipping_Client/BLTDeploymentCrashGuard.dll
```

Enable "BLT Deployment Crash Guard" in the launcher, ordered **after** BannerlordTogether (the
optional dependency makes LauncherEx sort it there automatically). It patches only native game
methods, so it is safe (and inert) with BannerlordTogether disabled too.
