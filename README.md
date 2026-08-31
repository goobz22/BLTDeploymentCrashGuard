# BLT Deployment Crash Guard

Companion mod for **Mount & Blade II: Bannerlord** that fixes crashes and co-op bugs in
**BannerlordTogether** sessions — and several that also bite in plain single-player. It began
as a fix for the guaranteed siege-deployment crash-to-desktop and has grown into a suite of
self-disabling crash guards and root-cause fixes, plus deep battle diagnostics.

Design principles:

- **It patches native TaleWorlds game methods only.** It contains no third-party mod code and
  never modifies another mod's files. Every hook into BannerlordTogether is by reflection.
- **Every fix is self-disabling.** Crash guards are Harmony *finalizers* that do nothing unless
  the underlying bug actually throws; root fixes go inert when the bad condition stops occurring.
  If TaleWorlds or BannerlordTogether fixes something upstream, our fix simply never fires again
  (visible as "never fired" in the health report, so it can be retired).
- **Every load-bearing fix reports its health and self-tests at startup**, so a BannerlordTogether
  update that moves a method surfaces immediately instead of silently breaking a fix.

## Install (players)

Paste this into a **Command Prompt** (cmd), then press Enter:

```
curl -fsSL -o "%TEMP%\bltguard.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/install.cmd && call "%TEMP%\bltguard.cmd"
```

It finds your Bannerlord install (asks if it can't), downloads the mod into
`Modules/BLTDeploymentCrashGuard`, and you're done. Then in the Bannerlord launcher
(BLSE/LauncherEx), tick **"BLT Deployment Crash Guard"** in the Singleplayer mods list, ordered
anywhere **after** BannerlordTogether. Re-run the same line any time to update.

Both players in a co-op session should install it — several fixes protect each machine's own
player, and the co-op sync features need it on both ends.

## What it does

### Crash fixes

1. **Siege / battle deployment crash** — vanilla `DeploymentMissionController.SetupTeams()` and
   `FinishDeployment()` dereference `Mission.InitialPlayerAgent` without null checks; a co-op
   battle that starts with no player agent spawned is an instant CTD. Finalizers suppress the
   crash, log it, and best-effort complete the deployment tail so the mission survives — including
   restoring player control and AI ticking.

2. **Dead-hero reactivation (issue quests)** — when companions you sent away as an issue's
   "alternative solution" return, the game reactivates every hero among them **without checking if
   they're still alive**. A companion who *died* while away gets reactivated, which crashes on the
   character-development event. Two root fixes: dead heroes are stripped from the returning roster
   (they can't return — they're dead), and a domain invariant blocks any dead→Active hero
   transition everywhere.

3. **Conversation-camera crash** — `MissionConversationCameraView.MakeSpeakerLookToListener` NREs
   when a conversation agent is removed mid-dialog (e.g. a marriage applying while the dialog is
   still open). A finalizer skips the offending camera frame instead of crashing.

4. **Clan-screen crash** — the clan tab (`GauntletClanScreen.CreateDataSource`) can NRE on a
   half-synced clan/party graph on a co-op client. A finalizer closes the screen safely instead
   of a CTD.

5. **Client hero-creation crash** — `FindMostSuitableHomeSettlement` NREs on a half-synced
   clan/faction during a client's culture selection; guarded so client character creation
   completes.

6. **Party-AI crashes** — `MobilePartyAi.GetBehaviors` / `EncounterManager.HandleEncounterForMobileParty`
   NRE on half-synced parties during join races; guarded.

7. **Encounter-loop breaker** — breaks the infinite encounter-meeting re-open loop that could hang
   a co-op session.

8. **Map-incident siege crash** — vanilla's map-incident popups apply their siege-progress effect
   through `PlayerSiege` with no null check: confirm an incident option after the siege is gone
   (or while your party isn't attached to your army's siege in co-op) and the game CTDs. The fix
   is repair-first: if your army's siege is live, the real effect is applied to it; only when no
   siege exists anywhere does it report "the siege has already ended". Any *other* incident
   effect that throws on stale world state is caught at the same choke point and skipped instead
   of crashing.

### Co-op & gameplay fixes

9. **Client bootstrap fix** — the root cause of "invisible armies / joins not registering / total
   desync" as a co-op client. BannerlordTogether's client action-cache audit false-negatives on a
   stale static mirror and permanently aborts, leaving the whole client session with its sync
   patches unapplied. We prime the mirror from the live catalog so verification passes and BT's
   deferred patches actually apply. `BootstrapWatch` also detects a silent `BootstrapAborted` and
   clears the stale cache so the next launch is clean.

10. **Marriage fixes** — two decompile-proven BannerlordTogether defects:
   - *Solo clan-mode block*: BT gates marriage on clan-mode sync, which can never complete when you
     host with no one connected, so marriage is blocked forever. When you're provably alone we
     report the correct solo clan-mode so marriage works; the instant a peer connects, BT's real
     sync takes over untouched.
   - *Atomic dowry*: BT routes the marriage to host validation but lets the **gold** apply
     natively in the same barter — so a rejected marriage still took your money. The whole barter
     now cancels *before* any gold moves if the marriage can't complete.

11. **No death by sickness** — Bannerlord's old-age "illness" can kill your hero. This blocks the
    death roll for the local player's hero and cures an in-progress illness (aging of everyone else
    is untouched). Config `noSickness`; stands down if the standalone *NoSickness* mod is installed.

12. **Player-identity guard (co-op)** — fixes the spawn identity swap where you spawn AI-controlled
    and the other player's hero becomes "you"; moves control back to your own hero and repairs
    team/order/formation ownership.

13. **Time fixes (co-op)** — keep fast-forward through map clicks; don't auto-pause when your party
    idles (`timeAlwaysFlows`); auto-grant the client shared time control so either player can
    pause/play/fast-forward (`shareTimeControl`); neutralize stale co-op speed enforcement after an
    in-game load so host and client speeds don't desync.

14. **Auto battle mode** — hosting a co-op session *alone*, BT's battle pipeline can strip your side
    out of missions (empty formations, no player agent). In `auto` mode the mod checks at every
    battle start whether a remote player is actually connected: alone → BT's battle patches are
    lifted (stashed) for pure vanilla battles; a peer connected (or you're the client) → every
    stashed patch is restored under its original owner/priority so co-op battle sync is fully intact.

15. **Co-op pregnancy / birth sync** *(`pregnancySync`, default on)* — BannerlordTogether
    disables pregnancy for the client (host rolls run normally) and never replicates births, so
    a client's family never grows and a host's children never appear on the client.
    Host-authoritative fix: the host serializes each newborn's identity and broadcasts it over
    BT's own network channel; the client reconstructs the identical child (same id, parents,
    appearance). Conception itself follows vanilla rules — the daily roll happens when the
    spouses are in the same settlement (waiting inside a castle with your spouse counts) or the
    same party, ages 18–45; every conception is logged and the player clan's shows on screen.
    (A newborn is an age-0 infant in **Clan → Members**, not visible on the map until coming
    of age.)

16. **Co-op shared settlement stash** *(`stashSync`, default on)* — BannerlordTogether has no
    stash sync at all (it syncs the workshop warehouse, but a stash deposit exists only on the
    machine that made it — so same-clan players never actually share a stash, and a client's
    deposits silently diverge from the host). Now, closing a stash screen broadcasts that
    settlement's full stash contents over BT's own channel and every other machine applies it,
    so the stash behaves like one shared chest — deposit on one machine, withdraw on the other.
    The host relays client updates so all peers converge; applying waits if you have that stash
    open; on a simultaneous edit the last-closed screen wins. Limitation: a player-crafted item
    can't be expressed on the wire, so crafted stacks stay machine-local — excluded from the
    sync and preserved through every update (never deleted), each machine keeping its own.
    Inert outside co-op.

17. **Join-hold pause escape** — when someone joins your hosted session, BannerlordTogether
    pauses the campaign for their entire save download + load + hero creation (its "keep playing
    while they load" fast-join only applies to returning players when another gameplay peer is
    already connected). During that hold your pause key is **silently swallowed** — it only
    toggles the manual pause reason and can never clear the join's SaveSync/HeroCreation hold, so
    a joiner stuck in a retry loop freezes the host forever. Now a swallowed unpause explains
    itself on screen (who is holding time and why), and pressing pause again within 6 seconds
    cancels the stuck join via BT's own transfer-cancel routine — the same recovery its timeout
    watchdog uses — so you resume playing and the joiner is told to reconnect.

18. **Background-tick freeze guard (co-op)** — while the host is in a battle, BannerlordTogether
    keeps the campaign world running by ticking it on *every* frame with no time budget; when a
    campaign tick turns expensive (e.g. a third army joining your ongoing battle), every frame
    drowns in background work and the game freezes with all cores pegged — potentially forever.
    The guard puts an equal-time throttle on that background tick: blow the 100 ms budget and
    background ticking pauses for as long as the tick took (capped 10 s), so the game always
    keeps roughly half of wall time. Not a disable — the co-op world keeps running, just never
    at the cost of a frozen game.

19. **Close the gate when visiting your settlement** — walking around a castle or town, the
    gate shows no interaction at all: vanilla deliberately force-opens it and disables the
    whole gate machine in civilian missions (three separate locks, all verified in the game
    code). This fix re-enables the gate and its standing points for the player, so an open
    gate shows **F: Close** (and a closed one **F: Open**), using vanilla's own door
    animation, nav-mesh, and collider handling. Battle and siege gates are untouched.

### Diagnostics & robustness

20. **Startup health + self-tests** — every launch logs the build/version, a `MOD HEALTH:` summary
    of which fixes resolved, and (with `selfTest`) a decision-logic self-test per fix. If a core fix
    fails to resolve, BannerlordTogether was likely updated and this mod needs a matching update.

21. **Diagnostics log** — `CrashGuard.log` records battle flow (menu switches, encounters, mission
    launches with caller stacks) and command control (who becomes player-controlled, order/formation
    ownership, a full control map at deployment finish). Verbose tracers are off by default
    (`tracing`) and rotate at 8 MB.

22. **Safe mode** — `safeMode` disables everything the mod does, to isolate whether an issue is this
    mod or BannerlordTogether.

## Sharing your log with your co-op partner

Every log line is tagged `[H]` (hosting), `[C]` (client), or `[S]` (solo), so two players' logs
merge into one side-by-side timeline. To share yours after a session:

```
curl -fsSL -o "%TEMP%\bltshare.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/share-log.cmd && call "%TEMP%\bltshare.cmd"
```

It uploads `CrashGuard.log` to a 24-hour file host and puts the link on your clipboard.

## Troubleshooting

- **Which build am I running?** The first line of `CrashGuard.log` each launch is
  `===== BLT Deployment Crash Guard vX.Y.Z (build …) session=… =====`, followed by the `MOD HEALTH:`
  line. If a core fix shows NOT resolved, BannerlordTogether was likely updated.
- **Is it this mod or BannerlordTogether?** Set `"safeMode": true` in guardconfig.json and restart.
- **Collect everything for a bug report** in one link:
  ```
  curl -fsSL -o "%TEMP%\bltdiag.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/collect-diagnostics.cmd && call "%TEMP%\bltdiag.cmd"
  ```
  Bundles `CrashGuard.log`, `guardconfig.json`, BT's `bt-sync-*.txt`, and the newest crash report
  into a zip and uploads it (link to clipboard).

## Config

`Modules/BLTDeploymentCrashGuard/guardconfig.json` is auto-written on first run with every key
documented inline.

| Key | Default | Meaning |
|---|---|---|
| `safeMode` | `false` | `true` disables ALL guards/fixes/tracers (isolate this mod vs BT) |
| `battleMode` | `auto` | `auto` \| `solo` (always vanilla battles) \| `coop` (always co-op sync) |
| `timeAlwaysFlows` | `true` | campaign time does not auto-pause when your party idles |
| `shareTimeControl` | `true` | host auto-grants the client time control (either player controls speed) |
| `noSickness` | `true` | block the local player's hero dying of old-age illness (cures it instead) |
| `pregnancySync` | `true` | **co-op** — replicate host births to clients so both games share the same child |
| `stashSync` | `true` | **co-op** — settlement stashes stay identical on every machine (shared clan stash) |
| `tracing` | `false` | verbose diagnostic tracers — off for play, on for troubleshooting |
| `selfTest` | `false` | run each fix's decision-logic self-test at startup and log PASS/FAIL |
| `logStreamBin` | `""` | a filebin.net bin id; when set, the log auto-uploads for remote debugging |
| `hotReload` | `false` | **dev only** — no-restart reload of the payload (needs a `.hotreload-dev` marker) |
| `hotReloadRoslyn` | `false` | **dev only** — watch payload `.cs` source and recompile via Roslyn (else watch the prebuilt DLL) |
| `payloadSourceDir` | `""` | **dev only** — path to payload source for Roslyn reload |

## Architecture

The mod ships as two assemblies (see `HOTRELOAD.md`):

- **Harness** (`BLTDeploymentCrashGuard.dll`) — the small, stable module Bannerlord loads:
  lifecycle, logging, health/self-test, config, and the reload engine.
- **Payload** (`BLTDeploymentCrashGuard.Payload.dll`) — all guards, fixes, and tracers.

`SubModule.xml` points at the harness, which loads the payload. In a dev build with hot-reload
enabled, the payload can be rebuilt and reloaded with no game restart.

## Build from source

Requires the .NET SDK and the game installed. Game path is set in the `.csproj` (`GameDir`);
override with `-p:GameDir="..."`.

```
cd Harness && dotnet build -c Release
cd ..\Payload && dotnet build -c Release
```

Deploy **both** DLLs to `<Bannerlord>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/`
next to `SubModule.xml`. There are headless test suites for the pregnancy-sync and stash-sync
wire formats (`tests\BirthPayloadTest`, `tests\StashPayloadTest`), e.g.:

```
cd tests\BirthPayloadTest && dotnet build -c Release && bin\Release\BirthPayloadTest.exe
```

## Known co-op issues still being tracked

See `UPSTREAM_BUG_REPORT.md`. Outstanding items include dedicated-server role dropping to
player-host on an in-game save load, two clients forming separate battles on a dedicated server,
and siege roster truncation — all require a live two-player session to reproduce and fix.
