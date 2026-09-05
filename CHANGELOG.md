# Changelog

## v1.3.2 — solo battles fixed with tracing off; every guard reports health; verified releases (2026-09-04)

Nothing in v1.3.2 has been released yet — `origin` is still at v1.3.1 — so this single release
carries the diagnostics work, the `MovementOrder` root-cause fix and the 2026-09-04 audit fixes
below. Update from any earlier version: the first entry under *Battles* alone changes whether a solo
battle works at all with the default configuration.

### Battles

- **Auto battle mode only made a decision when tracing was on** (`[BATTLE-MODE]`). The two decision
  points that matter — `PlayerEncounter.StartBattle` and `MissionState.OpenNew` — were hooked by the
  `[TRACE]` tracer, which is off in the shipped config, so with a default `guardconfig.json` the
  first solo battle of a session ran with the player side stripped out: empty player formations and
  the `SetupTeams` crash-to-desktop this mod exists to stop. **Proven, not guessed:** across every
  segment of the field logs the only decision that ever lifted BannerlordTogether's battle patches
  carried reason `start-battle`. It is the only one that can — BT installs its 24 battle patches
  *after* our game-start decision, and the pre-mission half of them
  (`MapEventSide.MakeReadyForMission`, the troop-supplier model, Order of Battle) runs *before*
  mission init, so neither the earlier nor the later decision point reaches them. **Fix:**
  `BattleMode` hooks both chokepoints itself, always on, independent of `tracing`. The decision now
  runs at six points — payload apply, module screen, game start, mission init, `start-battle`,
  `mission-open` — and the class header lists them.
- **Battle mode is now in `MOD HEALTH:` and `[SELFTEST]`.** New health component `battle-mode`
  (critical when a chokepoint hook is missing — the mode can no longer be decided at the point that
  matters; degraded but not critical when a single lift target is unresolved, which costs one lifted
  method) and self-test `battle-mode.contract`, which re-resolves all 24 lift targets and both
  chokepoints and pins the `WantVanilla` decision table, the "is this our own Harmony owner" filter
  and the config parser (including the legacy `soloVanillaBattles: false` → `coop` mapping).
- **An unresolvable lift target is no longer silent.** It used to be skipped with a bare `continue`;
  a game rename could therefore re-expose part of the solo bug with nothing in the log. It now logs
  once per target: `[BATTLE-MODE] lift target type/method not found: … (game update?)`.
- **Battle mode records its fires.** `GUARD ACTIVITY:` now counts `battle-mode` every time patches
  are actually lifted or restored, so a session can be checked for "did the mode ever switch".
- **`BattleMode` no longer writes its own `guardconfig.json`.** It carried a two-key stub writer that
  could leave a player with an undocumented two-line config if the harness's own write had failed;
  the harness `GuardConfig` template (every key with its `_key` explanation) is now the only writer.
- **Deployment crash guards report and self-test** (`[DEPLOY-GUARD]`, README #1). The two finalizers
  are installed by `PatchAll`, which reports nothing, so a rename would have silently disarmed the
  mod's oldest fix. A new health check verifies after `PatchAll` that our finalizers really are on
  `DeploymentMissionController.SetupTeams` and `FinishDeployment` and reports `deployment-guards`
  (critical), with self-test `deployment-guards.contract`. Every line of both guards now carries the
  `[DEPLOY-GUARD]` tag (they were untagged and ungreppable), and each step of the `FinishDeployment`
  recovery tail (`AllowAiTicking`, `DisableDying`, `SetFallAvoidSystemActive`,
  `OnAfterDeploymentFinished`, `AfterDeploymentFinished`, `RemoveMissionBehavior`) runs in its own
  try/catch so one failing step cannot abort the rest. **Stated limitation:** these finalizers
  suppress the crash-to-desktop; they do **not** restore the missing player-side troops. Auto battle
  mode is what prevents the empty player side — these are the last line for when it cannot.

### Crash guards: health and self-tests

- **Encounter-loop breaker could never trip with the shipped config** (`[ENCOUNTER-GUARD]`,
  README #7). The loop signature is "a local `PlayerEncounter.Finish`, then a re-application of the
  pending encounter request within 4 s; four of those inside 15 s". The `Finish` stamp was written
  only by the tracer, so with `tracing=false` the signature never formed and the breaker was dead
  code for players. It now hooks `PlayerEncounter.Finish` itself, always on. Health component
  `encounter-loop-guard` (healthy and explicitly `inert — BannerlordTogether not loaded` when BT is
  absent; degraded when BT *is* loaded but `BattleSyncBehavior` / `ApplyEncounterRequestNow` cannot
  be found) and self-test `encounter-loop-guard.contract`, which re-resolves the targets and pins
  the follows-a-Finish and window-trip logic.
- **Party-AI crash guard** (`[AI-GUARD]`, README #6) — health `party-ai-guard` and self-test
  `party-ai-guard.contract`; the header now says plainly that layer 1 is a **prefix that changes
  behaviour** (it skips one party's tick in the proven-inconsistent state) rather than a finalizer.
- **Client hero-creation guard** (`[HEROCREATE-GUARD]`, README #5) — health `hero-creation-guard`
  and self-test `hero-creation-guard.contract`.
- **`MovementOrder` type-init guard** (`[MO-INIT]`, README #9) — health `movementorder-typeinit`
  (critical) and self-test `movementorder-typeinit.contract`, which pins the premise the whole fix
  rests on: the `beforefieldinit` struct, the `MovementOrder..ctor(MovementOrderEnum)` target,
  exactly **1** transpiled call site, and the null-safe time helper. Being a load-time fix it needs
  a **fresh game launch**; a hot-reload cannot deliver it.
- **`PlayerIdentityGuard` and `BootstrapWatch` now appear in `GUARD ACTIVITY:`** —
  `player-identity-guard` records a fire on each identity correction, `bootstrap-watch` on each
  handled BT bootstrap abort. Both were previously invisible, so there was no way to tell whether
  the upstream bug they cover is still happening (which is how each becomes retirable).

### Diagnostics

- **Log flood at co-op setup fixed.** With `tracing=true`, while you sit on the co-op setup menu
  hosting alone, BannerlordTogether's `EnforcePlaySpeed` re-requests `UnstoppablePlay` every tick,
  our `TimeEnforcementGuard` blocks that write, and the mode never actually changes — so BT retries
  forever and the `[TIME]` tracer logged the blocked attempt, with a full stack, ~60×/second. That
  filled the 8 MB log in minutes and rotated the real setup evidence off the end. The `[TIME]` tracer
  now routes through `TraceThrottle`: the first occurrence of an identical transition logs in full
  (with its stack), repeats collapse to one `[repeat] … ×N in Ys (collapsed)` line at most every 5 s.
- **Rolling log history.** The logger keeps a rolling window of segments (`CrashGuard.log.1` … `.6`,
  ~48 MB) instead of a single `.1` overwrite, so a burst can no longer discard the evidence being
  chased; the size check is amortised every 256 writes rather than done once per launch (which had
  once let the file reach 283 MB). Harness change — **takes effect on the next game launch**.
- **`[TIME]` now names which of our prefixes vetoed a time-control write.** Three of our prefixes sit
  on `Campaign.set_TimeControlMode` (`[TIME-GUARD]`, `[CLICK-SPEED]`, and the tracer itself) and
  Harmony runs every one even when another returns false, so the old line "suppressed by another
  patch" left you unable to tell which. A vetoing prefix now notes itself and the tracer prints
  `change SUPPRESSED/ALTERED by [TIME-GUARD]` / `by [CLICK-SPEED]` / `by another patch (not one of
  ours)`; the `[repeat]` de-duplication key includes the vetoer, so two different vetoes never
  collapse into one line.
- **Character-creation lifecycle tracer and session-wide first-chance capture (`[CHARGEN]`).** Added
  for the 2026-09-04 report of the banner-editor preview rendering the character lying sideways. It
  logs the creation lifecycle (initialize / activate / each stage / refresh / finalize), and —
  **session-wide, armed once at payload apply, not only during character creation** — observes every
  first-chance exception thrown in game code (SandBox / StoryMode / TaleWorlds, excluding
  `TaleWorlds.Library` churn), with the **full inner-exception chain**, an engine-state `CONTEXT`
  line, a memory line and the **live** stack of what is actually executing (which shows the trigger
  an exception's own truncated stack hides). Coalesced by exception type + throwing frame and capped
  at **400 emissions per session** — one global cap, not per activation. This is what catches an
  exception the game swallows, and it is why the `MovementOrder` failure below was diagnosable at
  all: the real cause lived in the inner exception and ButterLib wrote no report. Off unless
  `tracing=true`; changes no game behaviour.
- **Memory / engine-state heartbeat (`[DIAG]`).** Every ~15 s and at every mission/scene transition:
  working set, private bytes, managed heap, GC collection counts and the current game-state /
  mission / campaign snapshot — enough to see a leak or a balloon build up *before* a symptom, and
  to know the exact state the engine was in when an exception fired. Off unless `tracing=true`.
- **`MovementOrder` construction origin probe (`[MO-PROBE]`).** Dev diagnostic that logs the first
  constructions and any throw inside `MovementOrder..ctor`, used to locate the type-init origin.
- **Turning tracing on no longer changes behaviour.** `TracePatches` is now literally log-only: the
  `BattleMode.DecideAndApply` calls and the encounter-finish stamp were removed from it (they live
  in the guards themselves now, per the two items above). Before this, `tracing=true` changed *when*
  battle mode was decided and *whether* the encounter breaker could trip — so a troubleshooting run
  and a normal run were not the same program.
- **`MOD HEALTH:` says how to read itself.** The summary line now ends with: *read each detail: a
  BannerlordTogether OR game update may have renamed a member; a detail saying 'inert', 'not loaded'
  or 'older game build' is on purpose*. Harness change — next launch.
- **New health components reported:** `battle-mode`, `encounter-loop-guard`, `deployment-guards`,
  `party-ai-guard`, `hero-creation-guard`, `movementorder-typeinit`, each with a matching
  `<component>.contract` self-test under `selfTest=true`. The two deployment finalizers keep their
  existing fire ids `setup-teams-guard` and `finish-deployment-guard`.

### Battle-load crash (root-cause fix)

- **`MovementOrder` type-init crash (affects v1.3.0–v1.3.1).**
  `TaleWorlds.MountAndBlade.MovementOrder` is a `beforefieldinit` struct whose static constructor
  builds six default orders through an instance constructor that reads `Mission.Current.CurrentTime`.
  Because the type is `beforefieldinit` the CLR may run that static constructor at any point before
  the first static-field access — including while a mod (this one, in v1.3.0) Harmony-patches
  `Formation` / `OrderController`. With no mission alive `Mission.Current` is null, the type
  initializer throws, and .NET **caches that failure permanently**: every battle for the rest of the
  session then dies at `Formation.ResetAux`. Fix: a transpiler makes that one read null-safe (time 0
  when there is no mission — the six template orders do not use it), then the static constructor is
  forced to run immediately under the patched instance constructor, so the type is cached
  successfully initialized for the whole process. Runs **first**, before any other patch, solo and
  in co-op; logs `[MO-INIT] MovementOrder initialized safely`. Load-time fix — **fresh launch
  required**. Root cause proven from IL: `docs/ENGINE-NOTES.md`.

### Releasing and installing

- **One release script, `tools/release.sh`.** The release is three files in two places (the game
  module and `dist/`), nothing stamped `dist/SubModule.xml`, and nothing cross-checked that the
  harness and payload in `dist/` came from the same build — `install.cmd` fetches each file
  separately, so a half-updated `dist/` shipped a mismatched pair silently. The script now builds
  both assemblies, deploys all three files to both destinations, writes `dist/manifest.txt`
  (`version=` plus a SHA256 per file), verifies every copy hash-matches across build output, `dist/`
  and the game module, and refuses to print "release-ready" on any mismatch. `--no-build` re-deploys
  from existing build output; if the game is running it says which copies were left locked.
- **`install.cmd` verifies what it downloaded.** After fetching the three files it downloads
  `dist/manifest.txt` and checks each file's SHA256 with `certutil`, refusing a mismatched set with
  a plain explanation ("the release may be mid-update on GitHub — run this again in a minute"). If
  there is no manifest or no `certutil`, it says it is skipping the check rather than failing.
- **The build now stamps `dist/SubModule.xml` too.** `Directory.Build.props` copies the freshly
  stamped `SubModule.xml` into `dist/` on every harness build; nothing had ever written that file,
  which made it the easiest part of a release to ship stale.
- **`collect-diagnostics.cmd` collects the whole picture.** The bundle is now `CrashGuard.log` plus
  rotated `.1`–`.6`, `guardconfig.json`, `hero-identity.json`, `SubModule.xml`, BannerlordTogether's
  `bt-sync-*.txt`, the game's own logs from `%ProgramData%\Mount and Blade II Bannerlord\logs`
  (3 newest `rgl_log`, 3 `rgl_log_errors`, 2 `watchdog`, 1 `launcher`), the text files from the
  newest game crash folder, and the newest crash-report `.html` from Documents.
- **The three player-facing scripts no longer disagree about where Bannerlord is.**
  `collect-diagnostics.cmd` searched 6 Steam-library paths while `install.cmd` and `share-log.cmd`
  searched 11; all three now carry the same 11. New `tools/lint-scripts.sh` fails if those lists ever
  diverge again, or if `install.cmd` does not download **and** verify every file listed in
  `dist/manifest.txt`.
- **`docs/RELEASE.md`** — the one release checklist: version bump, `tools/release.sh`, the
  hand-edited version literals, changelog and doc rows, lint, the pre-release verification gate,
  commit and push (pushing is releasing).

### Documentation and configuration text

- **The `noSickness` explanation in the generated `guardconfig.json` was wrong.** It claimed the
  guard "stands down automatically" when the third-party NoSickness mod is present; it never did.
  The generated text now states what the code does: it coexists — this guard only ever cures and
  never increments ill days, so that mod's own check sees a healthy hero and passes through.
  Existing installs keep their current `guardconfig.json` (the template is only written when the
  file does not exist); delete the file to regenerate it with the corrected text.
- **Documentation set.** `docs/DIAGNOSTICS.md` (how to investigate: probes, tracing, first-chance
  capture, log tags, rotation), `docs/ENGINE-NOTES.md` (engine facts proven from IL, with evidence
  and date), `docs/BT-INTERNALS.md` (BannerlordTogether internals as observed from IL,
  version-pinned), `docs/FIX-REFERENCE.md` (per-fix entries plus the five lookup indexes),
  `docs/MODDING-GUIDE.md`, `docs/MODDING-PITFALLS.md`, `docs/SPEC-pregnancy-coop-sync.md` and
  `docs/UPSTREAM_CONTRIBUTION.md`, plus the in-repo agent context (`CLAUDE.md`, `.claude/rules/*.md`
  and `.claude/skills/investigate-crash/SKILL.md`) so the operating rules travel with the clone.
- **`tools/il-probes/`** — standalone net472 probes that read the *installed* assemblies without a
  decompiler: `NameSearch`, `Inspect`, `IlDump` (including `.cctor` / `.ctor`), `Callers`,
  `VerCheck`. Every root cause in this release was proven with them.

## v1.3.1 — co-op: each player commands their own army (2026-09-03)

- **"In co-op I should be able to command my own army while the host commands theirs"** —
  BannerlordTogether's command model, read from the installed build's IL: the host approves a
  formation for the client only when it holds the client's troops ALONE
  (`IsClientFormationCommandApproved`: client-owned units present, no host-owned units, or
  the client is its PlayerOwner/Captain); approved formations form the client's
  `AllowedFormationMask` (sergeant over them inside an army, general otherwise); the client
  reports its troops' formations to the host once a second (`SendFormationMembershipSnapshot`)
  and the host mirrors them (`ApplyClientFormationMembership` → `ResolveFormationByClass`).
  Vanilla spawns both parties' troops into the same class formations, so every formation is
  mixed, the mask is empty and the client commands nothing (`[SPNATIVE ORDER-GUARD] blocked`).
- **Fix (`coopOwnArmyCommand`, default on)** — `CoopCommandSplit`: in a live co-op battle the
  two parties fight in separate formation blocks on both machines — host party and AI parties
  in I–IV (infantry / archers / cavalry / horse archers), client party in V–VIII, same order.
  Applied in a `Mission.SpawnTroop` postfix, re-applied on `Mission.OnDeploymentFinished` and
  every half second (Order of Battle re-sorts by class; reinforcements). The remote player's
  party is found through BT's session ghost-hero id; player heroes are never moved,
  companions travel with their party. Solo play is inert (no remote peer). Self-test pins the
  members and the block mapping.
- Known trade-off: four formations per player while a remote player is in the battle
  (troop preferences beyond the basic four fold into them). README item 24 (25–27 renumbered),
  config row, log tag `[COOP-CMD]`.

## v1.3.0 — siege defense: you command every formation, placed formations hold (2026-09-03)

- **"When someone sieges my castle my party runs off to guard the castle instead of staying
  where I set them down; when the castle is compromised they leave and get killed"** —
  root-caused in the installed build's IL, never guessed. In a siege battle vanilla's default
  formation orders END with **AI control ON** (`BattleDeploymentHandler.SetDefaultFormationOrders`:
  `SetOrder(IsSiegeBattle || IsSallyOutBattle ? AIControlOn : AIControlOff)`), run by the player
  side's auto-deploy and the Auto-deploy button. An AI-controlled formation belongs to
  `TacticDefendCastle`: `FormationAI.TickOccasionally` runs behaviors only while
  `IsAIControlled`, and the tactic assigns lanes and key positions (walls, gate, keep),
  re-plans on a breach ("retreat to keep", "defend key position") and re-balances troops
  between formations through `Formation.TransferUnits` / `Formation.Split`.
  `OrderController.BeforeSetOrder` returns a formation to the player only when it is
  AI-controlled AND has a `PlayerOwner`; `Formation.RemoveUnit` hands an emptied formation back
  to the AI (a refilled one is the AI's again); `Team.SetPlayerRole` hands EVERY formation to
  the AI when the player is not the general, and `MapEvent.IsPlayerSergeant` demotes the player
  inside another lord's army — even in their own castle.
- **Fix (`siegeCommandAll`, default on)** — when the player's team defends a siege:
  `Mission.OnDeploymentFinished` hands every AI-held regular formation to the player with a
  MOVE order to where it stands; `Formation.SetControlledByAI` refuses AI hand-offs after
  deployment; `Formation.TransferUnits` (the tactic-only API) never moves troops into or out
  of a formation the player commands; `Team.SetPlayerRole` + the role controller make the
  owner of the defended settlement the general. Exceptions that keep working on purpose:
  F6 delegate command (`OrderController.SetOrder(AIControlOn)`), vanilla's death hand-off
  (`Team.DelegateCommandToAI`), BannerlordTogether's player-down releases on the host.
  Deployment is untouched (vanilla's auto-deploy still positions formations first). A BT
  client stands down — the host's command assignment is authoritative there.
- Field-proven live: the new payload hot-reloaded into the running game (gen2, MOD HEALTH 19/19).
- Control tracer (`tracing`) now records every `IsAIControlled` flip, `SetPlayerRole`,
  `DelegateCommandToAI` and tactic `TransferUnits` with caller stacks — the exact hand-off
  point of any future report is in the log.
- New log tag `[SIEGE-CMD]`; README item 23; config `siegeCommandAll`.

## v1.2.9 — troops on party creation; "Create New Party" explained (2026-09-01)

- **"I made a party and it didn't let me add anyone" / "it should happen on creation"** —
  decoded from the installed build's IL (`ClanPartiesVM`): the leader popup greys a card
  out when the hero is a prisoner/released/fugitive, a child, in someone else's party,
  already leading a party, a governor, at sea, or when the hero's gold plus yours is under
  the finance model's party threshold; the button is disabled for prisoner / no free
  war-party slot (clan tier) / no available hero / not enough gold. On confirm vanilla
  creates the party with the LEADER ONLY and expects you to find it on the map.
  BannerlordTogether does not touch this path.
- **Enhancement (`partyTroopsOnCreate`, default on)**: the moment the party is created, the
  troop exchange opens against it — vanilla's own manage-troops party screen
  (`PartyScreenHelper.OpenScreenAsManageTroops`, the call the "manage garrison" menu and
  the clan-member conversation use), deferred one tick and with the clan screen popped so
  it sits on the map like those flows. On a co-op client the party is provisional until BT's
  host confirms it, so the screen waits for the party instance to settle (3 s, 15 s timeout
  with an on-screen fallback note).
- New `[CLAN-PARTY]` log lines: the button's disabled reason and every candidate with its
  greyed-out reason whenever the popup opens (the vanilla iterator is enumerated for
  logging only — the commit review caught that replacing it would have crashed the popup).

## v1.2.8 — hideout sneak-in explained + command guarantee; co-op receive hooks re-resolved (2026-09-01)

- **"Sneak in spawned me as a soldier and I cannot command my army"** — decoded from
  the installed build: the hideout "Sneak in" is the new STEALTH ambush mission
  (`HideoutAmbushMissionController`). It spawns YOUR hero (the control trace confirms
  MainAgent is you) and re-dresses it in `Hero.StealthEquipment` with the enemy's
  clothing colors — the "soldier" is your disguise; it starts in stealth with a
  "locate the main camp" objective, troops held back and orders withheld by design;
  being spotted too long ends the mission. Orders and your squad arrive when the
  ambush is sprung (the stealth->battle transition selects the player order
  controller). Not a bug. Added: an on-screen explainer the moment a sneak-in starts,
  and a guarantee at every stealth->battle transition that the local player is the
  team general and owns the order controller (repairs otherwise) — vanilla only
  assumes it and BT's battle patches make that assumption fragile in co-op.
- **Hot-reload finally engineered right** (field-failed again 2026-09-01 17:37 with the
  v1.2.3 engine — the DIAG line said it all: "LoadFrom deduped to already-loaded
  1.2.7.42191"): the LoadFrom context dedups simple-named assemblies by NAME only, so a
  unique AssemblyVersion never mattered and the engine fell back to byte-load (the
  0Harmony identity split). Now every payload BUILD compiles under a unique assembly
  name (`BLTDeploymentCrashGuard.Payload.b<stamp>`) and is published under the fixed
  file name; LoadFrom cannot collapse two names, and LoadFrom-context binding gives the
  correct 0Harmony. The harness API the payload uses (Log/Diag/GuardConfig/SelfHealing)
  is now public — `InternalsVisibleTo` is matched by exact name and could never cover a
  stamped one. Requires one game restart (the loaded harness must be 1.2.8+); every
  reload after that is clean.
- **Co-op receive hooks re-resolved**: BannerlordTogether moved its network classes to
  `BannerlordTogether.Network.*`; pregnancy-sync and stash-sync now look there first
  (the 2026-09-01 health line showed both "not resolved").

## v1.2.7 — shared-save host handoff: you always load as YOUR hero (2026-08-30)

- **Field report: "when Noah saves as host and I load our co-op, it loads me as his hero"**
  — a save stores exactly one player identity (MainHero at save time), and BT's identity
  registry (slots/steam/password claims) is only consulted on the CLIENT join flow —
  nothing fixes the identity of the person LOADING the save (verified by assembly scan;
  SharedSaveMode is a bare flag). New `CoopHeroIdentityLock`: a per-machine
  campaign→hero map (`hero-identity.json`); on load as host/solo the player is switched
  back to this machine's hero via vanilla's `ChangePlayerCharacterAction` (the
  succession mechanism), with an on-screen note naming who the save was last played as.
  New campaigns record automatically; existing shared campaigns are claimed once with
  `"myHero": "YourHeroName"` in guardconfig.json; the record follows death-succession.
  Never runs as a BT client (BT assigns that hero). Hosting can now pass back and forth
  on one save with each player always resuming their own hero.

## v1.2.6 — gates get their F Close/Open back, in sieges and settlement visits (2026-08-30)

- **SIEGE: "defending, the gate is open, no F to close it"** (the field report) —
  root-caused in the installed build's IL: `CastleGate.ServerTick` activates the gate's
  standing points only when the door's animation parameter is EXACTLY >= 1.0; anything
  less deactivates every point. Vanilla itself parks a closed gate at a FROZEN 0.99
  (`SetInitialStateOfGate`), and an opened door can settle a float-hair under 1.0 — in
  both cases the gate is visually at rest but permanently un-interactable. Fix: when the
  parameter is in [0.98, 1.0), apply vanilla's own direction rule (open gate -> close
  points active, closed gate -> open points active). Mid-swing doors and machine-level
  deactivation stay vanilla. A ram-DESTROYED gate is untouched — broken gates cannot be
  closed by design — but with tracing on the log now says so explicitly.
- **SETTLEMENT VISITS**: civilian (walk-around) missions call
  `CastleGate.OpenDoorAndDisableGateForCivilianMission`; `SetInitialStateOfGate` then
  force-opens the door and `SetDisabled(true)`s the entire gate machine (every standing
  point with it — no prompt, and `CloseDoor()` itself early-outs on `IsDisabled`), and
  the usable team is set to `Mission.DefenderTeam`, which never equals the player's
  team in a civilian mission (`StandingPointWithTeamLimit` requires equality). Fix:
  postfix on `AfterMissionStart` for civilian gates only — re-enable the gate and its
  standing points and set the usable team to the player's team. Closing/opening runs
  vanilla's own `CloseDoor`/`OpenDoor` (animation, nav-mesh, colliders). Siege/battle
  gates untouched; a tick finalizer insures against any siege-only assumption now that
  civilian gates tick.

## v1.2.5 — pregnancy works while waiting with your spouse; sync on by default (2026-08-30)

- **Verified against the installed build's IL** (operator ask: "make sure waiting at the
  castle with my wife can get her pregnant, vanilla and co-op"): vanilla's daily roll
  (`PregnancyCampaignBehavior.RefreshSpouseVisit`) fires when `CheckAreNearby` passes —
  same settlement (waiting inside the castle counts; the party's `CurrentSettlement` is
  that castle) or same party; ages 18–45; chance falls with age and existing children.
  In co-op, BT's suppression is literally `return !IsClient` — the HOST's rolls run
  untouched. No behavior change was needed; what was missing:
- **`pregnancySync` now defaults ON** so the resulting birth reaches the other machine
  (it was off pending live validation; the wire format and loopback are proven, the
  two-machine hop gets validated the first time it fires).
- **Conception is now observable**: every conception logs `[PREG] conception: …`, the
  player clan's gets an on-screen note, and with `tracing` on, each daily
  spouse-proximity check for the player clan logs whether the couple counted as
  together and where each of them was — so "did waiting next to her count?" is
  answerable from the log instead of by vibes.

## v1.2.4 — co-op shared settlement stash (2026-08-30)

- **Settlement stashes are now shared between co-op players** (`stashSync`, default on).
  BannerlordTogether has no stash sync at all (assembly scan: zero stash-named members —
  it syncs the workshop warehouse but not the stash), so a deposit existed only on the
  machine that made it and a client's deposits silently diverged from the authoritative
  host. Now closing a stash screen broadcasts that settlement's full stash roster over
  BT's own channel (the same transport pregnancy-sync uses; new "BTCS" frame, provably
  non-colliding with births and with all 255 BT packet types); every other machine
  applies it, the host relays client updates so all peers converge, applying defers
  while that stash is open locally, and the last-closed screen wins a simultaneous
  edit. Same-clan players therefore share one stash — deposit on one machine, withdraw
  on the other. Wire format proven by a headless suite (`tests/StashPayloadTest`).
  Player-crafted items can't be expressed on the wire, so crafted stacks are
  machine-local: excluded from snapshots AND preserved through applies — never deleted
  (the commit review caught that a naive snapshot-apply would silently wipe a crafted
  item the peer's snapshot structurally couldn't mention; crafted replication is
  recorded as an upstream item). Corrupt packets (zero/negative counts, empty ids)
  are rejected on parse.

## v1.2.3 — hot-reload engine: LoadFrom every generation (2026-08-30)

- **Mid-session payload reload fixed at the root** (field-failed again 2026-08-30 16:00 —
  thanks to the new DIAG output the failure finally told the whole story): byte-loading a
  generation (`Assembly.Load(bytes)`) resolves its references via DEFAULT-context probing,
  which finds the game's own `0Harmony 2.4.2.0` in the app base and binds it silently —
  `AssemblyResolve` never fires (probing succeeds), so the 2026-08-30 resolver pin could
  never help — and the `Harmony` type identity splits across `IPayload.Apply`
  (`Method 'Apply' … does not have an implementation`). Gen1 always worked because
  `LoadFrom`-context probing sees the module-loaded `0Harmony 2.3.6.0` the harness is bound
  to. Now EVERY generation loads via `LoadFrom` on a per-process, per-generation shadow
  copy; `LoadFrom`'s identity dedup (which byte-load existed to dodge) is defeated by
  stamping each payload BUILD with a unique `AssemblyVersion` revision (verified: two
  consecutive builds stamp distinct revisions), and a dedup, should one still happen, is
  detected by location mismatch and falls back loudly instead of re-applying old code.

## v1.2.2 — background-tick freeze guard (2026-08-30)

- **Whole-game freeze during host battles fixed** (field hang 2026-08-30 15:24: a third army
  joined the player's battle as the mission started; the game froze with all 16 cores pegged
  for 10+ minutes; root-caused by live debugger attach — repeated stack samples all landed
  inside BT's `CoopSubModule.TryBackgroundCampaignTick` → `Campaign.RealTick/Tick`).
  BT ticks the campaign on EVERY application tick while the host is in a mission, with no
  time budget; a pathologically expensive campaign tick (encounter-hold churn for the
  joining army + hourly-AI catch-up) therefore starves rendering, input, and the mission
  itself, indefinitely. New `[TICK-GUARD]`: equal-time throttle — a background tick that
  blows the 100 ms budget pauses background ticking for as long as it took (capped 10 s), so
  the foreground always keeps ~half of wall time. Not a disable: the co-op background world
  keeps running, just never at the game's expense. Inert under normal load; fires are
  counted and logged for the upstream report.

## v1.2.1 — map-incident siege crash + single-source version (2026-08-30)

- **Map-incident siege-progress CTD fixed at the root** (field crash 2026-08-30 15:04,
  crashreport1.html): confirming an incident option NREs inside vanilla's
  `IncidentEffect.SiegeProgressChange` consequence lambda — it dereferences
  `PlayerSiege.PlayerSiegeEvent…SiegePreparations` with no null check. Probing the installed
  build showed `PlayerSiegeEvent` is a computed getter over `MainParty.SiegeEvent` /
  `CurrentSettlement.SiegeEvent`, so a null means the player's own party isn't attached to any
  siege. Two-branch fix, no feature loss in either mode:
  - **co-op army attach gap** (party rides a besieging army but was never attached to the
    besieger camp): the guard finds the army's live siege and applies the exact vanilla
    effect to it — same `SetProgress`, same report text — so co-op keeps the full incident;
  - **siege genuinely over** (pure-vanilla repro: popup open while the siege ends): the
    effect reports "The siege has already ended." instead of crashing.
  Patch targeting is by IL inspection (only lambdas that call `get_PlayerSiegeEvent`), so the
  harmless preview lambda stays untouched and compiler renumbering can't break it. Class
  safety nets on `IncidentEffect.Consequence` and `Incident.InvokeOption` turn any OTHER
  stale-state incident throw into a logged skip (each fire is a root-fix candidate).
- **One source of truth for the version**: `Directory.Build.props` holds the only version
  number — MSBuild stamps both assemblies from it, the log banner reads the assembly
  identity at runtime, and a build target pokes `SubModule.xml` (which had drifted to
  v1.0.0) in lockstep.

## v1.2.x — installer + hot-reload fixes (2026-08-30)

- **Installer shipped a pre-split build** — `dist/` still held the v1.1 monolithic DLL and
  `install.cmd` only downloaded the harness, so anyone installing from the README one-liner got a
  build without any v1.2.x fix and, on the harness alone, no payload at all. `dist/` now carries
  both assemblies and the installer downloads both (each moved aside as `.prev` if the game holds
  a lock).
- **Hot-reload rejected a mid-session payload** (`TypeLoadException: Method 'Apply' … does not
  have an implementation`, field-hit 2026-08-29 22:44 while trying to enable tracing on a live
  stuck-battle repro). The assembly resolver returned the *first* loaded `0Harmony` copy, and a
  process can hold two (game bin + Bannerlord.Harmony), splitting the `Harmony` type identity that
  `IPayload.Apply(Harmony …)` crosses. The resolver now pins `0Harmony` and the harness to the
  copies the harness itself is bound to and flags any other ambiguous simple name; the failure
  diagnostics print the harness-bound `0Harmony` identity.
- **`tracing` can be enabled by hot-reload** — the payload reads the flag fresh from
  `guardconfig.json` on each generation (the harness caches the file per session), so a
  live repro can be traced without restarting the game.

## v1.2.x — fixes added on top of the harness/payload split

New crash guards and root-cause fixes (all self-disabling; health-reported; self-tested):

- **Dead-hero reactivation (issue quests)** — issue "alternative solution" troops returning
  reactivated dead companions (vanilla had no IsAlive check) → NRE on the character-development
  event. Root fixes: strip dead heroes from the returning roster, and block any dead→Active hero
  transition globally.
- **Conversation-camera crash** — `MissionConversationCameraView.MakeSpeakerLookToListener` NRE
  when a conversation agent is removed mid-dialog (e.g. a marriage applying) → finalizer skips the
  camera frame.
- **Marriage — solo clan-mode block** — BT gates marriage on clan-mode sync that never completes
  when hosting alone; report the correct solo clan-mode so marriage works, inert once a peer joins.
- **Marriage — atomic dowry** — BT let the gold apply natively while routing the marriage to host
  validation; a rejected marriage still took your money. The barter now cancels before any gold moves.
- **Join-hold pause escape** — a joining player's save sync pauses the host for the whole
  download/load/hero-creation, the host's unpause is silently swallowed (it can't clear the
  SaveSync/HeroCreation pause reasons), and a joiner stuck in a retry loop froze the host forever
  (field log 2026-08-22 23:43). Now a swallowed unpause explains the hold on screen; pressing
  pause again within 6 s cancels the stuck join through BT's own transfer-cancel routine.
- **No death by sickness** (`noSickness`, default on) — block the local hero's old-age illness death
  and cure an in-progress illness; it coexists with the standalone NoSickness mod (this guard only
  ever cures and never increments ill days, so that mod's own check sees a healthy hero and passes
  through). *(corrected 2026-09-04 — this entry originally said the guard "stands down if the
  standalone NoSickness mod is present"; it never did, and the same false claim was removed from the
  generated `guardconfig.json` text in v1.3.2.)*
- **Co-op pregnancy / birth sync** (`pregnancySync`, default off) — host broadcasts births over BT's
  channel; client reconstructs the identical child. Wire format proven by a headless test suite
  (`tests/BirthPayloadTest`); off until validated live with a second player.
- **Loader hardening** — first payload generation loads via `Assembly.LoadFrom` at the canonical
  path (fixes an assembly-identity split that made the payload fail to load in-game); a loud
  on-screen "CRASH GUARD NOT ACTIVE" warning if the payload ever fails to load; log rotation now
  re-checks periodically instead of once per launch.
- **NoSickness module fix (external)** — corrected the standalone NoSickness mod's `SubModule.xml`
  version string that blocked it from loading.

## v1.2.0 — hot-reload architecture (harness/payload split)

- **No-restart iteration (dev only).** Split into a stable **harness** (`BLTDeploymentCrashGuard.dll`,
  the module Bannerlord loads — lifecycle + reload engine) and a hot-reloadable **payload**
  (`BLTDeploymentCrashGuard.Payload.dll` — all guards/fixes/tracers). The engine loads each payload
  generation via `Assembly.Load(bytes)` (fresh statics) under a per-generation Harmony owner id,
  applies the new generation, then `UnpatchAll`'s the previous one — a failed reload keeps the
  previous generation, so the game is never left unpatched.
- Two reload sources: **build-and-drop** the payload DLL (default, dependency-free) or **Roslyn
  edit-.cs** auto-recompile (opt-in `-p:Roslyn=true`, falls back to the DLL if Roslyn bind-conflicts
  on net472). See HOTRELOAD.md.
- Hard-gated dev-only: requires `hotReload: true` AND a `.hotreload-dev` marker file. Players load
  the prebuilt payload once (no watcher, no Roslyn).
- Cross-reload state (guard fire counts, launch session id) persists in a harness-owned shared
  store; per-generation health/self-test lists are reset each reload to avoid duplicates.
- Known gap (Phase B): `BattleMode`'s foreign-patch stash doesn't yet survive a reload; reloading
  in `battleMode=solo` can leave BT battle patches lifted (coop unaffected).

## v1.1.0 — robustness & troubleshooting

- **Version stamping**: the DLL now carries a real version + build timestamp, logged at
  startup (`===== BLT Deployment Crash Guard v1.1.0 (build …) session=… =====`) so any
  crash report or log identifies exactly which build is running.
- **Startup health summary**: because every hook is by-name reflection, a
  BannerlordTogether update can silently break our patches. Each load-bearing fix now
  reports resolved/missing, and startup logs `MOD HEALTH: N active, …` — with an
  on-screen warning if a core fix failed to resolve (i.e. BT changed and we need updating).
- **Tracers off by default**: the verbose diagnostic tracers (mission/menu/control/time/
  coop-battle/role) now only load with `tracing: true` in guardconfig.json. Normal play is
  lightweight; troubleshooting turns them on.
- **Safe mode**: `safeMode: true` disables every guard/fix/tracer — for isolating whether
  this mod or BT is the cause of an issue.
- **Log rotation**: CrashGuard.log rolls to `.1` past 8 MB (it hit 12 MB in a long session
  and broke streaming). Per-launch session id stamped for separating runs.
- **Fully-documented guardconfig.json**: auto-written with every key explained.
- **collect-diagnostics.cmd**: one command bundles CrashGuard.log + BT's bt-sync-*.txt +
  the newest crash report into a zip and uploads it (link to clipboard).

## v1.0.x — fixes & guards (initial)

- **Siege deployment crash guard**: suppress the `DeploymentMissionController.SetupTeams` /
  `FinishDeployment` NRE that CTD'd every co-op siege.
- **Client bootstrap fix**: BT's client action-cache audit false-negatives on a stale
  static mirror and permanently aborts, leaving the whole client session with sync patches
  unapplied (invisible armies, joins not registering, speed desync). We prime the mirror
  from the live catalog and let verification pass — the root fix for co-op-as-client.
- **BootstrapWatch**: detects BT's silent `BootstrapAborted` and auto-clears the stale
  RuntimeDataCache so the next launch loads cleanly; warns on screen to restart.
- **Client hero-creation guard**: suppress the `FindMostSuitableHomeSettlement` NRE on a
  half-synced clan/faction during culture selection.
- **Party-AI crash guards**: suppress `MobilePartyAi.GetBehaviors` /
  `EncounterManager.HandleEncounterForMobileParty` NREs on half-synced parties (join races).
- **Encounter-loop breaker**: break the infinite encounter-meeting re-open loop.
- **Player-identity guard**: correct the co-op spawn identity swap (playing the other hero).
- **Auto battle mode**: vanilla battles when hosting solo, co-op battle sync when a peer is
  connected — auto-detected, fail-safe toward co-op.
- **Time fixes**: keep fast-forward through map clicks; don't auto-pause on idle
  (`timeAlwaysFlows`); auto-grant shared client time control (`shareTimeControl`) so either
  player controls speed; neutralize stale co-op speed enforcement after an in-game load.
- **Log streaming**: optional auto-upload of the log for remote debugging.

## Known open items (see UPSTREAM_BUG_REPORT.md)

- Dedicated-server role drops to player-host on in-game save load (tracer in place).
- Two clients on a dedicated server form separate battles (per-client-ghost encounters);
  shared-battle lease formation not yet fixed.
- Reactive vs root: player-identity and some crash guards are safety nets, not root fixes.
