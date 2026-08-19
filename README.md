# BLT Deployment Crash Guard

Companion mod for **Mount & Blade II: Bannerlord** co-op sessions (BannerlordTogether).
It stops the guaranteed crash-to-desktop on siege deployment, adds an automatic
solo/co-op battle-mode switch, and logs deep battle diagnostics so co-op battle bugs
(empty armies, wrong player getting command) can be pinned down and fixed.

It patches **native TaleWorlds game methods only** — it contains no third-party mod
code and does not modify any other mod's files.

## Install (players)

Paste this into a **Command Prompt** (cmd), then press Enter:

```
curl -fsSL -o "%TEMP%\bltguard.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/install.cmd && call "%TEMP%\bltguard.cmd"
```

It finds your Bannerlord install (asks if it can't), downloads the mod into
`Modules/BLTDeploymentCrashGuard`, and you're done. Then in the Bannerlord launcher
(BLSE/LauncherEx), tick **"BLT Deployment Crash Guard"** in the Singleplayer mods
list, ordered anywhere **after** BannerlordTogether. Re-run the same line any time to
update.

## What it does

1. **Siege crash guard** — vanilla `DeploymentMissionController.SetupTeams()` and
   `FinishDeployment()` dereference `Mission.InitialPlayerAgent` without null checks.
   When a co-op battle starts with no player agent spawned, that's an instant CTD.
   Harmony finalizers suppress the crash, log it, and best-effort complete the
   deployment tail so the mission stays alive.

2. **Battle mode (auto/solo/coop)** — when hosting a co-op session **alone**, the
   co-op battle pipeline can strip your side out of missions (empty formations, no
   player agent). In `auto` mode the mod checks at every battle start whether a
   remote player is actually connected:
   - alone → foreign Harmony patches are lifted off a fixed list of native
     battle/deployment/spawn methods (and stashed) → pure vanilla battles;
   - a friend is connected, or you are the client → every stashed patch is
     re-applied under its original owner and priority → co-op battle sync fully
     intact.

3. **Diagnostics** — `Modules/BLTDeploymentCrashGuard/CrashGuard.log` records battle
   flow (menu switches, encounters, mission launches with caller stacks) and command
   control (who becomes player-controlled, order-controller and formation ownership,
   plus a full control map of every team/formation when deployment finishes).

## Config

`Modules/BLTDeploymentCrashGuard/guardconfig.json` (created on first run):

```json
{ "battleMode": "auto" }
```

- `auto` — detect at battle time (recommended)
- `solo` — always vanilla battles
- `coop` — never lift the co-op pipeline (use to test/repro co-op battle bugs solo)

## Build from source

Requires the .NET SDK and the game installed. Game path is set in the `.csproj`
(`GameDir`); override with `-p:GameDir="..."`.

```
dotnet build -c Release
```

Output: `bin/Release/BLTDeploymentCrashGuard.dll` → copy to
`<Bannerlord>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/` next to
`SubModule.xml`.

## Known co-op issues being tracked

See `UPSTREAM_BUG_REPORT.md` — host-solo battles start with the player side empty
(root cause of the siege CTD), and in co-op sieges command of the host's army is
sometimes handed to the client. The diagnostics above exist to pin these down.
