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

19. **Gates always offer F: Close / F: Open when at rest** — two verified vanilla defects:
    - *Sieges*: the gate's interaction points only activate when the door's animation
      parameter is **exactly** 1.0 — but vanilla parks a closed gate at a frozen 0.99, and
      an opened door can settle a hair under 1.0, leaving a visually-at-rest gate with no
      prompt at all (the "defending, gate open, no F to close it" report). Doors at rest
      now always offer the correct direction; mid-swing doors stay un-interactable, and a
      ram-**destroyed** gate stays gone by design (the log says so with tracing on).
    - *Settlement visits*: civilian missions deliberately force the gate open and disable
      the whole machine. Re-enabled for the player, so the gate can be closed and reopened
      while walking around your castle or town, using vanilla's own animation, nav-mesh,
      and collider handling.

20. **Shared-save host handoff — you always load as YOUR hero** *(`myHero` + automatic)* —
    a Bannerlord save stores exactly one player identity: whoever was MainHero when it was
    saved. Pass a shared co-op save to the other player and they load in as *your* hero.
    BannerlordTogether's identity registry only fixes this for joining clients, never for
    the person hosting the load. Now each machine keeps its own campaign→hero record
    (`hero-identity.json`): on load (as host or solo, never as a client), the player is
    switched back to this machine's hero via the game's own succession mechanism. New
    campaigns record automatically; an existing shared campaign is claimed once by
    setting `"myHero": "YourHeroName"` in guardconfig.json, after which the record
    maintains itself (it also follows death-succession to your heir). So the two of you
    can pass hosting back and forth on one save and each always resumes your own hero.

21. **Hideout sneak-in: explained on screen, command guaranteed** — the new stealth
    hideout ambush dresses your hero in your **stealth outfit** (enemy colors) and withholds
    your troops and orders until you locate the main camp and spring the ambush — by design,
    but it looks like "I spawned as a soldier and can't command". The mod now says so on
    screen when a sneak-in starts, and at the stealth→battle transition guarantees you are
    the team general and own the order controller (vanilla assumes it; co-op battle
    patches make that fragile).

22. **Troops on party creation** *(`partyTroopsOnCreate`, default on)* — vanilla creates a new
    clan party with the **leader only** and silently expects you to find it on the map to give
    it troops. Now the troop exchange opens the moment the party is created (vanilla's own
    manage-troops screen, on the map). Solo and co-op host: immediate; co-op client: waits a
    few seconds for BannerlordTogether to confirm the party. Also explained: the leader popup
    greys out clan members who are prisoners, children, governors, already in or leading
    another party, at sea, or whose gold plus yours is under the party-creation threshold; the
    button is disabled with no free war-party slot (clan tier) or not enough gold — every
    reason is logged under `[CLAN-PARTY]`.

23. **Siege defense: you command everything, and placed formations hold** *(`siegeCommandAll`,
    default on)* — field report: "my party runs off to guard the castle instead of staying
    where I set them down; when the walls are breached they leave and get killed." Decoded
    from the installed build's IL: in a **siege** vanilla's default formation orders end with
    **AI control ON** (`BattleDeploymentHandler.SetDefaultFormationOrders`, run by the player
    side's auto-deploy), and an AI-controlled formation belongs to the castle-defence tactic,
    which marches it to walls / gate / keep, re-plans on a breach, and re-shuffles troops
    between formations (`TransferUnits` / `Split`). A wiped-and-refilled formation goes back to
    the AI too, and inside another lord's army the game demotes you to a sergeant even in your
    own castle. Now, when your team defends a siege: every regular formation is yours the
    moment deployment ends (AI-held ones get a MOVE order to where they stand), nothing hands
    them back to the AI afterwards, and the tactic never moves troops into or out of a
    formation you command. Defending a settlement your clan owns makes you the general.
    Deliberate exceptions keep working: **F6 delegate command**, the vanilla hand-off when you
    fall, and BannerlordTogether's player-down releases on the host. Deployment itself is
    untouched (vanilla's auto-deploy still positions formations first). Solo and co-op host;
    on a co-op client the host's command assignment stays authoritative (host the session to
    command your castle — see #20). Log tag `[SIEGE-CMD]`.

24. **Co-op: each player commands their own army** *(`coopOwnArmyCommand`, default on)* —
    "in co-op I should be able to command my own army while the host commands theirs."
    Read from BannerlordTogether's own rules: the host lets the client command a formation only
    when it holds the client's troops **alone**; the client reports where its troops are once a
    second and the host mirrors it. Vanilla spawns both parties' troops into the same class
    formations, so nothing is ever purely one player's and the client ends up commanding
    nothing. Now, in a live co-op battle, the two armies fight in separate blocks on both
    machines: the **host's** troops (and every AI party on the side) in formations **I–IV**
    (infantry / archers / cavalry / horse archers) and the **client's** in **V–VIII**, same
    order. Applied at spawn, again when deployment ends and every half second, so the Order
    of Battle screen and reinforcements cannot re-mix them. With the blocks clean, BT's own
    approval, order forwarding and ownership filter do the rest: you order your block, your
    partner orders theirs, AI parties follow the host. Player heroes are never moved. Solo
    play is untouched. Log tag `[COOP-CMD]`.

### Diagnostics & robustness

25. **Startup health + self-tests** — every launch logs the build/version, a `MOD HEALTH:` summary
    of which fixes resolved, and (with `selfTest`) a decision-logic self-test per fix. If a core fix
    fails to resolve, BannerlordTogether was likely updated and this mod needs a matching update.

26. **Diagnostics log** — `CrashGuard.log` records battle flow (menu switches, encounters, mission
    launches with caller stacks) and command control (who becomes player-controlled, order/formation
    ownership, a full control map at deployment finish). Verbose tracers are off by default
    (`tracing`). The log rolls a segment past 8 MB and keeps a rolling window of six segments
    (`CrashGuard.log.1` … `.6`), so a busy session's evidence is not overwritten by the next
    rollover. High-frequency tracer lines are coalesced — an identical line that repeats every
    tick logs once, then `[repeat] … ×N in Ys (collapsed)` at most every few seconds.
    Every fix logs under its own tag, so you can grep for what happened: `[AI-GUARD]`
    party-AI, `[CONVO-CAM]` conversation camera, `[INCIDENT-GUARD]` map incidents,
    `[TICK-GUARD]` background-tick throttle, `[GATE]` gate prompts, `[SIEGE-CMD]` siege-defense
    command (formations taken back from the AI, refused hand-offs, stopped troop shuffles), `[COOP-CMD]` co-op
    own-army formation blocks (who commands I–IV / V–VIII, troops re-sorted), `[IDENTITY]` player
    identity / shared-save hero, `[STASH-SYNC]`, `[PREG]` / `[PREG-SYNC]` conception and
    births, `[STEALTH]` hideout sneak-in, `[CLAN-PARTY]` create-party leader list and greyed-out reasons,
    `[CHARGEN]` character-creation / banner-editor lifecycle and any swallowed exception during it
    (with `tracing`), `[BATTLE-MODE]`, `[HOTRELOAD]`. Each launch ends
    its startup with `MOD HEALTH:` (which fixes resolved) and, with `selfTest`, a
    `[SELFTEST]` PASS/FAIL per fix; `GUARD ACTIVITY:` every two minutes lists which guards
    actually fired — a guard that never fires is a bug that never happened.

27. **Safe mode** — `safeMode` disables everything the mod does, to isolate whether an issue is this
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
| `partyTroopsOnCreate` | `true` | open the troop exchange with a new clan party the moment it is created |
| `siegeCommandAll` | `true` | **siege defense** — you command every formation and placed formations hold (no AI hand-off after deployment, no tactic troop shuffles; owner of the settlement = general). F6 still delegates on purpose |
| `coopOwnArmyCommand` | `true` | **co-op** — each player commands their own army: host's troops in formations I–IV, client's in V–VIII, on both machines |
| `myHero` | `""` | **shared-save co-op** — this machine's hero by name; on load you are switched back to it (needed once per existing campaign; new campaigns record automatically) |
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
enabled, the payload can be rebuilt and reloaded with no game restart (each payload build
carries a unique assembly name so the runtime never hands back a previous generation).

### How each fix works

Every fix lives in its own `Payload/*.cs` file whose header explains the bug and the fix. A fix is
a Harmony patch (prefix/postfix/finalizer/transpiler) or a by-name reflection hook into the game or
BannerlordTogether. Game and BT members are resolved by reflection so that a game or BT update
degrades gracefully instead of crashing, and each fix carries a startup self-test that pins the
members and its decision logic (`selfTest`), reports health (`MOD HEALTH`), and logs under its own
tag so you can grep the log for exactly what happened. The single version number lives in
`Directory.Build.props` and is stamped into both assemblies and `SubModule.xml` at build time.

### For developers and AI agents

The how-it-works-so-we-don't-re-derive-it docs:

- **`CLAUDE.md`** — operating guide: architecture, build/deploy (deploy both DLLs + `SubModule.xml`
  to the game module *and* `dist/`, hash-verify; pushing == releasing), and the house rules.
- **`docs/DIAGNOSTICS.md`** — how to investigate a crash without guessing: the IL-probe toolchain,
  runtime tracing, the session-wide first-chance exception capture, log tags, and rotation.
- **`docs/ENGINE-NOTES.md`** — engine/BT facts proven from IL (e.g. the `MovementOrder`
  `beforefieldinit` type-init crash, mission load order, siege command, the BT battle command model).
- **`tools/il-probes/`** — small standalone tools to read the installed game assemblies
  (`NameSearch`, `Inspect`, `IlDump`, `Callers`, `VerCheck`).

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

## Community-reported BannerlordTogether crashes vs. this mod (audit 2026-09-01)

All 66 open reports on BannerlordTogether's Nexus bug tracker (v0.3–v0.5.0.1) were read in full
— none contains an exception or stack trace (the author triages on Discord), so the mapping is by
scenario. BT's own changelog was checked for upstream fixes.

**Covered by this mod:**

| Reported | Fix here |
|---|---|
| Crash when joining an army / when an army joins your battle / attacking with an army (5 reports) | #6 party-AI guards, #18 freeze guard, tracers on the encounter chokepoints |
| "There's another me" — a clone of my character with maxed stats receiving my income (client) | #20 shared-save identity lock (loading the save as the other hero) |
| Client can't control troops / spawns as AI / "have his character as AI" | #12 player-identity guard |
| Marriage didn't work / second player can't marry / duplicate wife+child in a shared clan | #10 marriage fixes; #15 birth sync reconstructs the same child on both machines. The 0.5.0.1 message *"Marriage could not be safely completed by host-owned sync"* is BT's host **persistence commit** failing after the marriage itself validated (decompiled: `TryCommitOwnerMarriageCompletionPersistence`); #10 keeps your dowry gold safe when it happens, and the cause is printed on the host in `bt-sync-host.txt` as `[MARRIAGE] CompletionApply … reason=` |
| Crash opening the clan screen after becoming a mercenary | #4 clan-screen crash guard |
| World freeze while the host is in a tournament and the city gets besieged; freezes during a siege | #18 background-tick freeze guard |
| Crash on winning a battle next to allies / client crash when a battle ends | #1 deployment guards + #12; BT itself fixed the spectator/duplicate-party cases in 0.4.1.4 |
| Client visits a settlement stash / crafted items lost on trade or reload | #16 shared stash; crafted items are the documented machine-local limitation |
| Conversation crash mid-marriage; dead companion returning from a quest crashes | #3, #2 |

**Fixed upstream by BannerlordTogether (per its changelog):** client invincible in battle (0.3.1);
spectator crash after a battle, duplicate-battle-party mid-battle client crash, native loading-window
crash, siege defenders on the wrong side (0.4.1.4); kingdom-screen, fief, crafting-daily-order and
diplomacy-UI crashes (0.5).

**Still open upstream — no root cause reachable from this mod without a live repro** (tracing
captures them; report with `collect-diagnostics.cmd`):

- Co-op **Assault / Friendly Battle** freezes the second player ~5 s in (3 reports, 0.4.1.3–0.4.1.6;
  BT calls the mode experimental).
- **Co-op siege launch**: "launch attack" with both players in the siege camp errors or crashes;
  client removed from the siege start; client group counts differ from the host (4 reports).
- Joining while the host is **inside a town** (BT refuses: "cannot transfer the save during a
  player encounter") — a BT design limit; leave the town first.
- Client-side gaps that are not crashes: no renown from co-op battles, enemy factions ignore the
  client, client caravans earn nothing, client can't upgrade to cavalry despite owning mounts,
  skills not applied in battle, troop banners/firing animations missing for the client, "clan was
  destroyed" when the other player enters a town, castle ownership shown swapped after a load.
- Play-as-soldier: picking up a rock or weapon in a siege ejects the spectator and zeroes the unit.

## Known co-op issues still being tracked

See `UPSTREAM_BUG_REPORT.md` for the evidence behind each. Items this mod cannot fully fix
from the outside (each has a workaround or a guard here, and a BannerlordTogether-side fix on
record):

- **Client bootstrap abort** — the action-cache audit false-negative (fix #9 primes it; BT
  should regenerate the cache instead of aborting).
- **Background campaign tick has no time budget** — throttled by #18; BT should bound the
  per-frame cost.
- **Army-siege attach gap** — a peer's party rides a besieging army without joining the
  besieger camp, so every `PlayerSiege`-derived path reads null on that peer (#8 repairs the
  incident case; `[INCIDENT-GUARD] REPAIRED` lines are the field evidence).
- **Map incidents are not synced** — an incident's world effects apply only on the peer that
  confirmed it.
- **Siege command on a co-op client** — BannerlordTogether's host decides which formations a
  client may command (`BattleCommandAssignmentPacket`, re-applied by the client every few
  seconds), so #23 stands down on a client and logs a `[SIEGE-CMD] co-op CLIENT` note. With
  #24 the client still commands its own block (V–VIII); to command every formation of your
  own castle's defense including the host's and the garrison's, host the session (#20 hands
  the host role back and forth on a shared save).
- **Four formations per player in co-op** — #24 folds each army into infantry / archers /
  cavalry / horse archers so the two blocks stay pure; per-troop formation preferences beyond
  those four (skirmisher, heavy infantry, light/heavy cavalry) are not honoured while a
  remote player is in the battle.
- **No settlement-stash sync in BT** — #16 provides it; player-**crafted** items cannot be
  expressed on the wire (each machine keeps its own).
- **Shared-save identity** — BT's identity registry only fixes the joining client; #20 fixes
  the loading host.
- **Dedicated server**: role drops to player-host on an in-game save load; two clients form
  separate battles (per-client-ghost encounters); siege roster truncation — all need a live
  multi-player session to reproduce further.
