# BLT Deployment Crash Guard

Companion mod for **Mount & Blade II: Bannerlord** that fixes crashes and co-op bugs in
**BannerlordTogether** sessions — and several that also bite in plain single-player. It began
as a fix for the guaranteed siege-deployment crash-to-desktop and has grown into a suite of
self-disabling crash guards and root-cause fixes, plus deep battle diagnostics.

This README documents **v1.3.2**. `CHANGELOG.md` has the per-version detail; the version you are
actually running is printed on the first line of `CrashGuard.log` every launch (see
[Troubleshooting](#troubleshooting)).

## Contents

- [Install (players)](#install-players) — [Manual install (no curl)](#manual-install-no-curl) ·
  [Uninstall](#uninstall)
- [What it does](#what-it-does) — [Crash fixes](#crash-fixes) ·
  [Co-op & gameplay fixes](#co-op--gameplay-fixes) ·
  [Diagnostics & robustness](#diagnostics--robustness)
- [Sharing your log with your co-op partner](#sharing-your-log-with-your-co-op-partner)
- [Troubleshooting](#troubleshooting) —
  [Is the mod actually doing anything?](#is-the-mod-actually-doing-anything) ·
  [Messages the mod puts on screen](#messages-the-mod-puts-on-screen) ·
  [Specific symptoms](#specific-symptoms) ·
  [Collect everything for a bug report](#collect-everything-for-a-bug-report) ·
  [Where to send a bug report](#where-to-send-a-bug-report) ·
  [What gets uploaded](#what-gets-uploaded-read-before-sharing) ·
  [Installer problems](#installer-problems) ·
  [Files this mod writes and renames](#files-this-mod-writes-and-renames) ·
  [What this was proven against](#what-this-was-proven-against)
- [Config](#config)
- [Architecture](#architecture) — [How each fix works](#how-each-fix-works) ·
  [For developers and AI agents](#for-developers-and-ai-agents)
- [Build from source](#build-from-source)
- [Community-reported BannerlordTogether crashes vs. this mod](#community-reported-bannerlordtogether-crashes-vs-this-mod-audit-2026-09-01)
- [Known co-op issues still being tracked](#known-co-op-issues-still-being-tracked)
- [License](#license)

Design principles:

- **It patches native TaleWorlds game methods wherever the defect is the game's**, and it ships no
  third-party mod code. Where the defect is BannerlordTogether's own, a number of BT methods are
  patched directly: the client-bootstrap verify (#10), the clan-mode getter (#11), BT's background
  campaign tick (#19), its `EnforcePlaySpeed` (#14), its encounter-request application (#7), its
  time-key handlers (#18) and its inbound-packet accept hook (#16/#17). Every BT member is resolved
  by name through reflection first, so a BT update degrades the fix instead of crashing the game.
  The only file it touches outside its own
  folder is BannerlordTogether's regenerable `RuntimeDataCache`, which it *renames*, never deletes
  (see [Files this mod writes](#files-this-mod-writes-and-renames)).
- **The guards do nothing while the world is healthy.** Most crash guards are Harmony *finalizers*
  that never run a line unless the underlying bug actually throws. Several are *prefixes* instead —
  the party-AI tick skip (#6), the background-tick throttle (#19), the encounter-loop breaker (#7),
  the map-incident repair (#8) and the two time vetoes (#14) — which means they do run on every
  call, but only to make a cheap check and step aside; the party-AI and map-incident fixes pair that
  prefix with finalizers for anything that still throws, and #9 is a transpiler that rewrites a
  single call site. Root fixes go inert when the bad condition stops occurring: if TaleWorlds or
  BannerlordTogether fixes something upstream, our fix simply never fires again — the guard then
  stops appearing in `GUARD ACTIVITY:` and can be retired. Nearly every guard is fire-counted; the
  `MovementOrder` type-init fix (#9) is not, because it is a one-shot load-time repair rather than a
  recurring guard.
- **Every crash guard reports its health and runs a startup self-test**, so a game or
  BannerlordTogether update that moves a method surfaces immediately instead of silently breaking a
  fix. As of v1.3.2 that includes the ones that used to be silent: the deployment guards (#1), the
  client hero-creation guard (#5), the party-AI guards (#6), the encounter-loop breaker (#7), the
  `MovementOrder` type-init fix (#9) and auto battle mode (#15) all register a `MOD HEALTH:` entry
  and a `<component>.contract` self-test — and, in a second pass, so do the player-identity guard
  (#13), the `BootstrapAborted` watcher half of #10 and all five time fixes (#14, #18). What still
  reports neither: the log streamer and every tracer. For those the startup log line and its
  hooked-method count *are* the health signal — see
  [Is the mod actually doing anything?](#is-the-mod-actually-doing-anything).

## Install (players)

Paste this into a **Command Prompt** (cmd), then press Enter:

```
curl -fsSL -o "%TEMP%\bltguard.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/install.cmd && call "%TEMP%\bltguard.cmd"
```

It finds your Bannerlord install (asks if it can't), downloads the mod into
`Modules/BLTDeploymentCrashGuard`, and you're done. Then in the Bannerlord launcher
(BLSE/LauncherEx), tick **"BLT Deployment Crash Guard"** in the Singleplayer mods list, ordered
anywhere **after** BannerlordTogether. Re-run the same line any time to update.

**The installer verifies what it downloaded.** A release carries `dist/manifest.txt` — the version
plus a SHA256 for each of the three files — and after downloading, `install.cmd` fetches that
manifest and hashes every file with Windows' own `certutil`, printing `Verified 3 file(s) against
the release manifest.` If any hash differs it refuses the install with *"the release may be
mid-update on GitHub, run again in a minute"* — that is what stops you ending up with a harness
from one build and a payload from another. If the manifest cannot be downloaded, or `certutil` is
not available, it prints `(no release manifest or certutil available - skipping the integrity
check)` and installs anyway.

Before you install:

- **Bannerlord.Harmony must already be installed.** It is a separate module (Nexus), `SubModule.xml`
  declares it as a dependency, and nothing in the installer checks for it.
- `SubModule.xml` declares Native / SandBoxCore / Sandbox at **v1.4.8**, so another game build can
  be reported as incompatible by the launcher. (That is the module manifest the launcher reads, not
  the release manifest `dist/manifest.txt` above, which holds only a version line and three hashes.)
- **BannerlordTogether is an *optional* dependency** — the mod loads and runs in plain
  single-player. The optional dependency does not enforce load order; ticking this mod after
  BannerlordTogether in the launcher is what does.
- Auto-detection only knows **Steam** layouts on drives C:–G:. Epic, GOG, Xbox Game Pass and drives
  past G: are never found automatically — paste the folder at the prompt, or set the override first
  (every script honours it):
  ```
  set BANNERLORD_DIR=D:\Games\Mount & Blade II Bannerlord
  ```

Both players in a co-op session should install it — several fixes protect each machine's own
player, and the co-op sync features need it on both ends. **Run the same mod version on both
machines:** the birth and stash packets carry a format-version byte, and a peer on a different
version drops them rather than misparse, silently and with no popup.

**Updating while Bannerlord is open** succeeds but does not take effect until you fully restart the
game: the installer renames the locked DLLs to `BLTDeploymentCrashGuard.dll.prev` /
`BLTDeploymentCrashGuard.Payload.dll.prev` and downloads the new ones next to them, while the
running game keeps executing the old code. The two `.prev` files (~230 KB) stay behind, are replaced
on each re-run rather than piling up, and are safe to delete.

### Manual install (no curl)

If you would rather not run a script, download the release files from
<https://github.com/goobz22/BLTDeploymentCrashGuard/tree/main/dist> and place them exactly where
`install.cmd` would, under your Bannerlord folder:

| Download from `dist/` | Put it here |
|---|---|
| `SubModule.xml` | `Modules\BLTDeploymentCrashGuard\SubModule.xml` |
| `BLTDeploymentCrashGuard.dll` | `Modules\BLTDeploymentCrashGuard\bin\Win64_Shipping_Client\` |
| `BLTDeploymentCrashGuard.Payload.dll` | `Modules\BLTDeploymentCrashGuard\bin\Win64_Shipping_Client\` |

Create the folders if they don't exist, and take **both** DLLs from the same download — a harness
without its matching payload loads cleanly and applies nothing. Also grab `manifest.txt` from the
same folder and check the two DLLs and the XML against it yourself if you want the same integrity
check the installer does:

```
certutil -hashfile "BLTDeploymentCrashGuard.Payload.dll" SHA256
```

Then tick the mod in the launcher after BannerlordTogether, exactly as above.

### Uninstall

1. Untick **"BLT Deployment Crash Guard"** in the Bannerlord launcher's Singleplayer mods list.
2. Delete the folder `Modules\BLTDeploymentCrashGuard` (it holds the DLLs, `CrashGuard.log` and its
   rotated segments, `guardconfig.json`, `hero-identity.json` and `bootstrapwatch.state` — nothing
   the game itself needs). Do it with Bannerlord closed; the shadow-copied payload DLLs stay locked
   while the game runs.
3. Only if `BootstrapWatch` ever renamed BannerlordTogether's cache: in
   `Modules\BannerlordTogether\RuntimeDataCache\` you may find files named `*.rdc.stale-<timestamp>`.
   Nothing was deleted — rename each back to `*.rdc` if you want BT's old cache returned. Leaving
   them is also fine: BannerlordTogether regenerates that cache by itself.

Nothing is written outside those two folders, so there is nothing else to clean up.

## What it does

### Crash fixes

1. **Siege / battle deployment crash** — vanilla `DeploymentMissionController.SetupTeams()` and
   `FinishDeployment()` dereference `Mission.InitialPlayerAgent` without null checks; a co-op
   battle that starts with no player agent spawned is an instant CTD. Finalizers suppress the
   crash, log it, and best-effort complete the deployment tail so the mission survives — including
   restoring player control and AI ticking.

   *Limitation, plainly: this suppresses the crash-to-desktop; it does **not** restore the missing
   player-side troops.* BannerlordTogether never rosters or spawns the player side during team setup
   when you host alone, and that is untouched: a guarded battle can still open with every player
   formation `0/0` ("Formation is currently empty") while your party is full and unwounded. What
   prevents the empty player side is **#15 auto battle mode**; these two finalizers are the last
   line for when it cannot. If you see the orange "prevented a deployment-setup crash" notice, the
   crash was caught but the battle may still be empty — check `battleMode`.

   Everything these guards log carries the tag **`[DEPLOY-GUARD]`**: `SUPPRESSED crash in
   DeploymentMissionController.SetupTeams` / `.FinishDeployment`, the `recovery …` lines that name
   which tail step had to be replayed (`player agent handover`, `AllowAiTicking`, `DisableDying`,
   `OnAfterDeploymentFinished`, `AfterDeploymentFinished`, `RemoveMissionBehavior`) and
   `FinishDeployment recovery failed` if the replay itself could not run. Each tail step runs in its
   own try/catch, so one failing step no longer aborts the rest. That replay is still a
   hand-maintained mirror of vanilla's own deployment tail, so a game update that changes the tail
   can make the recovery incomplete without any error.

   Because the two finalizers are attached by attribute (`PatchAll` reports nothing), a separate
   check runs after patching, confirms both finalizers really are on `SetupTeams` and
   `FinishDeployment`, and reports the `deployment-guards` health component (critical) plus the
   `deployment-guards.contract` self-test. The startup line reads `[DEPLOY-GUARD] deployment crash
   guards active — SetupTeams=guarded FinishDeployment=guarded`; `DEGRADED` there means one of them
   did not attach.

2. **Dead-hero reactivation (issue quests)** — when companions you sent away as an issue's
   "alternative solution" return, the game reactivates every hero among them **without checking if
   they're still alive**. A companion who *died* while away gets reactivated, which crashes on the
   character-development event. Two root fixes: dead heroes are stripped from the returning roster
   (they can't return — they're dead), and a domain invariant blocks any dead→Active hero
   transition everywhere. The troops a dead companion was carrying leave the returning roster with
   them. Logged under `[DEADHERO]`.

3. **Conversation-camera crash** — `MissionConversationCameraView.MakeSpeakerLookToListener` NREs
   when a conversation agent is removed mid-dialog (e.g. a marriage applying while the dialog is
   still open). A finalizer on that method **and on `UpdateAgentLooksForConversation`** skips the
   offending camera frame instead of crashing: the cost is one frame of camera look-at and the
   conversation continues.

4. **Clan-screen crash** — the clan tab (`GauntletClanScreen.CreateDataSource`) can NRE on a
   half-synced clan/party graph on a co-op client. A finalizer closes the screen safely instead
   of a CTD. It does not repair the half-synced graph: re-opening the tab in the same state closes
   it again until BannerlordTogether's clan sync catches up.

5. **Client hero-creation crash** — `FindMostSuitableHomeSettlement` NREs on a half-synced
   clan/faction during a client's culture selection; guarded so client character creation
   completes. The guard substitutes the same fallback the method itself uses in its own edge cases
   — your clan's initial home settlement, or the first settlement in the world if there is none —
   so a client created during a bad sync can end up with an unexpected home settlement. Check it
   once the world has finished syncing; every suppression is logged under `[HEROCREATE-GUARD]`
   with the fallback it used. If the recovery itself fails — or the world holds no settlement to
   fall back to — the guard returns no settlement at all and logs `[HEROCREATE-GUARD] recovery
   failed:`; the crash can then resurface further along, so report that line. Health component
   `hero-creation-guard`, self-test `hero-creation-guard.contract`.

6. **Party-AI crashes** — `MobilePartyAi.GetBehaviors` / `EncounterManager.HandleEncounterForMobileParty`
   NRE on half-synced parties during join races; guarded. In practice: a party caught mid-sync has
   its AI tick skipped, and any other throw makes that one party **hold position for a single tick**
   instead of crashing — so right after someone joins you may see an AI party pause for a moment.
   It self-heals the instant BannerlordTogether finishes syncing that party; the skips are
   summarised under `[AI-GUARD]` at most once every 5 seconds. Note the first of its three layers is
   a **prefix**, not a finalizer: it runs on every party AI tick and skips the tick for a party in
   the one proven-inconsistent state, with no exception in sight. Health component `party-ai-guard`,
   self-test `party-ai-guard.contract`.

7. **Encounter-loop breaker** — breaks the infinite encounter-meeting re-open loop that could hang
   a co-op session. **Fixed in v1.3.2: it now works with tracing off.** The breaker only counts an
   encounter request toward its trip when it closely follows a local `PlayerEncounter.Finish`, and
   until v1.3.2 that `Finish` was stamped only by a tracer patch that existed when `"tracing": true`
   — so with the default config the breaker could never trip. It now hooks `PlayerEncounter.Finish`
   itself, always-on, and needs no config change.

   The `[ENCOUNTER-GUARD] encounter-request loop breaker active (N method(s); local-Finish stamp
   hooked=True)` line proves it attached to BannerlordTogether; `LOOP BROKEN:` is a fire. It also
   reports the `encounter-loop-guard` health component and an `encounter-loop-guard.contract`
   self-test, so its state is never silent: **healthy** when BT is absent (that is on purpose, not a
   failure — it is counted in `MOD HEALTH:`'s `N active` and, being healthy, is not named on the
   line), and **NOT resolved** when BT
   *is* loaded but `BattleSyncBehavior` or `ApplyEncounterRequestNow` cannot be found — the signal
   that a BT update renamed them. It suppresses BT's re-application rather than consuming the stuck
   request, so the upstream defect remains. Its trip threshold (4 requests), its window (15 s), its
   retry hold (60 s) and the window it treats as "closely follows a `Finish`" (4 s) are compile-time
   constants with no config key.

8. **Map-incident siege crash** — vanilla's map-incident popups apply their siege-progress effect
   through `PlayerSiege` with no null check: confirm an incident option after the siege is gone
   (or while your party isn't attached to your army's siege in co-op) and the game CTDs. The fix
   is repair-first: if your army's siege is live, the real effect is applied to it; only when no
   siege exists anywhere does it report "the siege has already ended". Any *other* incident
   effect that throws on stale world state is caught at the same choke point and skipped instead
   of crashing.

   Half of this one is not a co-op bug at all: leaving a map-incident popup open until the siege
   ends and *then* confirming CTDs in **plain single-player** too. The whole fix is inert on a game
   build older than **v1.4.8**, which has no map incidents to patch — and it then reports itself
   NOT resolved (see Troubleshooting).

9. **Battle-load crash: `MovementOrder` type-init (affects v1.3.0–v1.3.1 — update)** —
   `TaleWorlds.MountAndBlade.MovementOrder` is a `beforefieldinit` struct whose static constructor
   builds six default orders through an instance constructor that reads `Mission.Current.CurrentTime`.
   Because the type is `beforefieldinit`, the CLR may run that static constructor at any point before
   the first static-field access — including while a mod (this one, in v1.3.0) Harmony-patches
   `Formation` / `OrderController`. With no mission alive `Mission.Current` is null, the type
   initializer throws, and .NET **caches that failure permanently**: every battle for the rest of the
   game session then dies at `Formation.ResetAux`. Fixed in v1.3.2: a transpiler makes that one read
   null-safe (time 0 when there is no mission — the six template orders don't use it), and the static
   constructor is then forced to run immediately under the patched constructor, so the type is cached
   successfully initialized for the whole process. It applies solo and in co-op, runs **first**,
   before any other patch, and logs its outcome as `[MO-INIT] MovementOrder initialized safely`.
   Being a load-time fix it needs a **fresh game launch** — a hot-reload cannot deliver it. It
   reports the `movementorder-typeinit` health component (critical) and a
   `movementorder-typeinit.contract` self-test that re-resolves the constructor, re-checks the
   premise the fix rests on (`MovementOrder` is still a `beforefieldinit` struct), asserts exactly
   **one** transpiled site, and calls the null-safe time helper. If a game update ever makes the
   premise false, that self-test says so rather than the fix quietly becoming pointless. It is one
   of the few fixes with no fire count — the solo clan-mode fix (#11) and the four time fixes (#14)
   have none either — because it is a one-shot load-time repair, not a recurring guard. Root cause
   proven from IL: `docs/ENGINE-NOTES.md`.

### Co-op & gameplay fixes

10. **Client bootstrap fix** — the root cause of "invisible armies / joins not registering / total
    desync" as a co-op client. BannerlordTogether's client action-cache audit false-negatives on a
    stale static mirror and permanently aborts, leaving the whole client session with its sync
    patches unapplied. We prime the mirror from the live catalog so verification passes and BT's
    deferred patches actually apply. `BootstrapWatch` also detects a silent `BootstrapAborted`,
    warns you on screen and renames BT's stale cache files so BT rebuilds them — but the cache is
    not the cause; the mirror priming is what actually fixes the session.

    The fix runs **once per game launch**, before BannerlordTogether's single bootstrap audit. BT
    never re-audits, so a client that already aborted this session cannot be repaired without a
    restart; and BT never persists a good cache, so the mismatch recurs on every launch and this fix
    re-primes each time. When it works the log carries `[CLIENT-FIX] native catalog confirmed ready;
    primed N ActionIndexCache mirror field(s)` and the screen shows *"co-op sync patches verified —
    client bootstrap fixed"*. Before overriding anything it asks whether the bug is still there: if
    every mirror is already primed it stands down and logs which explanation applies — `mirrors
    already primed this session (by us)`, or `action-cache mirrors already primed and we never
    intervened — bug not present, BT/engine handles it (fix is dormant)`, which means it can be
    retired.

    The priming half reports the `client-bootstrap-fix` health component and a
    `client-bootstrap-fix.wiring` self-test. `BootstrapWatch` — the half that detects a silent
    `BootstrapAborted` and renames BT's stale cache — reports neither, but since v1.3.2 each abort
    it handles is fire-counted, so `bootstrap-watch=N` shows up in `GUARD ACTIVITY:`.

11. **Marriage fixes** — two decompile-proven BannerlordTogether defects:
   - *Solo clan-mode block*: BT gates marriage on clan-mode sync, which can never complete when you
     host with no one connected, so marriage is blocked forever. When you're provably alone we
     report the correct solo clan-mode so marriage works; the instant a peer connects, BT's real
     sync takes over untouched.
   - *Atomic dowry*: BT routes the marriage to host validation but lets the **gold** apply
     natively in the same barter — so a rejected marriage still took your money. The whole barter
     now cancels *before* any gold moves if the marriage can't complete.

    What you see: the barter closes with *"marriage barter cancelled BEFORE any gold moved — co-op
    clan sync isn't ready yet, try again in a moment"*. The marriage does not happen and nothing is
    lost; re-open the barter once your partner's identity has finished syncing and it goes through.
    The cancel only ever triggers inside a live co-op session while BannerlordTogether's clan mode
    still reads Unknown — solo play, and co-op once clan sync has landed, pass straight through to
    vanilla, and if BT's state cannot be read at all the barter is allowed rather than blocked.
    Logged under `[MARRIAGE-GUARD]` and `[CLANMODE-FIX]`.

12. **No death by sickness** — Bannerlord's old-age "illness" can kill your hero. This blocks the
    death roll for the local player's hero and cures an in-progress illness (aging of everyone else
    is untouched). Config `noSickness`. Only **your own** hero is protected — companions and NPC
    lords still catch the illness and die of it, so the world keeps ageing; each machine protects
    its own player, so in co-op both players need the mod. It **coexists** with the standalone
    *NoSickness* mod rather than detecting it: this guard never increments ill days, so once it
    cures a hero that mod's own prefix sees a healthy hero and passes through. (There is no
    detection of that mod anywhere in the code. As of v1.3.2 the `_noSickness` doc string in the
    generated `guardconfig.json` says "coexists" as well — the old "stands down automatically if the
    third-party NoSickness mod is installed" wording was wrong and has been removed.) Logged under
    `[NOSICK]`.

13. **Player-identity guard (co-op)** — fixes the spawn identity swap where you spawn AI-controlled
    and the other player's hero becomes "you"; moves control back to your own hero and repairs
    team/order/formation ownership. It checks once a second during a campaign mission and corrects
    at most **five times per battle** (a cap so it can never fight another system in a loop), stays
    out of the way entirely while the **deployment phase** runs (control being unassigned there is
    normal), and does nothing if your own hero has no live agent in the mission (spectating). When
    it acts it says so on screen: *"fixed player identity — you are back in control of your own
    character"*. It is a corrective net over a BannerlordTogether defect, not a prevention — expect
    a brief moment as the wrong character. Since v1.3.2 every correction is fire-counted, so
    `player-identity-guard=N` appears in `GUARD ACTIVITY:` — that is how you tell whether the swap
    still happens on your machine at all (and, one day, that it can be retired). Since this release
    it also reports `player-identity-guard` in `MOD HEALTH:`, runs a
    `player-identity-guard.contract` self-test that pins the mission/agent members it writes, and
    watches the one place the swap can happen: the game grants player control only to your own
    hero, so when anything assigns it to another hero the guard logs
    `[IDENTITY] SWAP AT SOURCE …` with the live stack — the line that names the code to fix.

14. **Time fixes (co-op)** — four separate fixes:
    - *Keep fast-forward through map clicks* — exactly one transition is vetoed: the
      **unstoppable** fast-forward BannerlordTogether enforces being downgraded to normal speed by a
      map click. Vanilla's own stoppable fast-forward is still governed by the game's "map double
      click behavior = keep speed" option. Clicking while paused still unpauses. (`[CLICK-SPEED]`)
    - *Don't auto-pause when your party idles* (`timeAlwaysFlows`) — this one is **not co-op-only**:
      it patches the game's own party code and applies in plain single-player too, so campaign time
      keeps running when you arrive at a destination instead of stopping there. Only **your** party
      is affected — AI parties keep vanilla waiting behaviour, wait menus still run to completion,
      and every real pause (pause button, menus, encounters) is untouched. (`[TIME-FLOW]`)
    - *Shared time control* (`shareTimeControl`) — the host auto-grants the client time control so
      either player can pause/play/fast-forward, with an on-screen notice when the grant lands.
      It runs on the authority only, targets the single gameplay client (correct for a two-player
      session), and happens **once per launch**: after that the mod stops touching the setting, so
      if you deliberately turn client time control off again it stays off. Restart to re-grant.
      (`[SHARE-TIME]`)
    - *Neutralize stale co-op speed enforcement* — a **solo-host** fix, despite living here: after
      an in-game save load BannerlordTogether keeps forcing its own speed every tick, so those
      writes are blocked while **no** remote player is connected and full enforcement returns
      automatically within about 2 seconds of someone joining. It cannot affect host↔client sync
      because it never acts when a client exists. (`[TIME-GUARD]`)

15. **Auto battle mode** — hosting a co-op session *alone*, BT's battle pipeline can strip your side
    out of missions (empty formations, no player agent). In `auto` mode the mod checks whether a
    remote player is actually connected: alone → BT's battle patches are lifted (stashed) for pure
    vanilla battles; a peer connected (or you're the client) → every stashed patch is restored under
    its original owner/priority so co-op battle sync is fully intact.

    **Fixed in v1.3.2 — this used to be broken with the default config.** The decision re-runs at
    six points, and all six are now always-on: mod startup, the launcher/module screen, every
    campaign load, every mission's initialization, and the two battle chokepoints this fix now hooks
    itself — `PlayerEncounter.StartBattle` and `MissionState.OpenNew`. Those last two are the ones
    that matter, and until v1.3.2 they lived in the tracer and therefore existed only with
    `"tracing": true`. Field evidence from every log segment of the 2026-09-04 audit: the only
    decision that ever actually lifted BannerlordTogether's 24 battle patches was `start-battle` —
    BT installs those patches *after* our game-start decision, and the pre-mission half of them
    (`MapEventSide.MakeReadyForMission`, the troop-supplier model, Order of Battle) runs *before*
    mission init. So with tracing off (the default) the first solo battle of a session ran with the
    player side stripped. With the chokepoints always hooked, a friend joining or leaving
    mid-session flips the mode before your next battle with no restart, tracing or not.

    Only *battle* methods are ever lifted — 24 of them across deployment, troop spawn, order of
    battle, battle end and battle-agent logic; campaign/map co-op machinery is deliberately never
    touched, so the overworld stays synced in every mode.

    It now reports the `battle-mode` health component and a `battle-mode.contract` self-test (which
    re-resolves all 24 lift targets and both chokepoints, and re-verifies the decision table, the
    own-patch owner filter and the `battleMode` config parser including the legacy
    `soloVanillaBattles` key). A missing **chokepoint** is critical — without it solo battles strip
    the player side again. A lift target that no longer resolves is reported as degraded rather than
    critical, since it only costs the one method, and it is now logged once — `[BATTLE-MODE] lift
    target type not found: <Type> — its BT patches cannot be lifted (game update?)` or
    `[BATTLE-MODE] lift target method not found: <Type>.<Method> (game update?)` — instead of being
    silently skipped. The startup line reads `[BATTLE-MODE] battle chokepoints hooked — chokepoints
    StartBattle=True OpenNew=True; lift targets 24/24 method(s)`.

    In `auto` the session check can come back **unreadable** (BannerlordTogether absent, updated, or
    its state not legible yet). When that happens the mod deliberately leaves co-op battle sync
    fully intact rather than risk stripping it out from under a connected partner — so a solo host
    in that state can still hit the empty-battle bug. The escape hatch is one line in
    `guardconfig.json`: `"battleMode": "solo"`. The log names the branch it took:
    `[BATTLE-MODE] CO-OP battles active (auto: state unreadable — failing safe to co-op
    (battleMode=solo forces vanilla), …)`.

    When the mode actually changes you get an orange `[Deploy Guard]` message: *battles set to
    native/vanilla (…)* or *co-op battle sync restored (…)*, with the reason in brackets
    (`config=solo`, `auto: remote player connected`, …). No message means nothing moved.

16. **Co-op pregnancy / birth sync** *(`pregnancySync`, default on)* — BannerlordTogether
    disables pregnancy for the client (host rolls run normally) and never replicates births, so
    a client's family never grows and a host's children never appear on the client.
    Host-authoritative fix: the host serializes each newborn's identity and broadcasts it over
    BT's own network channel; the client reconstructs the identical child (same id, parents,
    appearance) and the arrival is announced on screen — *a child was born in your co-op family:
    &lt;name&gt;* — alongside the `[PREG-SYNC] reconstructed child …` log line.
    Conception itself follows vanilla rules — the daily roll happens when the
    spouses are in the same settlement (waiting inside a castle with your spouse counts) or the
    same party, ages 18–45; every conception is logged and the player clan's shows on screen.
    (A newborn is an age-0 infant in **Clan → Members**, not visible on the map until coming
    of age.) The broadcast is live-only: nothing is sent unless a peer is connected at the moment
    of birth, and there is no backfill (see
    [Known co-op issues](#known-co-op-issues-still-being-tracked)).

17. **Co-op shared settlement stash** *(`stashSync`, default on)* — BannerlordTogether has no
    stash sync at all (it syncs the workshop warehouse, but a stash deposit exists only on the
    machine that made it — so same-clan players never actually share a stash, and a client's
    deposits silently diverge from the host). Now, closing a stash screen broadcasts that
    settlement's full stash contents over BT's own channel and every other machine applies it,
    so the stash behaves like one shared chest — deposit on one machine, withdraw on the other.
    The host relays client updates so all peers converge; applying waits if you have that stash
    open; on a simultaneous edit the last-closed screen wins. Limitation: a player-crafted item
    can't be expressed on the wire, so crafted stacks stay machine-local — excluded from the
    sync and preserved through every update, each machine keeping its own.
    Inert outside co-op.

    The trigger is a *committed* stash screen (Done) for the settlement you are currently in:
    cancelling out of the screen sends nothing, and only that settlement's stash is snapshotted —
    stashes you are not standing in stay as they were until someone opens and closes them. While
    you have a stash screen open, incoming updates are queued rather than applied; nothing is
    dropped, and every queued update is applied in order the moment you close the screen, so what
    you see refresh on close is your partner's latest state.

18. **Join-hold pause escape** — when someone joins your hosted session, BannerlordTogether
    pauses the campaign for their entire save download + load + hero creation (its "keep playing
    while they load" fast-join only applies to returning players when another gameplay peer is
    already connected). During that hold your pause key is **silently swallowed** — it only
    toggles the manual pause reason and can never clear the join's SaveSync/HeroCreation hold, so
    a joiner stuck in a retry loop freezes the host forever. Now a swallowed unpause explains
    itself on screen (who is holding time and why), and pressing a time key again within 6 seconds
    cancels the stuck join via BT's own transfer-cancel routine — the same recovery its timeout
    watchdog uses — so you resume playing and the joiner is told to reconnect. Any handled time
    key counts, not just pause: the host's normal-speed key arms and cancels the same way.
    Cancelling is destructive to the joiner's in-flight save transfer — they must reconnect and
    download again.

    The escape only appears when a SaveSync/HeroCreation hold can actually be *read* as active. If
    that state is unreadable the mod stays silent rather than offering to destroy a healthy join,
    so no on-screen message during a join hold means the state could not be read, not that there
    is no hold. Logged under `[JOIN-ESCAPE]`.

19. **Background-tick freeze guard (co-op)** — while the host is in a battle, BannerlordTogether
    keeps the campaign world running by ticking it on *every* frame with no time budget; when a
    campaign tick turns expensive (e.g. a third army joining your ongoing battle), every frame
    drowns in background work and the game freezes with all cores pegged — potentially forever.
    The guard puts an equal-time throttle on that background tick: blow the 100 ms budget and
    background ticking pauses for as long as the tick took (capped 10 s), so the game always
    keeps roughly half of wall time. Not a disable — the co-op world keeps running, but in
    **bursts**: during a throttle window background ticking is fully stopped, so the co-op map can
    visibly lag behind during a heavy battle. The budget and the cap are compile-time constants
    with no config key.

20. **Gates always offer F: Close / F: Open when at rest** — two verified vanilla defects:
    - *Sieges*: the gate's interaction points only activate when the door's animation
      parameter is **exactly** 1.0 — but vanilla parks a closed gate at a frozen 0.99, and
      an opened door can settle a hair under 1.0, leaving a visually-at-rest gate with no
      prompt at all (the "defending, gate open, no F to close it" report). Doors at rest
      now always offer the correct direction; mid-swing doors stay un-interactable, and a
      ram-**destroyed** gate stays gone by design (the log says so with tracing on).
    - *Settlement visits*: civilian missions deliberately force the gate open and disable
      the whole machine. Re-enabled for the player, so the gate can be closed and reopened
      while walking around your castle or town, using vanilla's own animation, nav-mesh,
      and collider handling. The settlement half only runs for civilian gates and only when
      there is a local player team to hand the gate to; in any other scene the gate is left
      exactly as vanilla scenery. It also leaves the open-state nav-mesh flags exactly as vanilla
      set them, so pathing while the gate is open is unchanged — closing goes through vanilla's
      own `CloseDoor`.

21. **Shared-save host handoff — you always load as YOUR hero** *(`myHero` + automatic)* —
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

    The switch happens on the **campaign map just after the campaign comes up**, never inside a
    mission (swapping who you are mid-battle would break the mission's agents, teams and
    controllers — exactly what #13 exists to repair). It is also completely inert when you join as
    a client: BannerlordTogether assigns a joining client's hero through its own claim flow. On an
    existing shared campaign with no record and no `myHero` it refuses to guess which hero is
    yours — it logs guidance once per session and changes nothing.

22. **Hideout sneak-in: explained on screen, command guaranteed** — the new stealth
    hideout ambush dresses your hero in your **stealth outfit** (enemy colors) and withholds
    your troops and orders until you locate the main camp and spring the ambush — by design,
    but it looks like "I spawned as a soldier and can't command". The mod now says so on
    screen when a sneak-in starts, and at the stealth→battle transition guarantees you are
    the team general and own the order controller (vanilla assumes it; co-op battle
    patches make that fragile). The repair runs at those transitions only — if something takes
    ownership back later in the ambush it is not re-repaired. Logged under `[STEALTH]`.

23. **Troops on party creation** *(`partyTroopsOnCreate`, default on)* — vanilla creates a new
    clan party with the **leader only** and silently expects you to find it on the map to give
    it troops. Now the troop exchange opens the moment the party is created (vanilla's own
    manage-troops screen, on the map). Solo and co-op host: immediate; co-op client: waits about
    three seconds for BannerlordTogether to confirm the party. Also explained: the leader popup
    greys out clan members who are prisoners, children, governors, already in or leading
    another party, at sea, or whose gold plus yours is under the party-creation threshold; the
    button is disabled with no free war-party slot (clan tier) or not enough gold — every
    reason is logged under `[CLAN-PARTY]`.

    The screen only opens from the map, exactly like vanilla's own manage-troops flows: the mod
    closes the clan screen first, and it will not push the party screen over a mission, over a
    party screen you opened yourself, or over an inquiry/encyclopedia — it waits for the map, up
    to a 15-second timeout (see
    [Known co-op issues](#known-co-op-issues-still-being-tracked)).

24. **Siege defense: you command everything, and placed formations hold** *(`siegeCommandAll`,
    default on)* — field report: "my party runs off to guard the castle instead of staying
    where I set them down; when the walls are breached they leave and get killed." Decoded
    from the installed build's IL: in a **siege** vanilla's default formation orders end with
    **AI control ON** (`BattleDeploymentHandler.SetDefaultFormationOrders`, run by the player
    side's auto-deploy), and an AI-controlled formation belongs to the castle-defence tactic,
    which marches it to walls / gate / keep, re-plans on a breach, and re-shuffles troops
    between formations (`TransferUnits` / `Split`). A wiped-and-refilled formation goes back to
    the AI too, and inside another lord's army the game demotes you to a sergeant even in your
    own castle. Now, when your team defends a siege **and you are the general of that defense**:
    every regular formation is yours the moment deployment ends (AI-held ones get a MOVE order to
    where they stand), nothing hands them back to the AI afterwards, and the tactic never moves
    troops into or out of a formation you command. Defending a settlement your clan owns makes
    you the general; riding in another lord's army defending someone else's castle, the fix
    stands down entirely and vanilla command applies. It covers the regular formations (I–VIII)
    only — the general's and bodyguard formations are never taken back or protected.
    Deliberate exceptions keep working: **F6 delegate command**, the vanilla hand-off when you
    fall, and BannerlordTogether's player-down releases on the host. A formation you hand to the
    AI with F6 goes back under the castle-defence tactic completely — it can be marched and
    re-shuffled again. Deployment itself is untouched (vanilla's auto-deploy still positions
    formations first). **Sally-out** battles are excluded on purpose: sallying out is an attack,
    not a hold-your-ground defense, so vanilla's AI-control-on default still applies there.
    Solo and co-op host; on a co-op client the host's command assignment stays authoritative
    (host the session to command your castle — see #21). Announced once per battle on screen:
    *Siege defense: you command all N formations — they hold where placed (F6 delegates one to
    the AI)*. Log tag `[SIEGE-CMD]`; the IL evidence is in `docs/ENGINE-NOTES.md`.

25. **Co-op: each player commands their own army** *(`coopOwnArmyCommand`, default on)* —
    "in co-op I should be able to command my own army while the host commands theirs."
    Read from BannerlordTogether's own rules: the host lets the client command a formation only
    when it holds the client's troops **alone**; the client reports where its troops are once a
    second and the host mirrors it. Vanilla spawns both parties' troops into the same class
    formations, so nothing is ever purely one player's and the client ends up commanding
    nothing. Now, in a live co-op battle, the two armies fight in separate blocks on both
    machines: the **host's** troops (and every AI party on the side) in formations **I–IV**
    (infantry / archers / cavalry / horse archers) and the **client's** in **V–VIII**, same
    order. Applied at spawn, again when deployment ends and every half second, so anything the
    Order of Battle screen or reinforcements re-mix is sorted back within about half a second.
    With the blocks clean, BT's own approval, order forwarding and ownership filter do the rest:
    you order your block, your partner orders theirs, AI parties follow the host. Neither
    player's hero is ever moved; **companions are not exempt** — they move with the rest of
    their owner's party, so a companion always fights in the block of the party it belongs to.
    Solo play is untouched. Announced once per battle on screen: *Co-op: &lt;host&gt; commands
    I–IV, &lt;client&gt; commands V–VIII (own army each)* — if you never see that line, the split
    did not engage (see Troubleshooting). Log tag `[COOP-CMD]`; evidence in
    `docs/ENGINE-NOTES.md` and `docs/BT-INTERNALS.md`.

### Diagnostics & robustness

26. **Startup health + self-tests** — every launch logs the build/version, a `MOD HEALTH:` summary
    of which fixes resolved, and (with `selfTest`) a decision-logic self-test per fix. If a core fix
    fails to resolve, BannerlordTogether was likely updated and this mod needs a matching update —
    but read the caveats in
    [Is the mod actually doing anything?](#is-the-mod-actually-doing-anything): a fix that is
    *disabled by config* still reports healthy, a guard whose BannerlordTogether type has not
    loaded yet reports healthy as *inert*, and only the log streamer and the tracers register no
    health entry at all.

    When something is NOT resolved the summary line now spells out how to read it: *"read each
    detail: a BannerlordTogether OR game update may have renamed a member; a detail saying 'inert',
    'not loaded' or 'older game build' is on purpose"*. Read that as the rule for reading a detail,
    not as three strings to grep for: no health detail in the mod currently spells any of those
    three phrases. The on-purpose case you will actually meet is the map-incident guard (#8), which
    on a game build older than v1.4.8 reports NOT resolved with the detail `IncidentEffect type not
    found`, and says so plainly in its own line — `[INCIDENT-GUARD] IncidentEffect not found —
    guard inactive (older game build without map incidents)`.

27. **Diagnostics log** — `CrashGuard.log` records battle flow (menu switches, encounters, mission
    launches with caller stacks) and command control (who becomes player-controlled, order/formation
    ownership, a full control map at deployment finish). Verbose tracers are off by default
    (`tracing`). The log rolls a segment once it passes 8 MB (the size is checked every 256 writes,
    so a segment can run slightly over) and keeps a rolling window of six segments
    (`CrashGuard.log.1` … `.6`), so a busy session's evidence is not overwritten by the next
    rollover. High-frequency tracer lines are coalesced — an identical line that repeats every
    tick logs once, then `[repeat] … ×N in Ys (identical, collapsed)` at most every few seconds.
    With `tracing` on, the swallowed-exception capture uses the same mechanism, so its collapsed
    lines read `[repeat] CHARGEN-FC <ExceptionType> @ <Namespace.Type.Method> ×N …`.

    Every fix logs under its own tag, so you can grep for what happened. **Always on:**
    `[DEPLOY-GUARD]` the two deployment crash guards (#1) — new in v1.3.2, these were the one
    component that logged untagged, `[MO-INIT]` the `MovementOrder` type-init guard's load result,
    `[AI-GUARD]` party-AI,
    `[ENCOUNTER-GUARD]` encounter-loop breaker (attach line, `LOOP BROKEN:` fires),
    `[INCIDENT-GUARD]` map incidents,
    `[TICK-GUARD]` background-tick throttle, `[CONVO-CAM]` conversation camera, `[CLAN-GUARD]`
    clan screen, `[HEROCREATE-GUARD]` client hero creation, `[DEADHERO]` dead-hero reactivation,
    `[NOSICK]` old-age illness, `[MARRIAGE-GUARD]` atomic marriage barter, `[CLANMODE-FIX]` solo
    clan mode, `[CLIENT-FIX]` client-bootstrap priming, `[BOOTSTRAP-WATCH]` silent
    `BootstrapAborted` detection and cache clearing, `[IDENTITY]` player identity **and**
    shared-save hero (two components share this tag, so a grep mixes battle-time and load-time
    events), `[STASH-SYNC]`, `[PREG]` / `[PREG-SYNC]` conception and births (`[PREG]` is split too:
    the conception line is always on, but the spouse nearby-check line under the same tag comes from
    a tracer that only exists with `tracing`), `[STEALTH]` hideout
    sneak-in, `[CLAN-PARTY]` create-party leader list and greyed-out reasons, `[GATE]` **both**
    gate fixes plus suppressed gate-tick errors, `[SIEGE-CMD]` siege-defense command (formations
    taken back from the AI, refused hand-offs, stopped troop shuffles), `[COOP-CMD]` co-op
    own-army formation blocks (who commands I–IV / V–VIII, troops re-sorted), `[BATTLE-MODE]` the
    vanilla/co-op battle switch — its config read, the chokepoint-hook line, each VANILLA/CO-OP
    decision with the reason that triggered it, and (new in v1.3.2) a one-time `lift target type
    not found: …` or `lift target method not found: …` line for any of the 24 targets a game update
    moved,
    `[PEER-DETECT]` a BannerlordTogether type lookup that *threw*
    (note that a BT type simply missing — renamed by a BT update — resolves to null silently with
    no `[PEER-DETECT]` line; the earliest warning of a BT update is a fix's own `INACTIVE` /
    `not found` startup line and its `MOD HEALTH:` entry, not this tag), `[TIME-FLOW]` idle
    auto-pause suppression, `[CLICK-SPEED]` fast-forward kept through a map click, `[SHARE-TIME]` client
    time-control grant, `[TIME-GUARD]` blocked co-op speed enforcement, `[JOIN-ESCAPE]` join-hold
    pause escape, `[STREAM]` log auto-upload, `[HOTRELOAD]` (and `[HOTRELOAD][DIAG]`, a one-time
    evidence dump when the payload fails to load).
    **Only with `tracing: true`:** `[TRACE]` mission launches, map-menu opens and player encounters
    with caller stacks, `[CONTROL]` AI-control flips / `SetPlayerRole` / `DelegateCommandToAI` /
    tactic troop transfers, `[TIME]` every time-control mode change with its calling stack,
    `[COOP-BATTLE]` co-op battle formation, `[ROLE]` co-op session role across save loads,
    `[CHARGEN]` character-creation / banner-editor lifecycle plus the first-chance exception
    capture — which is armed once and covers the **whole session**, not just character creation,
    capped at 400 events, `[DIAG]` memory + engine-state heartbeat, `[MO-PROBE]` `MovementOrder`
    construction probe.

    **`[TIME]` now names the vetoer.** Three of this mod's prefixes sit on
    `Campaign.set_TimeControlMode` and Harmony runs all of them even when one refuses the write, so
    the tracer used to be able to say only "suppressed by another patch". Since v1.3.2 the prefix
    that vetoes notes itself, and the follow-up line reads `change SUPPRESSED/ALTERED by
    [TIME-GUARD]` (the co-op speed-enforcement neutralizer), `by [CLICK-SPEED]` (the map-click
    fast-forward keeper) or `by another patch (not one of ours)` — which means a *different mod* is
    fighting over the speed. The `[repeat] … ×N` collapse key includes the vetoer, so two different
    vetoers never collapse into one line.

    `docs/FIX-REFERENCE.md` § *Index 1: log tag → file* is the complete tag→file index;
    `docs/DIAGNOSTICS.md` is the investigation playbook (probes, tracing, first-chance capture,
    rotation) and lists only the tags added in the 2026-09-04 investigation.

    Three lines from *BannerlordTogether* itself are worth knowing when you read a co-op log, all
    in `bt-sync-client.txt` / `bt-sync-host.txt` on your Desktop: `[SPNATIVE ORDER-GUARD] blocked …`
    (BT refusing a client's order on a formation outside its mask — the symptom #25 exists to
    remove), `[HARMONY] BootstrapAborted reason=…` (the failure #10 fixes) and its counterpart
    `[HARMONY] NativeActionCatalogReady …`, which reports that BT's *native* action catalog loaded
    fine — the proof that a `BootstrapAborted` beside it is a false negative on the stale static
    mirror rather than a genuinely missing catalog.

    Each launch ends its startup with `MOD HEALTH:` (which fixes resolved) and, with `selfTest`, a
    `[SELFTEST]` PASS/FAIL per fix; `GUARD ACTIVITY:` is re-checked every two minutes and logged
    whenever it changed, listing which guards actually fired as `guard-id=count` pairs — so a quiet
    session shows one line, not one every two minutes, and a guard that never fires is a bug that
    never happened. The crash-guard ids you will see there: `setup-teams-guard`,
    `finish-deployment-guard` (#1 — the two finalizers keep their own ids; the health component is
    the single `deployment-guards`), `conversation-camera-guard` (#3), `clan-screen-guard` (#4),
    `hero-creation-guard` (#5), `party-ai-guard` (#6), `encounter-loop-guard` (#7),
    `map-incident-guard` (#8), `bg-tick-budget-guard` (#19). A non-zero count is a crash that was
    caught — attach the log to a bug report.

    Fixes that correct state rather than catch a crash are counted the same way, and v1.3.2 added
    three that were previously invisible here: `battle-mode` (#15, each time patches are lifted or
    restored), `player-identity-guard` (#13) and `bootstrap-watch` (#10's abort detector). The rest:
    `client-bootstrap-fix` (#10),
    `dead-hero-return-fix` / `dead-hero-activate-invariant` (#2), `marriage-barter-guard` (#11),
    `illness-death-guard` (#12), `join-sync-pause-escape` (#18),
    `hero-identity-lock` (#21), `siege-gate-prompt-fix` / `civilian-gate-fix` (#20),
    `stealth-hideout-advisor` (#22), `clan-party-advisor` (#23), `siege-command-guard` (#24),
    `coop-command-split` (#25), `pregnancy-sync` (#16), `stash-sync` (#17). Some fixes are absent
    from this line by design and can never appear on it: the `MovementOrder` type-init fix (#9),
    whose evidence is `[MO-INIT]` because it is a one-shot load-time repair; the solo clan-mode half
    of #11, whose evidence is `[CLANMODE-FIX]`; and the four time fixes (#14), whose evidence is
    their own tags. Absence there means "not counted", never "never fired".

28. **Safe mode** — `safeMode` disables everything the mod does, to isolate whether an issue is this
    mod or BannerlordTogether. It is an isolation switch, not a play mode: it returns **before any
    patch is applied**, including the load-time `MovementOrder` fix (#9), so with it on the guarded
    crashes come back. Use it for one reproduction run, then turn it back off.

## Sharing your log with your co-op partner

Every log line is tagged `[H]` (hosting), `[C]` (client), or `[S]` (solo), so two players' logs
merge into one side-by-side timeline. There is a fourth state: lines written before peer detection
first runs — the whole startup block including the session header, `MOD HEALTH:` and `[SELFTEST]` —
carry `[?]`. The tag is refreshed at most once every 5 seconds, and `[S]` is also what a host gets
when the session state cannot be read, so treat it as a hint rather than proof.

To share yours after a session:

```
curl -fsSL -o "%TEMP%\bltshare.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/share-log.cmd && call "%TEMP%\bltshare.cmd"
```

It uploads `CrashGuard.log` to a file host with a 24-hour link and puts the link on your clipboard
(the full bundle from `collect-diagnostics.cmd` uses 72 hours). Read
[What gets uploaded](#what-gets-uploaded-read-before-sharing) first — nothing is redacted.

## Troubleshooting

### Is the mod actually doing anything?

- **Which build am I running?** The first line of `CrashGuard.log` each launch is
  `===== BLT Deployment Crash Guard vX.Y.Z (harness build YYYY-MM-DD HH:mm) session=… =====`,
  followed by the `MOD HEALTH:` line. The version comes from the **harness** assembly identity at
  runtime, so it cannot disagree with the harness DLL that is loaded. It does not cover the payload
  — that line reports only `payload build HH:mm:ss` — so if you copied files by hand, check both
  DLLs (see [Build from source](#build-from-source)).
- **Ticking it in the launcher is not proof it is running.** The harness loads a second DLL (the
  payload) that holds every fix; if that load fails, the game looks completely normal with all
  fixes off. Two checks: `CrashGuard.log` contains `[HOTRELOAD] gen1 applied (initial)` followed by
  a `MOD HEALTH:` line, and you did **not** see `[Deploy Guard] CRASH GUARD NOT ACTIVE` in game.
  The mod retries the payload load twice (at the main menu and when a campaign starts) and then
  gives up for the session — a restart is the only recovery.
- **"CRASH GUARD NOT ACTIVE — payload failed to load, all fixes are OFF"** means exactly that: you
  are playing unprotected. Re-run the installer one-liner (it fetches **both** DLLs) and restart.
  Since v1.2.0 the mod is two assemblies — `BLTDeploymentCrashGuard.dll` (the harness, the file
  `SubModule.xml` names) and `BLTDeploymentCrashGuard.Payload.dll` (every guard, fix and tracer) —
  and a harness without its payload loads cleanly and applies nothing. If you copied files by hand,
  copy both.
- **A green `MOD HEALTH:` line does not by itself mean a fix is running.** The line is a count plus
  the exceptions: a component that resolved contributes only to the `N active` total and is never
  named, so its detail text never reaches the log at all — only NOT-resolved components are listed,
  each with its detail in brackets. A guard turned off in
  `guardconfig.json` still counts as resolved, a guard whose BannerlordTogether type has not
  loaded yet counts as resolved too (its detail says *inert*), and only the log streamer and the
  tracers register no health entry at all (see the third design principle). For those, check the
  tracer's own `active …` line and its hooked-method count. The line is printed at load and again
  at game start (tagged *re-checked at game start*); read the second one — a guard whose
  BannerlordTogether type loaded late has resolved by then, and the newer report replaces the
  older. Every guard and fix *does* report now, so a fix missing from the
  summary is a real problem rather than a known blind spot. The startup block also carries
  `payload build HH:mm:ss applying on <id>` and, once everything is wired,
  `patches applied; battleMode=<mode> tracing=<bool>` — the one line that states the effective
  battle mode for the session. `FAILED to apply patches:` there means the harness kept the previous
  payload generation.
- **If a core fix shows NOT resolved**, BannerlordTogether was most likely updated — but not
  always, which is why the line ends with *"read each detail: a BannerlordTogether OR game update
  may have renamed a member; a detail saying 'inert', 'not loaded' or 'older game build' is on
  purpose"*. Read the detail in brackets before you act on it. A detail
  naming a specific member (`BattleSyncBehavior not found`, `ApplyEncounterRequestNow not found`,
  `FindMostSuitableHomeSettlement(Clan) not found`, `chokepoints StartBattle=False …`) is the real
  signal that something moved and the mod needs an update — with **one** known exception:
  `map-incident-guard (IncidentEffect type not found)` is what a **game** build older than
  **v1.4.8** looks like, because that build has no `TaleWorlds.CampaignSystem.Incidents` for #8 to
  patch. Its `[INCIDENT-GUARD] IncidentEffect not found — guard inactive (older game build without
  map incidents)` line is the confirmation. The encounter-loop breaker (#7) is *not* in this list:
  with BannerlordTogether absent it resolves healthy, so it is counted and never named here.
- **A fix missing from `MOD HEALTH:` entirely** rather than listed as failed: the hideout sneak-in
  advisor (#22) returns silently on a game build with no stealth-ambush controller, so it appears
  in neither list.
- **Is it this mod or BannerlordTogether?** Set `"safeMode": true` in guardconfig.json and restart.
  Safe mode returns before *everything*, including the load-time `MovementOrder` fix (#9), so
  guarded crashes come back while it is on — that is expected, and it is exactly how you confirm
  the mod is the thing helping. Turn it back off after the isolation run.

### Messages the mod puts on screen

Anything prefixed **`[Deploy Guard]`** in the in-game message feed is this mod talking, not a game
bug. It only speaks when it acts. (That on-screen prefix is the whole mod's name badge — every
message uses it, whichever fix spoke. It is not the same thing as the `[DEPLOY-GUARD]` **log** tag,
which belongs to the deployment crash guards, #1, alone.)

| Message | What it means |
|---|---|
| `prevented a deployment-setup crash` / `prevented a deployment-finish crash` | #1 caught a crash-to-desktop; the full exception is in `CrashGuard.log`. The battle may still be empty — check `battleMode`. |
| `broke a stuck encounter loop` | #7 fired. |
| `battles set to native/vanilla (…)` / `co-op battle sync restored (…)` | #15 changed the battle mode; the bracket says why. |
| `co-op mod did NOT fully load — cache auto-cleared, RESTART THE GAME` | Act on it: BannerlordTogether aborted its bootstrap (#10). |
| `co-op sync patches verified — client bootstrap fixed` | #10 worked; no action. |
| `fixed player identity — you are back in control of your own character` | #13 fired. |
| `playing as <name> — this machine's hero (save was last played as <other>)` | #21 applied the shared-save handoff. |
| `prevented a hero-creation crash (half-synced world) — continuing` | #5 fired. |
| `prevented a clan-screen crash (co-op half-sync) — the clan screen was closed` | #4 fired. |
| `marriage barter cancelled BEFORE any gold moved…` | #11 protected your dowry; retry in a moment. |
| `your sickness was cured (no-sickness guard)` | #12 cured an in-progress illness. |
| `a companion who died while away could not return` | #2 stripped a dead hero from a returning roster. |
| `SNEAK-IN: you are disguised in your stealth outfit…` | #22 explaining the hideout ambush. |
| `Siege defense: you command all N formations…` / `Co-op: <host> commands I–IV, <client> commands V–VIII` | #24 / #25 engaged, once per battle. |
| `shared time control enabled — either player controls speed` | #14 granted the client time control. |
| `time is held by a joining player's sync (…) — press pause again within 6s to cancel their join` | #18: your pause key was swallowed by a join hold. Press a time key again inside the window to cancel that join — destructive to the joiner's in-flight save transfer. |
| `join sync cancelled — time is yours again (the joining player can reconnect)` / `could not cancel the join sync — see CrashGuard.log` | #18 acted on that prompt, or could not. |
| `<name>: party created — opening the troop exchange` / `<name>: new party created with no troops yet — click it on the map…` | #23; the second line means the screen could not be opened right then. |
| `could not open the troop exchange automatically — click the new party on the map to fill it` | #23 gave up (15-second timeout, or the map never came up). |
| `no clan member can lead a new party right now — hover a greyed card for the reason…` | #23 explaining the greyed-out leader list; the reasons are logged under `[CLAN-PARTY]`. |
| `log streaming active` | `logStreamBin` (or `logstream.txt`) is set and the log is auto-uploading — see [What gets uploaded](#what-gets-uploaded-read-before-sharing). |
| `a child was born in your co-op family: <name>` / `<name> is pregnant` | #16. |
| `WARNING: a core BLT-guard fix did not load (BT may have updated)` | A load-bearing fix could not resolve a method. The `MOD HEALTH:` log line names it; check for a mod update. |
| `self-tests: N FAILED (see CrashGuard.log)` | Only with `"selfTest": true`: a fix's decision logic no longer matches the game/BT. |
| `CRASH GUARD NOT ACTIVE — payload failed to load, all fixes are OFF` | The mod is installed but doing nothing — see above. |
| `SAFE MODE active — this mod is doing nothing` | `"safeMode": true` is set in `guardconfig.json`. |
| `hot-reloaded genN (no restart)` / `hot-reload FAILED — kept previous generation` | Dev only (`hotReload`). A failed reload keeps the previous fix set; it never leaves you unpatched. |

### Specific symptoms

- **Every battle crashes to desktop on load?** Grep `CrashGuard.log` for `[MO-INIT]`.
  `MovementOrder initialized safely (patched N site(s))` means #9 is active for this session.
  `MovementOrder was ALREADY poisoned before this guard could patch it` means something initialized
  the type before the mod loaded — that session cannot be saved; restart the game and report the
  line. `transpiler found no Mission.Current.CurrentTime site … (game changed?)` means a game update
  moved the code and the mod needs an update.
- **Solo battle starts with empty formations / no troops / an instant crash?** Grep for
  `[BATTLE-MODE]`. `VANILLA battles active` means #15 did its job and the problem is elsewhere.
  `CO-OP battles active (auto: state unreadable …)` means it could not confirm you are alone and
  stayed safe — set `"battleMode": "solo"` in `guardconfig.json` and restart. If you are on
  **v1.3.1 or older**, this is the bug v1.3.2 fixes: the decision that actually lifts BT's battle
  patches ran only with `"tracing": true`, so the first solo battle of a session was stripped with
  the default config. Update rather than turning tracing on. Also check the startup line
  `[BATTLE-MODE] battle chokepoints hooked — chokepoints StartBattle=True OpenNew=True; lift targets
  24/24 method(s)`: a `False` chokepoint or a count below 24 means a game update moved something and
  part of the fix is not in place.
- **Stuck in an encounter-meeting loop that re-opens forever?** Grep for `[ENCOUNTER-GUARD]`. The
  `encounter-request loop breaker active (N method(s); local-Finish stamp hooked=True)` line at
  startup means it is armed; `LOOP BROKEN:` means it tripped. On **v1.3.1 or older** the breaker
  could not trip at all without `"tracing": true` — v1.3.2 hooks `PlayerEncounter.Finish` itself, so
  updating is the fix. In plain single-player the breaker resolves **healthy** — this loop cannot
  happen without BannerlordTogether — so it is counted in `MOD HEALTH:`'s `N active` and never
  named there. That silence is the expected result, not a missing report.
- **Your partner's army never shows up in a co-op battle?** Check the **host's** log for
  `[BATTLE-MODE] VANILLA battles active`. If the detail says `config=solo`, the host has forced
  vanilla — set `"battleMode": "auto"` (or `"coop"`) on that machine and restart. Both players
  should also be on the same mod version.
- **Why did it decide that?** `[TIME-GUARD] peer state -> …` carries a snapshot of every input the
  session check used: `isClient=… isHost=… server=null|set GameplayPeerIds=N recentPackets=True|False`.
  `sessionType=missing` means BannerlordTogether was not found at all; `recentPackets=True` means
  co-op traffic arrived recently and is trusted over everything else. Include this line in any bug
  report about battle mode.
- **"Co-op is broken as a client: invisible partner armies, my joins don't register, speeds
  desync."** That is BannerlordTogether's bootstrap aborting silently. Check **`bt-sync-client.txt`
  on your Desktop** for `BootstrapAborted`, and `CrashGuard.log` for `[CLIENT-FIX] native catalog
  confirmed ready; primed …` (fix worked) or `[BOOTSTRAP-WATCH] co-op mod reported BootstrapAborted`
  (it did not — restart the game; the stale cache has been renamed aside for you). BT never shows
  this in game, and a session that aborted cannot be repaired without a restart.
- **Time or fast-forward misbehaving?** Grep for the startup lines that prove each time fix
  installed: `[TIME-FLOW] timeAlwaysFlows=true (patched N method(s))`, `[CLICK-SPEED] map-click
  fast-forward keeper active (N click method(s))`, `[TIME-GUARD] EnforcePlaySpeed neutralizer active
  (N method(s))`, `[SHARE-TIME] shared time control enabler active`, `[JOIN-ESCAPE] join-hold pause
  escape active`. A count of `0`, a `[CLICK-SPEED] MapScreen not found — keeper idle` line, or
  `[SHARE-TIME] required method(s) not found … INACTIVE` means the game or BannerlordTogether moved
  a method and that fix is not running. To see *what* is changing the speed, set `"tracing": true`,
  reproduce, and read the `[TIME]` lines (old → new mode plus the calling stack). Since v1.3.2 a
  refused write also names the refuser on the next line — `change SUPPRESSED/ALTERED by
  [TIME-GUARD]` or `by [CLICK-SPEED]` for this mod's own two vetoes, and `by another patch (not one
  of ours)` when a **different mod** is fighting over the speed. That distinction is the whole point
  of the line: three of this mod's prefixes share that setter, and Harmony runs all of them even
  when one refuses. Turn tracing back
  off afterwards: while you host alone, BannerlordTogether asks for its own speed every tick and
  this mod blocks the write, so the request repeats forever — the `[TIME]` tracer coalesces those
  into one line plus a periodic `[repeat] … ×N in Ys (identical, collapsed)`, but it still churns
  the log.
- **Co-op formation blocks (#25) never engage.** The split needs a live BannerlordTogether session
  in which this machine can resolve *both* players' parties through BT's session ghost-hero id.
  Until it can, it re-probes every 2 seconds and does nothing else — no error. Evidence: the
  `[COOP-CMD] active …` line appears at startup but there is no `[COOP-CMD] co-op battle: …`
  announcement and no `re-sorted N troop(s)` line. Solo play is expected to look exactly like this.
- **`[SIEGE-CMD] BT host player-down releases hooked: 0`** at startup is expected without
  BannerlordTogether, and expected for a moment even with it — BT's assembly can load after this
  mod's, so the hooks are retried once from the mission path and then log `hooked late: N`. A line
  naming a single method (`BT release method not found (BT update?): <name>`) means BT renamed that
  one; the other hooks are still in place. **`role controller members not resolved`** means only
  the owner-is-general promotion is reduced — take-over, hand-off refusal and the tactic-shuffle
  block still work.
- **Did the co-op sync features survive a BannerlordTogether update?** Grep the startup block:
  `[STASH-SYNC] stash sync active (doneLogic=True receive=True)` and `[PREG-SYNC] receive hook
  installed (True)` mean both halves resolved. `DEGRADED`, `receive=False` or `installed (False)`
  means BT moved or renamed its packet-accept method and sync is silently off until this mod is
  updated. On the host you should also see `[PREG-SYNC] host birth listener subscribed for this
  campaign` once per loaded campaign.
- **"Is my spouse actually with me?"** Set `"tracing": true` *before* launching (the tracer is
  installed at startup) and watch for `[PREG] nearby-check <hero> & <spouse>: TOGETHER — daily
  conception roll happens` or `… apart, no roll (hero@<place>, spouse@<place>)`. It logs only your
  own clan's couples. `[PREG] conception: <hero> is now pregnant` is logged whenever the roll
  succeeds, tracing or not.
- **"It made me the wrong hero."** #21 uses the game's own succession action, so it is undoable:
  quit without saving, fix `"myHero"` (or delete this campaign's line in `hero-identity.json`) and
  load again. Two behaviours by design: if the recorded hero is **dead or missing** you keep the
  save's current player and the record moves to them; if you deliberately play a *different living*
  hero the record is never overwritten — it treats that as your choice, so re-claim it explicitly
  if you meant it.

### Collect everything for a bug report

```
curl -fsSL -o "%TEMP%\bltdiag.cmd" https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main/collect-diagnostics.cmd && call "%TEMP%\bltdiag.cmd"
```

It zips everything a bug report needs and uploads it (link to clipboard, 72-hour retention). As of
v1.3.2 the bundle contains:

| From | What |
|---|---|
| `Modules\BLTDeploymentCrashGuard\` | `CrashGuard.log` **and every rotated segment** `.1`–`.6`, `guardconfig.json`, `hero-identity.json`, `SubModule.xml` |
| Your Desktop | BannerlordTogether's `bt-sync-host.txt` / `bt-sync-client.txt` / `bt-sync-solo.txt` |
| `%ProgramData%\Mount and Blade II Bannerlord\logs\` | the game's own logs — the 3 newest `rgl_log_*`, the 3 newest `rgl_log_errors_*`, the 2 newest `watchdog_log_*`, the newest `launcher_log_*` |
| `%ProgramData%\Mount and Blade II Bannerlord\crashes\` | the newest crash folder's **text** files (memory dumps are deliberately left out — they are enormous) |
| `%USERPROFILE%\Documents` | the newest `*.html` whose name contains "crash" |

- **After a crash, don't rely on a streamed log alone.** With `logStreamBin` set, uploads happen at
  most once a minute and only when the file has grown, so the last seconds before a crash-to-desktop
  are usually still local-only. Collect from the machine that crashed.
- **My bundle has no crash report in it.** Crash reports do not all land in the same place, so check
  **both**: the Documents root (`%USERPROFILE%\Documents\*.html`), which the collector scans, and a
  *Bannerlord subfolder* of Documents, which it does not — ButterLib can write there, and the
  collector only looks at the top level. If the report you need is in a subfolder, attach it by
  hand. The game's own `%ProgramData%` logs and crash folder *are* collected, so those you do not
  have to fetch yourself.
- **"ERROR: could not create the zip."** The collector builds the bundle with PowerShell's
  `Compress-Archive`. If PowerShell is blocked on your machine the zip step fails, but every
  collected file is still in `%TEMP%\bltguard-diag` — zip that folder yourself and send it, and zip
  it **before** re-running the collector, which clears that folder on every run.
- **Stacks have no line numbers.** Both DLLs ship without PDBs, so a crash report names methods but
  not lines — the `CrashGuard.log` tag lines are what pin the location.
- **All three scripts now search the same 11 Steam locations.** `install.cmd`, `share-log.cmd` and
  `collect-diagnostics.cmd` used to disagree — the collector knew only 6 and would ask for a folder
  the installer had already found. They are kept identical by `tools/lint-scripts.sh`. If yours is
  not a Steam layout on C:–G: at all, set `BANNERLORD_DIR` before running, or paste the path at the
  prompt.

### Where to send a bug report

Open an issue at <https://github.com/goobz22/BLTDeploymentCrashGuard/issues> and attach the
`collect-diagnostics.cmd` link from above — the bundle is the whole picture, and without it almost
every report needs a round trip. Read
[What gets uploaded](#what-gets-uploaded-read-before-sharing) before you paste the link, since
nothing in it is redacted; if you would rather not upload, attach the files from
`Modules\BLTDeploymentCrashGuard\` to the issue directly.

Worth putting in the issue text itself: your mod version (the first line of `CrashGuard.log`), your
game and BannerlordTogether versions, whether you were hosting / joining / solo, and the exact
on-screen message if there was one.

### What gets uploaded (read before sharing)

Both share scripts POST your files **unredacted** to a public anonymous file host
(litterbox.catbox.moe, falling back to 0x0.st). Anyone with the link can read them.
`CrashGuard.log` contains Windows paths (including your user name), save names and hero names; the
diagnostics bundle adds the rotated log segments, `guardconfig.json`, `hero-identity.json`,
`SubModule.xml`, BannerlordTogether's `bt-sync-*.txt`, the game's own `rgl`/watchdog/launcher logs
and your newest Bannerlord crash report — so the bundle exposes more than a single log does, not
less. Single-log links live 24 hours, bundle links 72. If you would rather not
upload, the log is at `Modules\BLTDeploymentCrashGuard\CrashGuard.log` — attach it directly.

`logStreamBin` (or a `logstream.txt` file) is a *continuous* version of the same exposure — see the
Config notes.

### Installer problems

- **The installer can't find my game.** See [Install](#install-players): auto-detection is Steam-only
  on C:–G:; use `BANNERLORD_DIR` or the prompt.
- **It said "Installed successfully" but the launcher doesn't list the mod.** When you type the path
  yourself the installer only checks that a `Modules` folder exists under it — a wrong folder that
  happens to contain one passes, and the files land somewhere useless. Confirm the path also
  contains `bin\Win64_Shipping_Client`, then re-run.
- **I re-ran the installer while the game was open and nothing changed.** Expected — see the update
  note in [Install](#install-players). Restart Bannerlord.
- **"ERROR: downloaded files do not match the release manifest".** The installer hashed what it
  downloaded against `dist/manifest.txt` and one file disagreed — most often because a release was
  mid-upload on GitHub when you ran it. Run the one-liner again in a minute. Nothing was left
  half-installed that a successful re-run won't replace; if it keeps failing, report it with the
  exact file name the message printed.
- **"(no release manifest or certutil available - skipping the integrity check)".** Harmless: either
  GitHub did not serve `dist/manifest.txt` or `certutil` is not on your PATH. The files still
  downloaded; you can hash them yourself against `dist/manifest.txt` (see
  [Manual install (no curl)](#manual-install-no-curl)).

### Files this mod writes and renames

In its own folder (`Modules/BLTDeploymentCrashGuard/`): `CrashGuard.log` (+ `.1`–`.6`),
`guardconfig.json`, `hero-identity.json` (this machine's campaign→hero record, #21),
`bootstrapwatch.state` (which BT aborts it has already acted on), and `logstream.txt` if log
streaming was enabled at install time. The two small state files are plain text you can read and
edit with Notepad:

| File | Format | Example |
|---|---|---|
| `hero-identity.json` | a flat JSON object, one `"<campaignId>": "<heroStringId>"` line per campaign | `  "a1b2c3…": "lord_1_1"` |
| `bootstrapwatch.state` | one `<logname>\|<offset>` line per BT log it has read, where the offset is how far into that log it has already scanned | `bt-sync-client.txt\|184320` |

Both are written with no escaping, so do not hand-edit a value containing a quote or a `|`. To make
the mod re-claim a campaign, delete that campaign's line from `hero-identity.json` (see the Config
notes); to make it re-warn about a `BootstrapAborted` it already handled, delete
`bootstrapwatch.state`.

In **BannerlordTogether's** folder: when it sees a `BootstrapAborted` it *renames* (never deletes)
`Modules/BannerlordTogether/RuntimeDataCache/*.rdc` to `*.rdc.stale-<timestamp>` so BT rebuilds
them. To undo, rename them back (see [Uninstall](#uninstall)). If your install is not the standard
`Modules/<mod>/bin/Win64_Shipping_Client/` layout it cannot find either folder and silently does
nothing.

You will also see files named `BLTDeploymentCrashGuard.Payload.dll.<pid>.gen1.<hex>` in
`Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/`. These are expected on player installs
too: the mod loads a throw-away copy of its payload so the original file stays unlocked, and Windows
holds the copy open until the game exits. Each launch deletes the leftovers from previous runs
before making its own. They are safe to delete while the game is closed; if antivirus flags one, it
is a byte-for-byte copy of the shipped `BLTDeploymentCrashGuard.Payload.dll`.

### What this was proven against

Every fix here was root-caused from the IL of a specific build: Bannerlord **v1.4.8** (build
1.4.8.119303, Steam), **BannerlordTogether v0.5.0.1**, BLSE 1.6.7.356, LauncherEx 1.25.6, Harmony
2.3.6.220, ButterLib 2.11.1.0. `SubModule.xml` declares DependentVersion v1.4.8 for
Native/SandBoxCore/Sandbox and an *optional* dependency on BannerlordTogether (the mod also runs in
plain single-player). On any other game or BT build, the `MOD HEALTH:` line is the first thing to
read — a fix listed as NOT resolved means the method it hooks moved.

## Config

`Modules/BLTDeploymentCrashGuard/guardconfig.json` is auto-written on first run with every key
documented inline, by exactly one writer — the harness, before any fix runs. (v1.3.2 deleted a
second, undocumented two-key writer that could leave you with a `battleMode`/`timeAlwaysFlows` stub
if the harness write had failed; there is now one template and one code path that produces it.
Every key below is read whether or not it appears in the file, so a partial file is still valid —
missing keys just take their defaults.) If the file ever looks wrong, delete it and relaunch to
regenerate the documented template.

**Edits take effect on the next game launch.** The file is read once at startup and cached for the
whole session, so changing a setting while the game runs does nothing. The one exception is
`tracing`, which the payload re-reads straight from disk on every apply — which is why, with
hot-reload on, tracers can be turned on *during* a live repro instead of restarting and losing it.

| Key | Default | Meaning |
|---|---|---|
| `safeMode` | `false` | `true` disables ALL guards/fixes/tracers, including the load-time `MovementOrder` fix (#9) — an isolation switch, not a play mode: the guarded crashes come back while it is on |
| `battleMode` | `auto` | `auto` \| `solo` \| `coop`. `solo` forces vanilla battles **even with a partner connected** (their army never enters the authoritative battle — use it only when you play alone). `coop` forces co-op battle sync **even when you are alone**, which is the state that produces empty formations and the `SetupTeams` crash. Cached for the session: change it and restart |
| `timeAlwaysFlows` | `true` | campaign time does not auto-pause when your party idles — **your party only**, and in plain single-player as well as co-op |
| `shareTimeControl` | `true` | host auto-grants the client time control (either player controls speed); granted once per launch, on the authority only |
| `noSickness` | `true` | block the local player's hero dying of old-age illness (cures it instead). Your own hero only — companions and NPC lords age and die normally. Coexists with the standalone *NoSickness* mod: this guard never increments ill days, so once it cures, that mod's prefix sees a healthy hero and passes through |
| `pregnancySync` | `true` | **co-op** — replicate host births to clients so both games share the same child |
| `stashSync` | `true` | **co-op** — settlement stashes stay identical on every machine (shared clan stash) |
| `partyTroopsOnCreate` | `true` | open the troop exchange with a new clan party the moment it is created. Only the auto-open is gated: the `[CLAN-PARTY]` diagnostics keep logging either way |
| `siegeCommandAll` | `true` | **siege defense** — you command every formation and placed formations hold (no AI hand-off after deployment, no tactic troop shuffles into or out of the formations you command; owner of the settlement = general). Applies when you are the general of the defense; sally-outs are excluded. F6 still delegates on purpose |
| `coopOwnArmyCommand` | `true` | **co-op** — each player commands their own army: host's troops in formations I–IV, client's in V–VIII, on both machines |
| `myHero` | `""` | **shared-save co-op** — this machine's hero, matched by **name**, case-insensitive, among **living** heroes, preferring one in your own clan. Applied on load as **host or solo only**, never as a joining client. Needed once per existing campaign; new campaigns record automatically. An unmatched name changes nothing (the log says so); if two living heroes share the name the clan one wins |
| `tracing` | `false` | verbose diagnostic tracers — off for play, on for troubleshooting. **Log-only since v1.3.2**: the tracer no longer decides battle mode (#15) or stamps the encounter `Finish` (#7) — both now hook their own always-on chokepoints — so turning tracing on changes *what is written*, not *what the mod does*. A bug reproduced with tracing on is the same run as with it off, and neither #7 nor #15 needs it any more. It still costs log volume, so turn it back off afterwards |
| `selfTest` | `false` | run each fix's decision-logic self-test at startup and log PASS/FAIL. Not every fix registers one (see the design principles) |
| `logStreamBin` | `""` | a filebin.net bin id; when set, the **last 2 MB** of the log auto-uploads to `https://filebin.net/<bin>` about once a minute whenever the log has grown. **Anything in the log becomes publicly fetchable by anyone with the bin id**, and the uploaded file is named `blt-<H/C/S>-<YourMachineName>.log`. Leave empty unless you are actively debugging with someone |
| `hotReload` | `false` | **dev only** — no-restart reload of the payload. The flag alone does nothing: a runtime code-load also requires an empty `.hotreload-dev` file in the module root. Both are required by design, so a copied dev config can never turn runtime code loading on for a player |
| `hotReloadRoslyn` | `false` | **dev only** — watch payload `.cs` source and recompile via Roslyn (else watch the prebuilt DLL) |
| `payloadSourceDir` | `""` | **dev only** — path to payload source for Roslyn reload. **Must be set explicitly**: the shipped empty value counts as a real setting and overrides the built-in `PayloadSource` default, so leaving it blank makes Roslyn reload log `Roslyn: source dir not found:` and fall back to the prebuilt DLL |

Notes:

- **The file is read with a simple key regex, not a JSON parser.** A key sitting inside a duplicated
  or "commented-out" block still matches. `battleMode` accepts only the exact literals `"auto"`,
  `"solo"` or `"coop"` — anything else silently falls back to `auto`, with no separate warning: the
  startup line reads `[BATTLE-MODE] config: battleMode=auto` either way, so check it against what
  you typed. (`[BATTLE-MODE] config read failed, defaulting to auto: …` is a different case, logged
  only when the file itself could not be read.) `timeAlwaysFlows`
  and `shareTimeControl` are read by their own fixes and respond only to the literal `false`; any
  other value, a missing file or an unreadable file leaves them **on**, deliberately, so a malformed
  config can never silently disable a fix.
- **Legacy key.** A `guardconfig.json` carried over from an older install may still contain
  `"soloVanillaBattles": false`. It is honoured only when `battleMode` is absent or unparseable, and
  maps to `battleMode=coop` (logged as `[BATTLE-MODE] config: battleMode=coop (legacy
  soloVanillaBattles=false)`). Delete it and set `battleMode` explicitly.
- **Most fixes have no key at all**, and `safeMode` is the only switch that turns them off. The
  table above is the complete list of keys; everything else is always on. Specifically, these have
  no config key of their own:

  | Fix | Tag |
  |---|---|
  | #1 deployment crash guards | `[DEPLOY-GUARD]` |
  | #2 dead-hero reactivation (both the roster fix and the domain invariant) | `[DEADHERO]` |
  | #3 conversation-camera guard | `[CONVO-CAM]` |
  | #4 clan-screen guard | `[CLAN-GUARD]` |
  | #5 client hero-creation guard | `[HEROCREATE-GUARD]` |
  | #6 party-AI guards | `[AI-GUARD]` |
  | #7 encounter-loop breaker (its 4/15 s/60 s/4 s constants are compile-time too) | `[ENCOUNTER-GUARD]` |
  | #8 map-incident siege crash fix | `[INCIDENT-GUARD]` |
  | #9 `MovementOrder` type-init fix | `[MO-INIT]` |
  | #10 client bootstrap priming + the `BootstrapAborted` watcher | `[CLIENT-FIX]`, `[BOOTSTRAP-WATCH]` |
  | #11 marriage fixes (atomic dowry + solo clan mode) | `[MARRIAGE-GUARD]`, `[CLANMODE-FIX]` |
  | #13 player-identity guard | `[IDENTITY]` |
  | #14 map-click fast-forward keeper and co-op speed-enforcement neutralizer (the other two time fixes *do* have keys) | `[CLICK-SPEED]`, `[TIME-GUARD]` |
  | #18 join-hold pause escape | `[JOIN-ESCAPE]` |
  | #19 background-tick freeze guard (its 100 ms budget and 10 s cap are compile-time too) | `[TICK-GUARD]` |
  | #20 both gate fixes | `[GATE]` |
  | #22 hideout sneak-in advisor | `[STEALTH]` |

  #21's shared-save handoff is the odd one out: it runs automatically with no key, and `myHero` only
  exists to claim an *existing* campaign once.
- **Log streaming has a second source, checked first:** `logstream.txt` in
  `Modules/BLTDeploymentCrashGuard/`, a plain text file holding just the bin id, which `install.cmd`
  writes automatically if the `BLTGUARD_BIN` environment variable is set when you install. The
  sidecar wins over `logStreamBin`, and the installer never removes it — to stop streaming
  completely, delete `logstream.txt` **and** clear `logStreamBin`. A
  `[STREAM] log streaming enabled -> https://filebin.net/<bin>` line at startup tells you it is on.
  A failed upload is logged as `[STREAM] upload failed: …` and otherwise ignored — there is no
  retry and no on-screen warning, so never treat a streamed log as complete evidence of a crash.
- **Turning a guard off is a healthy state, not a failure.** A disabled fix logs what vanilla will
  now do and still counts as resolved in `MOD HEALTH` — resolved components are only counted, never
  named, so nothing about the disabled guard appears on that line. Its own tag's log line is where
  you confirm it is off.
- Alongside `guardconfig.json` the mod keeps **`hero-identity.json`** — this machine's campaign→hero
  record, one `"<campaignId>": "<heroStringId>"` line per campaign inside a flat JSON object. It is
  written automatically and is **per machine**: never copy it to
  your partner's PC. To re-claim a campaign for a different hero, delete its line (or the whole
  file), set `"myHero": "YourHeroName"` and load again. It stores raw ids with no escaping, so do not
  hand-edit a value containing a quote. The third state file, **`bootstrapwatch.state`**, is one
  `<logname>|<offset>` line per BannerlordTogether log the abort watcher has scanned — see
  [Files this mod writes and renames](#files-this-mod-writes-and-renames).

## Architecture

The mod ships as two assemblies (see `HOTRELOAD.md`):

- **Harness** (`BLTDeploymentCrashGuard.dll`) — the small, stable module Bannerlord loads:
  lifecycle, logging, health/self-test, config, and the reload engine.
- **Payload** (`BLTDeploymentCrashGuard.Payload.dll`) — all guards, fixes, and tracers.

`SubModule.xml` points at the harness, which loads the payload. The payload is loaded from a
per-launch shadow copy so the shipped file stays unlocked — that is the **player** path too, not
just a dev one, and it is why a shadow DLL sits in the bin folder while the game runs. In a dev
build with hot-reload enabled, the payload can be rebuilt and reloaded with no game restart (each
payload build carries a unique assembly name so the runtime never hands back a previous generation).

### How each fix works

Every fix lives in its own `Payload/*.cs` file whose header explains the bug and the fix. A fix is
a Harmony patch (prefix/postfix/finalizer/transpiler) or a by-name reflection hook into the game or
BannerlordTogether. Game and BT members are resolved by reflection so that a game or BT update
degrades gracefully instead of crashing, and each fix logs under its own tag so you can grep the log
for exactly what happened. Every crash guard reports health (`MOD HEALTH`) and registers a
`<component>.contract` self-test that pins its members and its decision logic (run with
`selfTest`); the remaining exceptions are listed in the design principles above and, per fix, in
`docs/FIX-REFERENCE.md`. The single version number lives in `Directory.Build.props` and is stamped
into both assemblies and `SubModule.xml` at build time. `dist/SubModule.xml` — the copy `install.cmd`
actually downloads — is placed by `tools/release.sh` together with the two DLLs and
`dist/manifest.txt`, never by the build, so `dist/` only ever changes as one hash-verified set.

### For developers and AI agents

The how-it-works-so-we-don't-re-derive-it docs:

- **`CLAUDE.md`** — operating guide: architecture, build/deploy (deploy both DLLs + `SubModule.xml`
  to the game module *and* `dist/`, hash-verify; pushing == releasing), and the house rules.
- **`docs/RELEASE.md`** — the one release checklist: `tools/release.sh`, the manifest, which docs
  must ship in the same commit.
- **`docs/DIAGNOSTICS.md`** — how to investigate a crash without guessing: the IL-probe toolchain,
  runtime tracing, the session-wide first-chance exception capture, log rotation and throttling. Its
  tag table covers the diagnostics tags added in the 2026-09-04 investigation, not every tag — the
  complete tag→file index is in `docs/FIX-REFERENCE.md`.
- **`docs/ENGINE-NOTES.md`** — engine facts proven from IL (e.g. the `MovementOrder`
  `beforefieldinit` type-init crash, mission load order, siege command, time control).
- **`docs/BT-INTERNALS.md`** — BannerlordTogether internals as observed from IL: the battle command
  model, session/peer state, packet dispatch (unofficial reference).
- **`docs/MODDING-GUIDE.md`** — the public techniques guide: Harmony patterns, reflecting into a
  peer mod, hot-reload, self-tests, diagnostics.
- **`docs/MODDING-PITFALLS.md`** — the companion: what bit us, which attempts were reverted, and
  the Harmony / .NET / engine / BT gotchas behind them.
- **`docs/FIX-REFERENCE.md`** — the per-fix developer table: file, class, tag, config key, scope,
  patched members, limitations and self-test, with six indexes — co-op scope, log tag → file (the
  complete log-tag index), config key → file, patched member → fix, on-screen message → file, and
  `MOD HEALTH` / `SELFTEST` component id → file.
- **`HOTRELOAD.md`** — the payload hot-reload workflow and its dev-only caveats.
- **`tools/il-probes/README.md`** — the standalone tools that read the installed game assemblies
  (`NameSearch`, `Inspect`, `IlDump`, `Callers`, `VerCheck`).
- **`CHANGELOG.md`**, **`UPSTREAM_BUG_REPORT.md`**, **`docs/UPSTREAM_CONTRIBUTION.md`** —
  per-version history, the evidence behind the BannerlordTogether-side defects listed below, and
  what has been reported upstream.
- **`docs/SPEC-pregnancy-coop-sync.md`** — the birth-sync wire format and design.
- **`.claude/rules/`** and **`.claude/skills/investigate-crash/`** — in-repo context that Claude
  Code auto-loads when working in this repo.

## Build from source

Requires the .NET SDK, the game installed, the **Bannerlord.Harmony** module installed (the
`0Harmony` reference resolves inside that module's folder), and network access to nuget.org for
restore. Game path is set in the `.csproj` (`GameDir`); override with `-p:GameDir="..."`.

```
cd Harness && dotnet build -c Release
cd ..\Payload && dotnet build -c Release
```

Deploy **both** DLLs **and** `SubModule.xml` to
`<Bannerlord>/Modules/BLTDeploymentCrashGuard/` (DLLs in `bin/Win64_Shipping_Client/`, the XML in
the module root) **and** to the repo `dist/`, then hash-verify all three across build output, game
module and `dist/`. `install.cmd` downloads from `dist/`, so **pushing to GitHub == releasing**, and
a stale `dist/` ships nothing to players.

`tools/release.sh` does all of that in one step: it builds both assemblies, deploys the three files
to the game module and to `dist/`, writes `dist/manifest.txt` (the version plus a SHA256 per file),
re-hashes every copy across build output / `dist/` / game module, and **refuses** to print
`release-ready` if any of them disagree. `--no-build` re-deploys and re-verifies the existing build
output; `BANNERLORD_DIR` overrides the game path. That manifest is what `install.cmd` checks on the
player's machine, so a half-updated `dist/` now fails loudly at install time instead of silently
shipping a harness from one build with a payload from another.

`tools/lint-scripts.sh` guards the three player-facing scripts: it fails if `install.cmd`,
`share-log.cmd` and `collect-diagnostics.cmd` no longer carry identical Steam path lists, or if
`install.cmd` does not download **and** verify every file `dist/manifest.txt` names. Run it before
committing a script or a release. Full procedure: `docs/RELEASE.md`.

There are headless test suites for the pregnancy-sync and stash-sync
wire formats (`tests\BirthPayloadTest`, `tests\StashPayloadTest`), e.g.:

```
cd tests\BirthPayloadTest && dotnet build -c Release && bin\Release\BirthPayloadTest.exe
```

## Community-reported BannerlordTogether crashes vs. this mod (audit 2026-09-01)

All 66 open reports on BannerlordTogether's Nexus bug tracker (v0.3–v0.5.0.1) were read in full
— none contains an exception or stack trace (the author triages on Discord), so the mapping is by
scenario. BT's own changelog was checked for upstream fixes. The audit is a point-in-time snapshot
against BT v0.5.0.1; re-run it per BT release, and re-check the fix numbers below whenever the
numbered list changes.

**Covered by this mod:**

| Reported | Fix here |
|---|---|
| Crash when joining an army / when an army joins your battle / attacking with an army (5 reports) | #6 party-AI guards, #19 freeze guard, tracers on the encounter chokepoints |
| "There's another me" — a clone of my character with maxed stats receiving my income (client) | #21 shared-save identity lock (loading the save as the other hero) |
| Client can't control troops / spawns as AI / "have his character as AI" | #13 player-identity guard |
| Marriage didn't work / second player can't marry / duplicate wife+child in a shared clan | #11 marriage fixes; #16 birth sync reconstructs the same child on both machines. The 0.5.0.1 message *"Marriage could not be safely completed by host-owned sync"* is BT's host **persistence commit** failing after the marriage itself validated (decompiled: `TryCommitOwnerMarriageCompletionPersistence`); #11 keeps your dowry gold safe when it happens, and the cause is printed on the host in `bt-sync-host.txt` as `[MARRIAGE] CompletionApply … reason=` |
| Crash opening the clan screen after becoming a mercenary | #4 clan-screen crash guard |
| World freeze while the host is in a tournament and the city gets besieged; freezes during a siege | #19 background-tick freeze guard |
| Crash on winning a battle next to allies / client crash when a battle ends | #1 deployment guards + #13; BT itself fixed the spectator/duplicate-party cases in 0.4.1.4 |
| Client visits a settlement stash / crafted items lost on trade or reload | #17 shared stash; crafted items are the documented machine-local limitation |
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

See `UPSTREAM_BUG_REPORT.md` for the evidence behind most of these. Items this mod cannot fully fix
from the outside (each has a workaround or a guard here, and a BannerlordTogether-side fix on
record):

- **Player side never rostered on a solo host** — the defect behind #1. Hosting with **zero clients
  connected**, BannerlordTogether's battle pipeline never spawns the player agent or the player-side
  troops during deployment team setup, so vanilla `DeploymentMissionController.SetupTeams()`
  dereferences a null `Mission.InitialPlayerAgent` and the game CTDs. It reproduces 100% on sieges
  and on defended village raids: the order-of-battle screen shows every player formation
  **0/0 "Formation is currently empty"** with a full, unwounded party. #1 removes the crash and #15
  restores playable battles by lifting BT's battle patches when you are alone; BT should roster and
  spawn the player side (or skip its deployment gate) when no client is connected.
- **Client bootstrap abort** — the action-cache audit false-negative. BT's audit would fail on
  **every** client launch, and a session that aborted cannot be repaired because BT never
  re-verifies; fix #10 makes the audit pass each time, before it can abort, so with this mod
  nothing recurs. What remains upstream is cosmetic: BT never persists the cache it validated, so
  #10 does its work on every launch instead of once.
- **Co-op spawn identity swap is repaired, not prevented** — BT's spawn sync can still build the
  *other* player's hero as the local player agent while your own hero spawns AI-controlled. #13
  detects this within a second and hands control back, but it is a safety net: expect a brief moment
  as the wrong character, and at most five corrections per battle. Since this release #13 also
  catches the swap **at the source**: vanilla only ever makes your own hero the player, so the
  moment anything assigns player control to another hero the log records who did it, with the live
  stack (`[IDENTITY] SWAP AT SOURCE …`). Send that line — it names the code that needs the
  BT-side fix.
- **Background campaign tick has no time budget** — throttled by #19; BT should bound the
  per-frame cost. The throttle is a burst pause, not a smooth slowdown: after one over-budget tick
  the background world stops for as long as that tick took (up to 10 s), so the co-op map can
  visibly lag during a heavy battle.
- **Army-siege attach gap** — a peer's party rides a besieging army without joining the
  besieger camp, so every `PlayerSiege`-derived path reads null on that peer (#8 repairs the
  incident case; `[INCIDENT-GUARD] REPAIRED` lines are the field evidence). BannerlordTogether
  manages camp membership deliberately — it has its own patch that blocks camp joins on the host
  for settlements it holds frozen for sync — so this mod does not force the join from the peer's
  side; that would fight BT's siege sync rather than fix it.
- **Map incidents are not synced** — an incident's world effects apply only on the peer that
  confirmed it.
- **Siege command on a co-op client** — BannerlordTogether's host decides which formations a
  client may command (`BattleCommandAssignmentPacket`, pinned to BT's IL: a per-player
  `AllowedFormationMask` plus general/sergeant flags, re-sent by the host), so #24 stands down on
  a machine BT positively reports as a client and logs a
  `[SIEGE-CMD] co-op CLIENT` note. With #25 the client still commands its own block (V–VIII); to
  command every formation of your own castle's defense including the host's and the garrison's,
  host the session (#21 hands the host role back and forth on a shared save).
- **Four formations per player in co-op** — #25 folds each army into infantry / archers /
  cavalry / horse archers so the two blocks stay pure; per-troop formation preferences beyond
  those four (skirmisher, heavy infantry, light/heavy cavalry) are not honoured while a
  remote player is in the battle.
- **No settlement-stash sync in BT** — #17 provides it; player-**crafted** items cannot be
  expressed on the wire (each machine keeps its own).
- **Shared-save identity** — BT's identity registry only fixes the joining client; #21 fixes
  the loading host.
- **Both players must run the same mod version** — the birth and stash packets carry a
  format-version byte and a peer on a different version *drops* them rather than risk misparsing.
  There is no popup: the receiving log just shows `[PREG-SYNC] received a malformed birth packet —
  dropped` or `[STASH-SYNC] received a malformed stash packet — dropped`, and the two games quietly
  stop sharing children and stashes. Re-run the installer on both machines after an update.
- **Birth sync is live-only** — the host broadcasts a newborn at the moment of birth and only if a
  peer is connected. A child born while your partner is offline never reaches their game; there is
  no backfill pass. A birth is also dropped if either parent cannot be resolved on the receiving
  machine (`[PREG-SYNC] cannot reconstruct child … parent not resolved`), with no retry.
- **Stash sync overwrites on first contact; it does not merge** — a stash update is the whole
  roster, not a diff. The first time you sync a settlement whose stashes had already drifted apart,
  whichever stash screen closes first wins and the other side's contents are replaced (only
  player-crafted stacks survive that). Run the **same mod list** on both machines, too: an item the
  receiver cannot resolve is skipped, and because the next snapshot from that machine will not name
  it, the machine that *can* resolve it deletes it. A single malformed entry rejects the whole
  update rather than that one stack.
- **Battle mode lifts only what is on its targets at the moment it decides** — another mod that
  patches one of the 24 battle methods *after* a decision keeps its patch until the next decision
  point (mod startup, module screen, campaign load, mission init, `StartBattle`, `MissionState.OpenNew`).
  The v1.3.2 gap this used to sit next to is closed: #15 now registers the `battle-mode` health
  component and a `battle-mode.contract` self-test, and a target a game update renamed is logged
  once and reported as degraded instead of being skipped in silence.
- **Most time fixes are not in `MOD HEALTH:`** — only the join-hold pause escape reports health and
  runs a self-test. The idle-hold suppressor, map-click keeper, shared-time-control enabler and
  co-op enforcement neutralizer prove themselves only by their startup log line and its
  patched-method count. Related: the neutralizer re-checks peer state at most every 2 seconds, so
  for up to 2 seconds after someone joins your own speed changes can still be honoured.
- **BannerlordTogether retries a time mode our guard refuses, every tick** — while you host alone,
  BT's `EnforcePlaySpeed` re-requests its own speed on every application tick, the guard refuses the
  write, and BT retries indefinitely. It is inert for play and only visible with `tracing` on (v1.3.2
  collapsed the resulting log flood, not the retry itself). BT-side fix: stop re-requesting a mode
  that did not take.
- **Clan tab on a co-op client** — #4 turns the CTD into a safe close, but does not repair the
  half-synced clan/party graph: re-opening the tab in the same state closes it again. Root cause is
  upstream in BT's clan sync.
- **Broad suppression in two guards** — #3 and #4 are finalizers that swallow *every* exception
  escaping their targets, not only the null-reference crashes they were written for. An unrelated
  future bug in those methods would be hidden rather than crashing; the suppressed message is always
  written to the log under `[CONVO-CAM]` / `[CLAN-GUARD]`.
- **Gate tick errors are swallowed mod-wide** — re-enabling settlement gates makes `CastleGate` tick
  in civilian scenes for the first time, so #20 rides a finalizer on the gate tick that skips a
  throwing tick instead of crashing the visit. That suppressor is **not** scoped to civilian gates:
  a siege-gate tick that throws is also skipped, logged as
  `[GATE] SUPPRESSED gate tick error` at most once every 5 s. If you see that during a siege, report
  it — it is insurance firing, not a normal state.
- **Revive-mod compatibility** — #2's invariant refuses any `Hero.ChangeState(Active)` on a hero
  whose `IsDead` is still true. Vanilla's own revive path clears the dead state first, so it is never
  blocked, but a third-party resurrect mod that reactivates a hero *before* clearing `IsDead` will be
  stopped (logged under `[DEADHERO]`).
- **Troops-on-creation limits** — #23 remembers one pending party creation at a time; creating a
  second party before the first troop screen opens drops the first (click that party on the map to
  fill it). If no stable party led by the new leader appears within 15 seconds — most likely on a
  co-op client waiting for the host — it gives up with an on-screen note telling you to click the
  party on the map.
- **Character creation renders the model lying sideways** (field report 2026-09-04, new character at
  co-op setup / banner-editor preview) — **not fixed**. The failing exception is swallowed somewhere
  in the scene / agent-visuals / pose path, so nothing surfaces. Set `"tracing": true` and recreate
  it: the `[CHARGEN]` tracer logs the creation lifecycle and arms a first-chance exception observer
  that names the swallowed exception's type, message and throwing frames. One hypothesis was tried
  and reverted — current belief is a separate, likely GPU-side vanilla issue.
- **Some guards are safety nets, not root fixes.** The player-identity guard (#13) and several crash
  guards suppress a symptom rather than fixing its cause; they are carried as debt with the root
  cause named, not treated as closed. #21 supersedes #13 for the save-load identity case. The
  class-level nets in #8 are likewise logged skips: the incident option closes with **no effect
  applied** and nothing is said in game, so every fire is a root-fix candidate rather than a
  finished fix.
- **Dedicated server** (BannerlordTogether's dedicated-authority mode, launched with
  `--coop-authority`) — an experimental BT mode this mod does not add but does instrument. Known
  problems: the authority role drops back to player-host when a save is loaded through the in-game
  menu; two clients form separate per-client-ghost battles instead of one shared battle; siege
  roster truncation (observed in the field, no upstream evidence recorded yet); and the flow
  contends with itself for hardcoded port **47770** — the owner window binds it and the authority
  instance it spawns then fails `Host network FAILED to bind port=47770` and self-destructs. With
  `tracing: true` the `[ROLE]` tag captures the session role before and after every save load and
  `[COOP-BATTLE]` captures which shape the battle took. All of these need a live multi-player
  session to reproduce further.
- **Dev only (hot-reload)** — reloading leaks ~1–3 MB per generation (an old assembly cannot unload
  on .NET Framework), and harness changes and load-time fixes such as #9 need a fresh launch. Since
  this release a reload while `battleMode=solo` keeps BannerlordTogether's lifted battle patches
  restorable, and a mid-campaign reload keeps birth sync (#16) working. Detail: `HOTRELOAD.md`.

## License

MIT — see [`LICENSE`](LICENSE).
