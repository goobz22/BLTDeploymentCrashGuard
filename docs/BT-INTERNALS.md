# BannerlordTogether internals as observed from IL — an unofficial reference

## What this document is, and is not

This is a reference for **BannerlordTogether (BT)** internals as this mod observes them at runtime.
It is not written by, endorsed by, or derived from BT's source. Everything here was obtained by
**reading the installed assembly's IL** (`tools/il-probes/`, pointed at
`<Game>/Modules/BannerlordTogether/bin/Win64_Shipping_Client/BannerlordTogether.dll`,
`tools/il-probes/README.md:32`), by **runtime stack traces** captured in `CrashGuard.log`, and by
**BT's own log lines** in its sync log. Where a name came from a stack trace rather than IL, that is
said at the item.

**Version pinned.** The findings below were proven against **BannerlordTogether v0.5.0.1**
(commit `035beead876d66fb1e91d7282cd98bc4f624430b`, installed via Vortex/Nexus) on game
**1.4.8.119303** (Steam) — `UPSTREAM_BUG_REPORT.md:5-6`.

**Names change.** Every member listed here is reached **by name via reflection**, never by a compile
-time reference (`SubModule.xml:13` declares BT as `Optional="true"`). A BT release that renames a
method or moves a type does not crash this mod — it silently *unhooks* the feature that depended on
it. That failure mode has already happened once: BT moved its network classes to the
`BannerlordTogether.Network.*` namespace and both sync features went dead with no crash and no
in-game signal, visible only as "NOT resolved" entries in the `MOD HEALTH` line
(`CHANGELOG.md:130-132`; the health text itself annotates degraded components
"likely a BannerlordTogether update renamed a method — check for a mod update",
`Harness/Diag.cs:92-96`). **Read the health line first whenever a co-op feature stops working after a
BT update.** Treat every name below as a snapshot, not a contract.

### Related documents

| Document | What it holds |
|---|---|
| `docs/ENGINE-NOTES.md` | Bannerlord **engine** facts proven from IL — the vanilla side of everything here. |
| `docs/DIAGNOSTICS.md` | How to investigate without guessing: the probes, tracing, the exception capture. |
| `docs/MODDING-GUIDE.md` | The public techniques — reflecting into a peer mod, Harmony patterns, self-tests. |
| `docs/MODDING-PITFALLS.md` | What bit us: reverted attempts and BT/Harmony/.NET gotchas. |
| `docs/FIX-REFERENCE.md` | Per-fix developer table — which fix depends on which BT member. |
| `tools/il-probes/README.md` | The IL/reflection probes that produced most of this document. |
| `UPSTREAM_BUG_REPORT.md`, `docs/UPSTREAM_CONTRIBUTION.md` | The BT-side reports these findings were written up into. |
| `README.md` | Player-facing: what each fix does and the known-issues list. |

---

## 1. Finding BT at runtime

| Fact | Detail | Evidence |
|---|---|---|
| Assembly simple name | Exactly `BannerlordTogether`. It is the only assembly scanned; its absence is how this mod decides BT is not installed. | `Payload/BattleMode.cs:461-465` |
| Type lookup | `PeerDetection.FindCoopType(simpleName)` walks `AppDomain.CurrentDomain.GetAssemblies()`, finds that assembly, and returns the first type whose **simple** `Name` matches. | `Payload/BattleMode.cs:456-491` |
| Partial-load tolerance | `assembly.GetTypes()` is wrapped: on `ReflectionTypeLoadException` the partial `loadEx.Types` array is used anyway (it contains nulls — they are skipped), so a partially-loadable BT build still resolves. | `Payload/BattleMode.cs:467-482` |
| Failure logging | Lookup failures log under `[PEER-DETECT]`. A null return means "BT absent or unreadable" and every BT-facing guard/tracer goes inert. | `Payload/BattleMode.cs:486-490` |
| Fully-qualified lookups | Some types are resolved with `AccessTools.TypeByName` on a full name instead — e.g. `BannerlordTogether.CoopSubModule` and `BannerlordTogether.SpNativeBattle.SpNativeBattleHostMissionBehavior`. Those are namespace-sensitive and therefore the first to break on a reorganisation. | `Payload/BackgroundTickBudgetGuard.cs:57`; `Payload/SiegeCommandGuard.cs:173` |
| Namespace churn (2026-09-01) | BT relocated its network types from `BannerlordTogether.CoopNetworkBase` / `.CoopServer` to `BannerlordTogether.Network.*`. Both sync features now try the new namespace **first** and keep the old names as fallbacks. Resolve by a candidate list, never one fully-qualified name. | `CHANGELOG.md:130-132`; `Payload/PregnancySync/PregnancySyncGuard.cs:228`; `Payload/StashSync/StashSyncGuard.cs:120` |
| Member shape is unstable | A `CoopSession` static may be a **property** in one build and a **field** in another. Every read resolves the property first (`Public\|NonPublic\|Static`) and falls back to the field; any failure is swallowed to `null`. | `Payload/BattleMode.cs:586-602` |
| Member visibility | `BattleSyncBehavior`'s members are enumerated with `Public\|NonPublic\|Static\|Instance\|DeclaredOnly` — several are non-public and `ApplyEncounterRequestNow` may have multiple overloads. | `Payload/EncounterLoopGuard.cs:55-76` |
| Obfuscation | Parts of BT are obfuscated. The clan-mode enum is type `af`; the pause-coordinator's `IsActive` query and the save-transfer coordinator's declaring type have machine names and must be found **by signature/fingerprint**, not by name. | `Payload/ClanModeSoloFix.cs:12-13,19-20`; `Payload/JoinSyncPauseEscape.cs:22-26,140-157` |

### Load order

BT's assembly is frequently **not loaded** when this mod's payload first applies. Every BT-dependent
`Apply()` must return silently when the type is missing, latch `_applied` only on success, and be
re-called from a later lifecycle point:

- `BackgroundTickBudgetGuard` retries at `OnBeforeInitialModuleScreen` — `Payload/BackgroundTickBudgetGuard.cs:57-61`, `Payload/PayloadEntry.cs:115-124` (the call is at `:120`).
- `EncounterLoopGuard` retries at `OnGameStart` — `Payload/EncounterLoopGuard.cs:55-59`, `Payload/PayloadEntry.cs:129`.
- `SiegeCommandGuard.RetryBt(harmony)` re-hooks BT's player-down releases once and logs
  `BT host player-down releases hooked late: <n>` — `Payload/SiegeCommandGuard.cs:142-155`.

Both modules' DLLs are loaded by Bannerlord through `Assembly.LoadFrom`, so BT lives in the LoadFrom
context and is invisible to default probing (`Harness/HotReload.cs:56-57`). The installer's final
instruction is to tick this mod **after** BannerlordTogether in the Singleplayer mods list, so BT
initializes first (`install.cmd:70-71`).

One ordering constraint is stricter than a retry can satisfy: **BT's client bootstrap verification
runs once per process**. An interop fix that must beat it has to be installed on a fresh launch; on a
mid-game payload reload it can only install its hook, because BT will never verify again
(`Payload/PayloadEntry.cs:72-74`).

---

## 2. Session, roles and state flags

### `CoopSession` — the static session facade

Resolved by simple name and cached once behind a `_searched` latch; a null type means "BT absent or
unreadable" (`Payload/BattleMode.cs:493-504`).

| Member | Shape | What it means | Evidence |
|---|---|---|---|
| `IsClient` | static bool | This machine is a client in someone else's session. Drives the immediate "co-op" decision and the `C` role tag. | `Payload/BattleMode.cs:427,506-509` |
| `IsHost` | static bool | This machine hosts. Used with `IsClient` to decide whether a null `Server` means "confidently no session" or "unknown". | `Payload/BattleMode.cs:428,526-537` |
| `IsActive` | static bool | A BT session is live. `MarriageBarterGuard` gates on `!= true`. | `Payload/BattleMode.cs:557-562`; `Payload/MarriageBarterGuard.cs:79-82` |
| `IsPaused` | static bool | The live shared-pause flag. | `Payload/JoinSyncPauseEscape.cs:247` |
| `Server` | static property → `BannerlordTogether.Network.CoopServer` | The host-side server object; null means no server is running **on this machine**. Presence alone is not proof of a session, and absence alone is not proof of no session. Read reflectively as `object` here, so the concrete type is never referenced. | `Payload/BattleMode.cs:429,528-539`; IL: `CoopSession::get_Server` |
| `Client` | static property → `BannerlordTogether.Network.CoopClient` | The client-side network object (the raw-send counterpart of `Server`). | `Payload/StashSync/StashSyncGuard.cs:440-447`; IL: `CoopSession::get_Client` |
| `AllowClientTimeControl` | static bool | Shipped shared-time-control permission. **Defaults off.** | `Payload/ShareTimeControl.cs:12-21` |
| `GhostHeroStringId` | static string | The remote player's ghost hero id. | `Payload/CoopCommandSplit.cs:351`; accessor `Payload/BattleMode.cs:564-573` |
| `SharedSaveMode` | static | A **bare flag** — verified by assembly scan; it carries no identity logic of its own. | `Payload/CoopHeroIdentityLock.cs:16-18`; `CHANGELOG.md:139-140` |
| `InSpNativeBattle`, `SpNativeBattleId` | static | Battle topology snapshot used by the co-op battle tracer. | `Payload/CoopBattleTrace.cs:151-193` |

A wider role surface is snapshotted (property-then-field, `Public|NonPublic|Static`) around save
loads and again whenever it changes on tick:
`IsHost`, `IsClient`, `IsDedicatedAuthority`, `AuthorityRole`, `HostMode`, `RequestedSessionRole`,
`State`, `LocalGameplayPlayerCount`, `SharedSaveMode`, `AuthorityAutoLoadSaveName`,
`IsOwnedAuthorityProcess` — `Payload/RoleTrace.cs:16-19,23-28`.

### Authority role and hosting topology

- **`CoopAuthorityRole.DedicatedGraphicalHost`** — the authority role set at launch from the
  command-line contract `--coop-authority` (alias `--coop-dedicated-authority`). Loading a save
  through the in-game menu appears to **re-derive** the role and drop dedicated mode, logging BT's
  own message *"switched to client mode out of dedicated server mode"*.
  `Payload/RoleTrace.cs:9-15,118-119`; `docs/ENGINE-NOTES.md` §12 ("This mod's own runtime contracts").
- **`DefaultHostingTopology: LegacyPlayerHost`** — the topology named in BT's own startup logging for
  the sessions in which the siege-deployment CTD and the empty player-side raid reproduce 100% of the
  time, with **zero** clients connected. `UPSTREAM_BUG_REPORT.md:47-51`.
- **Dedicated-server flow** — a separate mode from the player host. Its **owner window binds port
  47770** and the spawned **authority instance** then fails
  `Host network FAILED to bind port=47770 attempt=1/5..5/5` and self-destructs: two components of one
  flow contending for one hardcoded port. `UPSTREAM_BUG_REPORT.md:34-38`.

### Three session roles, three log files

BT writes its own sync log to the user's **Desktop** (not into its module folder) under three
role-named files — `%USERPROFILE%\Desktop\bt-sync-host.txt`, `bt-sync-client.txt`,
`bt-sync-solo.txt`. The three names are themselves evidence that BT has three distinct session roles;
which file exists and is fresh tells you what role a machine was actually playing.
`collect-diagnostics.cmd:36-38,66-67`; scanned by role name and last-write age at
`Payload/BootstrapWatch.cs:8-9,54-65`.

BT's own verbose switch is **`EnableVerboseLogging`**; enabling it produces the sync log for a repro
session (`UPSTREAM_BUG_REPORT.md:95-96`).

---

## 3. Peer and session detection

**BT session state is genuinely unreliable to read by reflection.** On 2026-08-19 20:27 the reads
reported "no remote player" while BT packets were arriving every ~2 seconds, which desynced the two
players' speeds (`Payload/BattleMode.cs:396-400`). Two rules follow, and both are load-bearing:

1. **Tri-state, never a confident negative.** Every flag read returns `bool?`. `null` means "BT absent
   or unreadable" — it must never be treated as "no". A null `Server` returns `false` **only** when
   both role flags read `false`; otherwise it returns `null` (unknown). Returning `false` there once
   produced a mid-session false-alone. `Payload/BattleMode.cs:511-555`.
   Write conditions so unknown passes through unchanged: `!= true` to disable a guard, `== true` to
   enable extra caution (`Payload/MarriageBarterGuard.cs:79-82`; `Payload/ClanPartyCreationAdvisor.cs:190`).
2. **BT's own packet handlers firing is the authoritative liveness signal.** Traced BT calls stamp
   `PeerDetection.NoteCoopActivity()`; `RecentCoopActivity()` is a **15-second** window, and recent
   activity short-circuits `AnyRemotePeerConnected()` to `true` regardless of what the flags say.
   `Payload/BattleMode.cs:402-416,511-516`; `Payload/TimeEnforcementGuard.cs:228-235`.

Peer counting on the host walks two candidate instance collections on `Server`, in order —
**`GameplayPeerIds`**, then **`ConnectedPeerIds`** (both are `int[]` **properties** on
`BannerlordTogether.Network.CoopServer` in the pinned build; they are read reflectively as
`IEnumerable`, so neither the type nor the member shape is compiled against). A non-empty enumeration
proves a live peer; a resolvable-but-empty collection is a confident `false`; neither resolving is
`null` — that walk-and-verdict is `AnyRemotePeerConnected` at `Payload/BattleMode.cs:541-554`, which
`continue`s past a resolving-but-empty collection to try the second and returns
`sawCollection ? false : null`. `Snapshot()` at `Payload/BattleMode.cs:433-446` uses the same two
member names in the same order, but `break`s at the first collection that resolves and produces no
verdict — it is evidence for the names, not for the semantics.

The composite verdicts used across the mod: a BT **client** is `IsClient() == true`; a BT **host** is
`!IsClient && AnyRemotePeerConnected() == true`; neither means solo — and unknown is fail-open
(`Payload/SiegeCommandGuard.cs:230-240`; `Payload/CoopCommandSplit.cs:341-346`). The computed role is
pushed into the logger as an `H`/`C`/`S` tag so every log line is attributable to the machine's BT
role (`Harness/Log.cs:8-10,31-38`). The tag **defaults to `"?"`** until the payload's tick reports a
role (`Harness/Log.cs:19`), so `?`-tagged lines at the head of a log are normal, not a failed
detection.

---

## 4. Network channel and packets

All of the following was proven by decompiling the installed build (2026-08/09).

| Member | What it does | Evidence |
|---|---|---|
| `PacketSerializer.Dispatch(byte[] data)` | Routes every inbound packet by its **first byte**: `Dispatch(data) = (PacketType)data[0]`. | `Payload/PregnancySync/BirthWireFraming.cs:9` |
| `PacketType` (byte enum, `BannerlordTogether.Network`) | Uses **every value 1..255** — there is no spare type id; the only free byte is **0**. Known member: `PlayerHeroData = 13`. A companion type `BannerlordTogether.Network.PacketTypeRanges` sits next to the enum, so BT reasons about **regions** of the id space and not only individual ids — re-check both on a BT update, since a release can reshape the ranges while leaving the 1..255 coverage this design depends on intact. | `Payload/PregnancySync/BirthWireFraming.cs:10`; `Payload/PregnancySync/PregnancySyncGuard.cs:508`; `PacketTypeRanges` resolves in the pinned assembly (IL probe) |
| `OnNetworkReceive` | BT's LiteNetLib receive entry point. It already **rejects zero-length packets**, and the dispatch switch has **no `case 0` and no `default`** — so a non-empty packet whose first byte is 0 is a guaranteed no-op inside BT even if an interception missed it. | `Payload/PregnancySync/BirthWireFraming.cs:11-15` |
| `CoopNetworkBase.ShouldAcceptIncomingPacket(int peerId, byte[] data) -> bool` | BT's per-packet accept gate, with an **override on `CoopServer`** with the same signature (both must be patched to cover either role). It runs on BT's **LiteNetLib network thread**, not the game thread. A Harmony prefix that sets `ref __result = false` and returns false consumes the packet: BT neither enqueues nor dispatches it. **Resolve it by name only** (`AccessTools.Method(type, "ShouldAcceptIncomingPacket")`) or with the full `new[]{ typeof(int), typeof(byte[]) }` signature — a typed lookup for `byte[]` alone returns `null` and the feature silently unhooks. A Harmony prefix binds the payload through a parameter literally named `data`. | `Payload/PregnancySync/PregnancySyncGuard.cs:21-24,228-236,316,326-339`; `Payload/StashSync/StashSyncGuard.cs:120-128,238,249-260` |
| `CoopSession.Server.BroadcastRawReliableOrdered(byte[])` | Host-side raw send: arbitrary bytes to every connected peer, reliable + ordered. Invoked by reflection so nothing compiles against BT. | `Payload/PregnancySync/PregnancySyncGuard.cs:444-449`; `Payload/StashSync/StashSyncGuard.cs:430-438` |
| `CoopSession.Client.SendRaw(byte[])` | Client-side raw send to the server. | `Payload/StashSync/StashSyncGuard.cs:440-447` |
| `WorkshopWarehouseRosterInventoryDonePatch` + `WorkshopWarehouseRosterPacket` | BT's workshop-warehouse roster sync — the only **item-roster** sync it has (there is no stash equivalent; §11). It patches the same `InventoryLogic` done-commit point a stash sync needs. A second warehouse patch, `BannerlordTogether.Patches.WorkshopWarehouseRosterDailyTickTownPatch`, exists alongside it. BT is **not** roster-blind in general: `BannerlordTogether.CoopClientCharacterRosterBehavior` with `BannerlordTogether.Packets.ClientCharacterRosterSummaryPacket` / `ClientCharacterRosterSlotSummaryPacket` covers the client character roster, and `BannerlordTogether.TroopRosterSnapshot` / `BannerlordTogether.ClientStartedBattleTroopRosterSnapshot` cover troops. | `UPSTREAM_BUG_REPORT.md:167-173`; `Payload/StashSync/StashSyncGuard.cs:16-24`; the other type names resolve in the pinned assembly (IL probe) |
| `SaveTransferAckPacket` | A BT packet type; used as a **fingerprint** to identify the obfuscated save-transfer coordinator (see §8). | `Payload/JoinSyncPauseEscape.cs:161-163,196-201` |

**Topology is a star.** A client's bytes reach only the host, which is why the host must **relay** an
applied client update for 3+ peers to converge (`Payload/StashSync/StashSyncGuard.cs:371-376`).

### How a companion mod piggybacks on the channel

Two of this mod's features (pregnancy/birth sync and stash sync) ride BT's LiteNetLib channel without
adding a packet type, because there is no spare type id to add. The design rests entirely on the four
transport facts above:

- **Frame** = `[0x00 marker][4-byte magic][payload]`. Leading byte 0 is the one `PacketType` value BT
  never dispatches, and it is safe twice over (empty packets rejected; no `case 0`, no `default`).
- **Two magics, never confusable.** Births use `"BTCG"`, stash uses `"BTCS"`; each feature's receive
  hook recognizes exactly its own magic and passes everything else through, so the two custom frames
  cannot misparse as each other. A real BT packet never starts with 0, so a BT packet can never be
  misread as ours. `Payload/PregnancySync/BirthWireFraming.cs:16-25`;
  `Payload/StashSync/StashWireFraming.cs:5-20`.
- **Receive** is a prefix on `ShouldAcceptIncomingPacket` that queues the payload (network thread) and
  returns false to consume it; the main-thread tick applies it.
  `Payload/PregnancySync/PregnancySyncGuard.cs:21-24`; `Payload/StashSync/StashSyncGuard.cs:27-31`.

A new custom frame must be proven non-colliding against **the other custom frame and all 255 BT
packet types** before it ships (`CHANGELOG.md:195-199`) — but note `docs/MODDING-PITFALLS.md` §S5
(`:1668-1678`): the naive 1..255 loop **cannot fail**, because `IsOurPacket` short-circuits on
`data[0] != Marker` and every iteration exits at byte 0. The case that actually has to be proven is a
real BT packet that starts `0x00` followed by one of the magics.

---

## 5. Battle command model

This is the part of BT most often mistaken for a bug in a companion mod. BT's rules are consistent;
vanilla's formation layout simply cannot satisfy them.

| Member / artifact | What it does | Evidence |
|---|---|---|
| `BannerlordTogether.SpNativeBattle.SpNativeBattleHostMissionBehavior` | The host-side SP-native-battle behaviour that owns command approval and the player-down releases. Resolved with `AccessTools.TypeByName`; absent type = BT not installed and every hook no-ops. | `Payload/SiegeCommandGuard.cs:173-177` |
| `IsClientFormationCommandApproved` | The host approves a formation for the client **only** when it holds the client's troops alone — `FormationHasClientOwnedUnit && !FormationHasHostOwnedUnit` — or when the client is that formation's `PlayerOwner`/`Captain`. | `Payload/CoopCommandSplit.cs:21-25`; `CHANGELOG.md:31-34` |
| `FormationHasClientOwnedUnit` / `FormationHasHostOwnedUnit` | The two predicates inside the approval rule that make a **mixed** formation un-approvable for the client. | `Payload/CoopCommandSplit.cs:22` |
| `AllowedFormationMask` | The client's set of approved formations. A non-empty mask makes the client a **sergeant** over exactly those inside an army, or their **general** otherwise. An empty mask means the client commands nothing. | `Payload/CoopCommandSplit.cs:23-24,30-31`; `CHANGELOG.md:34-38` |
| `[SPNATIVE ORDER-GUARD] blocked local …` | BT's own log line when the client issues an order for a formation outside its mask — the observable symptom of an empty mask. | `Payload/CoopCommandSplit.cs:31`; `CHANGELOG.md:38` |
| `SendFormationMembershipSnapshot` | Client → host report of its own troops' formations, sent **once a second**: host agent index + `FormationClass`. | `Payload/CoopCommandSplit.cs:25-27`; `CHANGELOG.md:35-36` |
| `ApplyClientFormationMembership` → `ResolveFormationByClass` | Host-side application of that snapshot when the claim is allowed; the target formation is resolved **by class**, which is why a class-based block split works identically on both machines. | `Payload/CoopCommandSplit.cs:27-28`; `CHANGELOG.md:36` |
| Order forwarding | With clean, unmixed blocks BT forwards the client's orders to the host by itself — no further intervention is needed. | `Payload/CoopCommandSplit.cs:40-42` |
| `BattleCommandAssignmentPacket` | The host's command assignment, re-applied by the client every few seconds. It is **authoritative**: a client-side command fix cannot win against it. | `README.md:419-421` |
| `ReleaseHostMainFormationsToAi` | Host-side "a player went down" release of the host's own main formations to the AI. | `Payload/SiegeCommandGuard.cs:178` |
| `ReleaseClientOwnedFormationsToAi` | Same, for the client-owned formations. | `Payload/SiegeCommandGuard.cs:178` |
| `ReleaseFieldBattleSourceFormationsToAi` | Same, for the field-battle source formations. | `Payload/SiegeCommandGuard.cs:178` |
| `CoopSession.GhostHeroStringId` | Names the **remote** player's hero, through which the remote player's party is located. It may resolve as a `Hero` **or** as a `CharacterObject` whose `HeroObject` is the hero (`MBObjectManager.Instance.GetObject<T>(id)`); the resolved party equalling `PartyBase.MainParty` means the read is degenerate and must be refused rather than resolved wrongly. | `Payload/CoopCommandSplit.cs:326-327,351-366` |

**The consequence to understand:** vanilla spawns both parties' troops into the same class formations
(Infantry, Ranged, …), so under BT every formation is mixed, nothing is ever purely the client's, the
mask is empty, and the client commands nothing (`Payload/CoopCommandSplit.cs:29-31`). Any fix has to
satisfy BT's rule rather than fight it — this mod keeps the two parties in separate formation blocks
(host I–IV, client V–VIII) so BT's own approval, snapshot and forwarding do the rest
(`Payload/CoopCommandSplit.cs:33-42`; `Harness/GuardConfig.cs:100`).

**Command authority is host-side.** On a BT client the host's assignment wins, so a siege-command fix
stands down entirely on a client and tells the player to host the session instead
(`Payload/SiegeCommandGuard.cs:52-53,399-405`; `Harness/GuardConfig.cs:102`).

**The three `Release*ToAi` methods must keep working.** A guard that refuses AI hand-offs has to stand
down while one of them runs — they are hooked with a prefix/finalizer depth pair for exactly that
(`Payload/SiegeCommandGuard.cs:178,188-189,484-496`).

**BT's battle patches make vanilla's ownership assumptions fragile.** Vanilla assumes the local player
is the team general and owns the `PlayerOrderController`; in co-op that is no longer safe to assume,
which is why both links are asserted and repaired at every stealth → battle transition rather than
trusted (`Payload/StealthHideoutAdvisor.cs:24-26`; `README.md:183-187`).

### Battle formation: encounter requests and leases

Four further members decide **whether two clients end up in one shared battle or in two independent
per-client battles**. All four are hooked log-only by `CoopBattleTrace`
(`Payload/CoopBattleTrace.cs:18-22,39-42`), so a server plus both clients can be compared line for
line.

| Member | What it does | Evidence |
|---|---|---|
| `BattleSyncBehavior.SendEncounterRequest(string attackerPartyStringId, string defenderGhostPartyStringId, EncounterKind kind, string settlementStringId)` | The **authority** sends a per-ghost encounter request. This is the decision point that determines one shared battle versus two per-client battles. The tracer logs **arg0/arg1 only** (attacker, defender ghost) — `kind` and the settlement id are not traced today. | `Payload/CoopBattleTrace.cs:19-20,39,96-101`; signature from IL |
| `BattleSyncBehavior.ApplyClientStartedBattleLeaseState(string sessionId, string missionAuthoritySourceKey, string[] leasedPartyIds, bool active, bool routeAsRemoteHeld, string source)` | Mission-authority battle **lease** grants, so lease ownership can be compared across the server and both clients. The tracer reads **args 0-3 only**, under the positional labels (sessionId, authKey, leased party ids, active); the leased ids are read as an `IEnumerable`, though the declared type is `string[]`. Args 4-5 (`routeAsRemoteHeld`, `source`) are never traced. | `Payload/CoopBattleTrace.cs:21,40,103-126`; signature from IL |
| `SpNativeBattleBehavior.StartLiveBattle` | Fires when a **shared** co-op battle actually starts — the positive signal that the two clients ended up in one battle rather than two. | `Payload/CoopBattleTrace.cs:21-22,41,128-131` |
| `SpNativeBattleBehavior.AttackLiveConsequence` | The consequence behind the map-menu option *"Attack (SP Co-op Battle)"* — the player's attempt to start a live co-op battle. | `Payload/CoopBattleTrace.cs:22,42,133-136` |
| `CoopSession.InSpNativeBattle`, `CoopSession.SpNativeBattleId` | The battle-topology flags stamped onto every traced line. | `Payload/CoopBattleTrace.cs:151-193` |

On a dedicated server with two gameplay clients, the observed outcome is **separate** battles
(per-client-ghost encounters); the shared-battle lease formation is an open, unfixed item
(`CHANGELOG.md:378-380`).

---

## 6. Time enforcement and shared pause

| Member | What it does | Evidence |
|---|---|---|
| `CoopCampaignBehavior.EnforcePlaySpeed` | Runs **every** tick. Per IL: if host and not paused, it forces `UnstoppablePlay` / `UnstoppableFastForward` by calling `Campaign.SetTimeControlModeLock(0)` then `set_TimeControlMode`. This is what stomps a solo host's chosen speed after a mid-session save load. | `Payload/TimeEnforcementGuard.cs:9-12,62-80`; `docs/ENGINE-NOTES.md` §4 ("Campaign time control") |
| The retry loop | Because BT re-requests the mode every tick and only stops when the mode changes, **blocking the write means BT retries forever** — the ~60/second source of the 2026-09-04 log flood that `TraceThrottle` exists to absorb. | `Payload/TraceThrottle.cs:9-14`; `CHANGELOG.md:5-12` |
| The unstoppable variant | BT enforces `UnstoppableFastForward`, not vanilla's `StoppableFastForward` — which is precisely why vanilla's keep-speed-on-click option (mode == 4) does not recognize a co-op session's speed and every map click downgrades it. | `Payload/MapClickSpeedKeeper.cs:11-18`; `docs/ENGINE-NOTES.md` §4 ("Time control in co-op") |
| `CoopSubModule.SetPaused(bool paused, string source, bool notify, string reason)` | BT's shared-pause entry point. **Static** (invoked with a null target); signature proven by this mod's own invocation `SetPaused(false, "Host", true, "join-escape")`. | `Payload/JoinSyncPauseEscape.cs:49,78,325`; `Payload/TimeEnforcementGuard.cs:119` |
| `CoopSubModule.ApplyTimeState` | Applies a received/derived shared time state. Fires during network time sync **and once during a solo game load** (`OnGameLoaded → SetPaused → ApplyTimeState`, log 2026-08-18 23:49) — so neither method firing is proof of a live session on its own. Names recovered from **runtime stack traces**, not a decompile. | `Payload/TimeEnforcementGuard.cs:22-25,119,228-231` |
| `CoopSession.AllowClientTimeControl` | The shipped shared-time-control flag; **defaults off**. Shared time control is a real BT feature, simply disabled by default. | `Payload/ShareTimeControl.cs:12-21` |
| `CoopSubModule.TrySendClientTimeControlCommand` | The path a client's time buttons route through. It bails at `if (!CoopSession.AllowClientTimeControl)` and prints *"[BT] Client time controls are disabled by the host."* — the observed reason a client is stuck at the authority's speed. | `Payload/ShareTimeControl.cs:13-17` |
| `CoopSubModule.ToggleClientTimeControlPermission(out bool enabled, out string reason)` | The host-side grant. Static; this overload takes no positional args and **auto-targets the single gameplay client**; it validates host-ness itself and reports back. It is a **toggle**, so calling it when already on turns it off. Benign reason strings include ones containing "no longer connected" and "No connected". | `Payload/ShareTimeControl.cs:17-20,34,94-102,109-113,121-136` |
| `CoopSubModule.IsClientTimeControlEnabledForCurrentMenu()` | Static bool "already granted?" query for the active menu. **It can lie** — the calling code carries an explicit fallback for "was already true and the menu-check lied". | `Payload/ShareTimeControl.cs:35,74-92,100-102` |

### Pause is a set of named reasons

- **`CoopSubModule._pauseCoordinator`** — a private static field holding the coordinator. Campaign
  pause is a **set of named reasons**, and the game is paused while **any** reason is active. Read the
  field live on every query so a reassigned coordinator instance is survived.
  `Payload/JoinSyncPauseEscape.cs:11-12,47,79-80,286`.
- **`IsActive(reason) -> bool`** — the coordinator's "is this reason currently active" query. Its name
  is **obfuscated**, so it is found by signature: the one declared instance method returning `bool`
  with exactly one parameter of the reason enum type. Invoking it is a pure read.
  `Payload/JoinSyncPauseEscape.cs:48,140-157,344-353`.
- **`CoopSubModule.MapPauseReason(string) -> reason enum`** — static mapper from a reason **name** to
  BT's reason enum. Proven-valid names: `"SaveSync"` and `"HeroCreation"`. Its `ReturnType` is how the
  reason enum type is discovered in the first place. `Payload/JoinSyncPauseEscape.cs:46,51-52,95-97`.
- **`CoopSubModule.ToggleHostManualPause(string, bool, bool, int, string) -> bool`** (static) — the
  host's pause **key** handler. It toggles only the **manual** reason, so it can never clear a join
  hold; its `bool` return means "the press was handled". Resolve it **by name only**
  (`Payload/JoinSyncPauseEscape.cs:128-137` enumerates declared methods and matches the name) — the
  parameter list is not stable and none of it is needed to postfix the `bool` result.
  `Payload/JoinSyncPauseEscape.cs:13-14,75,83-84,230-232`; signature from IL.
- **`CoopSubModule.ApplyHostNormalSpeed`** — BT's host normal-speed key path (optional; patched only
  if found). `Payload/JoinSyncPauseEscape.cs:76,110-113,235-238`.
- **The silent-swallow gate** — BT shows the player a message only when the paused **state** actually
  changes. A swallowed pause press leaves the state unchanged, so BT shows **nothing**: zero feedback.
  `Payload/JoinSyncPauseEscape.cs:14-16`.

---

## 7. Identity registry, ghost hero, clan mode

### Identity registry

BT's player-identity mapping (slots, Steam/password claims) is consulted **only on the client join
flow**. Nothing in BT resolves the identity of the person **loading** a shared save to host — verified
by assembly scan: `SharedSaveMode` is a bare session flag with no identity logic behind it. A
Bannerlord save stores exactly one player identity (whoever was `MainHero` when it was saved), so the
second person to host a shared save becomes the previous host's hero. That gap is what
`CoopHeroIdentityLock` fills, and it deliberately never runs as a client, whose hero BT assigns.
`Payload/CoopHeroIdentityLock.cs:11-24`; `CHANGELOG.md:139-146`.

### Ghost hero

`CoopSession.GhostHeroStringId` is BT's session-level identifier for the **remote** player's hero —
see §5 for how it resolves and how a degenerate read is detected
(`Payload/CoopCommandSplit.cs:326-327,351-366`). The same host-authority + identity-replication shape
(BT's "companion / ghost hero" pattern) is what this mod's birth sync was modelled on rather than dual
simulation (`docs/SPEC-pregnancy-coop-sync.md:18`).

### Clan mode

| Member | What it does | Evidence |
|---|---|---|
| `BannerlordTogether.ClanModeSyncBehavior` | Holds the clan-mode state machine. | `Payload/ClanModeSoloFix.cs:10-21` |
| `ClanModeSyncBehavior.Instance` | Static property returning the singleton; reflectively invoking the `CurrentMode` getter on it reads the **live, post-patch** value. | `Payload/ClanModeSoloFix.cs:90-97` |
| `ClanModeSyncBehavior.CurrentMode` | Property getter returning an internal enum describing the clan-sharing mode. It returns **Unknown whenever no *remote* identity snapshot has arrived** — and hosting with no peer connected means one never will, so it stays Unknown for the whole session and every clan-mode-gated action stays blocked. | `Payload/ClanModeSoloFix.cs:10-21,43-51,66-71` |
| Clan-mode enum (obfuscated type `af`) | `af.bI = 0` = **Unknown** (the value BT's marriage validator rejects on); `af.bi = 1` = **Separate** (the correct mode for a single player). | `Payload/ClanModeSoloFix.cs:12-13,19-20` |
| `[BT] Marriage is blocked until clan mode is synchronized` | The player-visible symptom of `CurrentMode == Unknown`. Marriage is the foremost clan-mode-gated action, but every such action is blocked with it. | `Payload/ClanModeSoloFix.cs:10-16` |

The enum values are compared as a **literal `0`** in the consuming guard
(`Payload/MarriageBarterGuard.cs:84`). A BT rebuild that renumbers the enum would silently turn that
guard into a permanent pass-through, so a BT version bump warrants re-dumping the enum.

---

## 8. Action cache, bootstrap and its audit

This is the highest-value BT-internals finding in this repo: it is why **every** client session was
permanently half-loaded.

| Member | What it does | Evidence |
|---|---|---|
| `CoopSubModule.TryVerifyNativeActionCacheWhenCampaignMapReady(string source) -> bool` | BT's bootstrap gate. **Instance** method (its caller `TryBootstrapHarmonyWhenNativeReady` pushes `this` before the one argument), so a Harmony prefix sees a `__instance`; the `source` string it is called with is worth logging. Before applying its **deferred Harmony patches** it audits the engine's `ActionIndexCache` — but it compares the engine's **static `ActionIndexCache` mirror fields** against fresh native lookups. On a client those mirrors are unprimed (index `-1`), so the audit reports a mismatch and aborts. It is a **false negative affecting every client**. | `Payload/ClientBootstrapFix.cs:11-21,74`; `docs/UPSTREAM_CONTRIBUTION.md:12-23` |
| `CoopSubModule._harmonyPatchBootstrapAttempted` | Set to `true` by the aborting audit, which **permanently blocks retry** — one false negative condemns the whole session to run with BT's deferred sync patches unapplied. It is an **instance** field (`ldfld`/`stfld` in `TryBootstrapHarmonyWhenNativeReady`), so the `SetValue(null, …)` pattern used on `_nativeActionCacheVerified` below does **not** transfer to it — a `null` target throws. | `Payload/ClientBootstrapFix.cs:16-18`; `docs/UPSTREAM_CONTRIBUTION.md:17-20`; shape from IL |
| `CoopSubModule._nativeActionCacheVerified` | Static bool recording that the audit passed; resolved with `AccessTools.Field` and settable by reflection with a `null` target (`ldsfld`/`stsfld` in IL). The neighbouring gate flag `_harmonyPatchBootstrapComplete` is static too — only `_harmonyPatchBootstrapAttempted` is per-instance. | `Payload/ClientBootstrapFix.cs:81,171-176` |
| `[HARMONY] NativeActionCatalogReady` | BT's readiness line: `source=application-tick actions=5167 animations=6170 … diskLoad=False cachedSentinel=-1 cacheMatchesNative=False cacheMismatches=214`. It **proves the native catalog is fully loaded** while only the static mirror is stale — which is what makes the subsequent abort a false negative. | `UPSTREAM_BUG_REPORT.md:11-12`; `Payload/ClientBootstrapFix.cs:19-21` |
| `[HARMONY] BootstrapAborted` | BT's abort line: `reason=action-cache-mismatch cachedSentinel=-1 nativeSentinel=4008 … deferredPatchesApplied=False earlyLifecyclePatchesRemain=True restartRequired=True`. Written **only to the sync log** — the player gets no in-game signal and plays on with broken sync. Detected here by grepping for the literal `BootstrapAborted`. | `UPSTREAM_BUG_REPORT.md:13-15,30-32`; `Payload/BootstrapWatch.cs:8-15,70` |
| `RuntimeDataCache/*.rdc` | BT's regenerable runtime data cache under `<Modules>/BannerlordTogether/RuntimeDataCache/`. The shipped cache (file dated 2026-06-30) **never loads** for game build 1.4.8.119303 — identical results with the file present (2026-08-19 20:46) and removed (21:41): `diskLoad=False` and all `-1` sentinels both ways — and **no cache write/persist ever occurs**, so `restartRequired=True` never becomes a working next launch. Because it is regenerated data, the remedy its own audit implies is to **rename** (never delete) it to `.stale-<timestamp>`. | `UPSTREAM_BUG_REPORT.md:17-22`; `Payload/BootstrapWatch.cs:97-116` |

**The audit runs on client sessions only.** Host and solo sessions never reach it and therefore work;
every co-op-as-client session ran with deferred patches unapplied. Observed downstream symptoms of the
half-load: no client hero selection, the client sees a host-style map shell, client join/encounter
requests never registered on the authority, partner armies missing from battles, speed desync between
machines. `UPSTREAM_BUG_REPORT.md:22-27`.

**BT's action-cache mirrors are also a stand-down probe.** A companion fix can check whether the
mirrors are *already* primed and stand down if so — i.e. the probe detects that BT has fixed the
bootstrap bug and the override is no longer needed (`Harness/SelfHealing.cs:18-21`).

---

## 9. Background campaign tick

| Member | What it does | Evidence |
|---|---|---|
| `BannerlordTogether.CoopSubModule` | BT's SubModule; hosts the three members below. Resolvable with `AccessTools.TypeByName("BannerlordTogether.CoopSubModule")`. | `Payload/BackgroundTickBudgetGuard.cs:57`; `UPSTREAM_BUG_REPORT.md:140-141` |
| `OnApplicationTick` | The per-frame entry point that calls `TryBackgroundCampaignTick`. Every sampled managed stack during the 10+ minute freeze was inside this chain. | `UPSTREAM_BUG_REPORT.md:139-141` |
| `ShouldBackgroundTick` | Enables background ticking whenever the active game state is **not** the map but a `MapState` is still in the state stack — i.e. whenever the host is inside a mission. | `Payload/BackgroundTickBudgetGuard.cs:12-13`; `UPSTREAM_BUG_REPORT.md:148-150` |
| `TryBackgroundCampaignTick` | Runs a full `Campaign.RealTick` + `Campaign.Tick` (reached by reflection) on **every** application tick with **no time budget**. Its body opens with many unconditional early-outs (paused / saving / not host), which is why **skipping a call is safe by construction** — its callers already tolerate no-op ticks. | `Payload/BackgroundTickBudgetGuard.cs:9-25,62`; `UPSTREAM_BUG_REPORT.md:149-151` |

**The hot chain**, from repeated managed stack samples of the main thread during the 2026-08-30 ~15:24
hang (`UPSTREAM_BUG_REPORT.md:139-152`):

```
BannerlordTogether.CoopSubModule.OnApplicationTick
  -> TryBackgroundCampaignTick -> (reflection) Campaign.RealTick / Campaign.Tick
     -> EncounterManager.HandleEncounters
        -> SuppressClientMirroredPartyHandleEncounterPatch.Prefix (String.Concat per call)
     -> BattleSyncBehavior.CanApplyEncounterHoldThirdPartyCooldownCandidate (via obfuscated wrappers)
     -> AiEngagePartyBehavior.AiHourlyTick -> FactionManager.IsAtWarAgainstFaction
```

The two dominant costs are BT's **encounter-hold re-evaluation** (reached through obfuscated wrappers,
present in every sample) and vanilla **hourly-AI catch-up**. When one campaign tick becomes
multi-second — here, a third army joining the mission's own map event — every frame drowns and the
game appears frozen with all cores pegged. **Nothing throws**, so BT's own exception/cooldown
machinery never engages: this needs a *time* guard, not an exception guard.

`SuppressClientMirroredPartyHandleEncounterPatch.Prefix` is BT's own Harmony prefix on per-party
encounter handling, and it builds a log string (`String.Concat`) on **every** invocation — under
encounter churn that alone is a significant cost (`UPSTREAM_BUG_REPORT.md:143-144,156-157`).

### The encounter-request queue

| Member | What it does | Evidence |
|---|---|---|
| `BattleSyncBehavior` | BT's campaign-side battle/encounter sync behaviour, located by simple name. | `Payload/EncounterLoopGuard.cs:55,61` |
| `ProcessPendingClientEncounterRequests` | Runs on the campaign tick and re-applies pending client encounter requests. The infinite-meeting bug lives here: **the queue entry is never consumed**, so it is re-applied every tick forever. | `Payload/EncounterLoopGuard.cs:10-14` |
| `ApplyEncounterRequestNow` | Applies one pending request, calling through `EncounterManager.StartPartyEncounter` → `PlayerEncounter.RestartPlayerEncounter`, which re-opens the same `encounter_meeting` menu. May exist as several overloads. | `Payload/EncounterLoopGuard.cs:11-13,63,69` |

Loop signature: a local `PlayerEncounter.Finish` immediately followed by re-application
(`Payload/EncounterLoopGuard.cs:8-14`). These member names were read from **runtime stack traces in
`CrashGuard.log`**, not from a decompile.

---

## 10. Save transfer and the join hold

A joining player's save sync **pauses the host** for the whole download + load + hero creation.

- **The fast-join gate** ("host keeps playing while the client loads") — obfuscated handle **`CK.A`**,
  the name to re-find it under in a later BT build — is gated on **four** conditions:
  session `Ready`, the joiner is not a spectator, the joiner already has a character, and at least one
  **other** gameplay peer exists. A spectator or a first-time joiner into a solo-hosted game fails that
  gate, so the **legacy path hard-holds the host** for the joiner's entire download + load + hero
  creation — and a joiner stuck in a retry loop holds the host frozen forever (field log 2026-08-22
  23:43-23:49). `Payload/JoinSyncPauseEscape.cs:17-21`.
- **Pause reasons `"SaveSync"` and `"HeroCreation"`** hold the pause across that window. The host's own
  unpause key toggles only the **manual** reason (§6), so it **cannot** clear them and is silently
  swallowed with no message. `Payload/JoinSyncPauseEscape.cs:11-16,95-96,280-304`.
- **The transfer-cancel router — `static void A(string reason, string message, bool notifyTarget)`** —
  is the exact method BT's own player-state timeout/watchdog calls: it resets the transfer, clears
  **both** pause reasons, tells the joiner to reconnect, and lets existing players continue. Its
  declaring type is **obfuscated**, so it is located by fingerprint: the one BT type that both handles
  `SaveTransferAckPacket` and declares that exact `static void (string,string,bool)` signature named
  `A`. `Payload/JoinSyncPauseEscape.cs:22-26,50,159-226`.
- **BT treats cancelling a stuck transfer as sanctioned recovery** — its watchdog calls that router,
  and it ships a host-facing **"Skip Resync Wait"** button for a related wait.
  `Payload/JoinSyncPauseEscape.cs:22-33`.

---

## 11. Stash, marriage and pregnancy on the BT side

### Stash — BT has none

An assembly scan (2026-08-30) found **zero** stash-named members anywhere in BT. `Settlement.Stash` is
entirely unsynced: a deposit exists only on the machine that made it, same-clan co-op players do not
actually share a stash, and a client's deposits diverge from the authoritative host state (lost on
resync/save-load). `Payload/StashSync/StashSyncGuard.cs:16-19`; `UPSTREAM_BUG_REPORT.md:165-171`.

BT *does* sync the workshop warehouse (`WorkshopWarehouseRosterInventoryDonePatch` +
`WorkshopWarehouseRosterPacket`) at the same `InventoryLogic` done-commit point a stash sync needs, so
a native BT stash implementation could reuse that packet machinery nearly verbatim
(`UPSTREAM_BUG_REPORT.md:167-173`). Either implementation hits the same wall: **player-crafted items
cannot be resolved by `StringId` on the other machine**, so replicating them requires `WeaponDesign`
serialization (`UPSTREAM_BUG_REPORT.md:173-176`).

### Marriage

| Member / behaviour | What it does | Evidence |
|---|---|---|
| `MarriageFinalBarterApplyPatch` | Suppresses the native marriage inside a barter and routes it to host validation — but does **not** suppress the sibling barterables in the same `BarterManager.ApplyAndFinalizePlayerBarter` loop. The gold dowry therefore applies **natively** even when BT's host-side gate later rejects the marriage: money gone, no marriage. | `Payload/MarriageBarterGuard.cs:11-16`; `CHANGELOG.md:296-297` |
| The clan-mode gate | BT's host-side validator rejects a routed marriage with `[BT] Marriage is blocked until clan mode is synchronized` whenever clan mode reads Unknown — and clan mode never leaves Unknown when hosting alone (§7), so marriage is blocked for a solo host. | `Payload/MarriageBarterGuard.cs:16-21,84`; `Payload/ClanModeSoloFix.cs:9-14`; `CHANGELOG.md:294-295` |
| `MarriageSyncBehavior.TryCommitOwnerMarriageCompletionPersistence` | On 0.5.0.1 the message *"Marriage could not be safely completed by host-owned sync"* is this host-side persistence commit failing **after** the marriage itself validated. The cause prints on the host in `bt-sync-host.txt` as `[MARRIAGE] CompletionApply … reason=`. | `README.md:377` |

The general shape worth naming: **one mod suppressing a single leg of a multi-leg transaction** cannot
be undone after the fact; the only remedy available to a patch is to cancel the whole transaction in a
prefix, before anything applies (`Payload/MarriageBarterGuard.cs:18-23`).

### Pregnancy

`SuppressClientPregnancyBehaviorPatch` prefixes `PregnancyCampaignBehavior.RegisterEvents` with
literally `return !CoopSession.IsClient`. Two consequences follow: the **client never simulates
pregnancy**, and the **host's vanilla rolls run completely untouched** — so what is missing is not
behaviour but **replication** of births host → client.
`docs/SPEC-pregnancy-coop-sync.md:5-8`; `CHANGELOG.md:180-181`; `Harness/GuardConfig.cs:94`.

BT's own release note is explicit: *"Pregnancy, children, succession, inheritance, and broad family
lifecycle state are blocked for this release."* BT has no family/hero replication among its hand-rolled
sync behaviours (`docs/SPEC-pregnancy-coop-sync.md:8-11`). The documented stand-down trigger for a
companion implementation: if BT ever ships real family sync — its suppression patch changes, or a
`PregnancySyncBehavior` type appears — the companion feature stands down
(`docs/SPEC-pregnancy-coop-sync.md:33-35`).

---

## 12. The battle patches BT installs

BT installs prefixes, postfixes, finalizers and transpilers — with priorities and `before`/`after`
ordering constraints — over native battle/deployment/spawn methods. They are visible through
`Harmony.GetPatchInfo(method)`, and a patch is identified as **foreign** by any owner id that does not
start with `"bltogether"` (this mod's own owner ids are `bltogether.crashguard.gen{N}`, one per
hot-reload generation). `Payload/BattleMode.cs:91-97,249-319`; `HOTRELOAD.md:11`.

**Why this mod lifts them.** With no remote peer, BT's synced-battle pipeline strips the player side
out of missions — empty formations plus a `SetupTeams` NRE, proven 2026-08-18. `battleMode=solo` means
"vanilla, BT battle patches lifted"; `battleMode=coop` means nothing is lifted; `auto` decides from
peer state at game start and at every battle chokepoint. Lifted patches are **stashed** with their
owner, kind, priority and before/after lists and restored verbatim when a peer connects.
`Payload/BattleMode.cs:10-31,249-319`; `HOTRELOAD.md:65-68`; `Harness/GuardConfig.cs:88`.
Caveat: the foreign-patch stash does **not** survive a payload hot-reload, so reloading while in
`battleMode=solo` can leave BT's battle patches lifted (`CHANGELOG.md:330-331`; `HOTRELOAD.md:65-68`).
The `ISharedState` doc comment still *names* the stash among the objects the harness keeps across a
reload (`Harness/Contracts.cs:25-30`, the stash on `:28`), but the payload never writes it there — the
only payload references to that bag are the interface handle at `Payload/PayloadEntry.cs:17,23`, and
`Stash` is a plain payload static (`Payload/BattleMode.cs:75`) that a reload resets. **Restart the game
after hot-reloading in `battleMode=solo`.**

**The native methods whose foreign patches are lifted** (battle-mission scope only; campaign/map co-op
machinery is deliberately not listed) — `Payload/BattleMode.cs:39-63`:

| Declaring type | Methods |
|---|---|
| `TaleWorlds.CampaignSystem.GameComponents.DefaultTroopSupplierProbabilityModel` | `EnqueueTroopSpawnProbabilitiesAccordingToUnitSpawnPrioritization` |
| `TaleWorlds.CampaignSystem.MapEvents.MapEventSide` | `MakeReadyForMission`, `OnTroopKilled`, `OnTroopWounded`, `OnTroopScoreHit` |
| `TaleWorlds.CampaignSystem.CampaignBehaviors.OrderOfBattleCampaignBehavior` | `GetFormationDataAtIndex`, `SetFormationInfos` |
| `TaleWorlds.MountAndBlade.DefaultBattleMissionAgentSpawnLogic` | `OnSideDeploymentOver` |
| `TaleWorlds.MountAndBlade.DeploymentMissionController` | `OnMissionTick`, `FinishDeployment`, `SetupAIOfEnemyTeam` |
| `TaleWorlds.MountAndBlade.ComponentInterfaces.BattleInitializationModel` | `CanPlayerSideDeployWithOrderOfBattle` |
| `TaleWorlds.MountAndBlade.BattleEndLogic` | `MissionEnded`, `OnAgentRemoved` |
| `TaleWorlds.MountAndBlade.BattleObserverMissionLogic` | `OnAgentRemoved` |
| `TaleWorlds.MountAndBlade.ViewModelCollection.OrderOfBattle.OrderOfBattleVM` | `Initialize`, `ExecuteBeginMission`, `OnDeploymentFinalized`, `RefreshValues` |
| `SandBox.GameComponents.SandboxBattleInitializationModel` | `GetAllAvailableTroopTypes` |
| `SandBox.Missions.MissionLogics.BattleAgentLogic` | `OnAgentBuild`, `CheckUpgrade`, `OnAgentHit`, `OnAgentRemoved` |

**When BT installs them, and why only one decision point can lift them (proven 2026-09-04).** BT
installs those 24 patches **after** `MBSubModuleBase.OnGameStart` and **before**
`PlayerEncounter.StartBattle`, and the pre-mission half of the set —
`MapEventSide.MakeReadyForMission`, `DefaultTroopSupplierProbabilityModel.Enqueue…` and
`OrderOfBattleCampaignBehavior` — runs **before** `OnMissionBehaviorInitialize`. So a lift decision
taken at game start finds nothing installed, and one taken at mission init is already too late for
the roster, the spawn probabilities and the Order-of-Battle data. Evidence: across every log segment
the only decision that ever lifted patches was the one taken at `StartBattle`
(`Payload/BattleMode.cs:24-34`). `BattleMode` therefore hooks `PlayerEncounter.StartBattle` and
`MissionState.OpenNew` itself, always-on rather than from the tracer
(`Payload/BattleMode.cs:110-150`). The engine-side half of this ordering is in
`docs/ENGINE-NOTES.md` § *When a co-op mod's battle patches are installed, relative to our lifecycle
hooks*.

### Named BT patches observed elsewhere

| BT patch | Target and note | Evidence |
|---|---|---|
| `SpNativeDeploymentReadyGateTickPatch` | On `DeploymentMissionController.OnMissionTick`. The community crash report names it as the frame under which `SetupTeams`' NRE surfaces (`OnMissionTick_Patch1`). | `UPSTREAM_BUG_REPORT.md:55-62` |
| `SuppressClientMirroredPartyHandleEncounterPatch.Prefix` | On per-party encounter handling; builds a log string on every invocation (§9). | `UPSTREAM_BUG_REPORT.md:143-144,156-157` |
| `AutoWaitMenuPatch` | **Prefixes** `DefaultEncounterGameMenuModel.GetGenericStateMenu`. Observe it with a **postfix** so you record whatever final value BT's prefix produced, either way. | `Payload/TracePatches.cs:28-30,45` |
| `KingdomArmyUiPreflightPatch` | BT's own finalizer/preflight over the kingdom screen — i.e. the accepted BT-side remedy for a half-synced UI graph. `ClanScreenCrashGuard` applies the same shape to `GauntletClanScreen`. | `Payload/ClanScreenCrashGuard.cs:11-12` |
| `ClientWarPartyCreationPatch` | On a co-op **client**, clan-party creation registers a *pending host-side* creation, so the locally created `MobileParty` is **provisional** and may be replaced by the host-authoritative instance. Anything acting on a fresh client party must re-check **identity**, not just presence (a `ReferenceEquals` mismatch means adopt the new instance and restart the settle window). | `Payload/ClanPartyCreationAdvisor.cs:34-37,190,221-233` |
| `SuppressClientPregnancyBehaviorPatch` | On `PregnancyCampaignBehavior.RegisterEvents` (§11). | `docs/SPEC-pregnancy-coop-sync.md:5-8` |
| `MarriageFinalBarterApplyPatch` | On the barter apply path (§11). | `Payload/MarriageBarterGuard.cs:11-16` |

### What BT does *not* patch

- **No Harmony patch on the vanilla gate-interaction path** — the targets `CivilianGateCloseFix` and
  `SiegeGatePromptFix` patch (`CastleGate.AfterMissionStart`, `CastleGate.ServerTick`) are unpatched by
  BT — neither name occurs anywhere in BT's metadata strings — so those *prompt / close* fixes behave
  the same in vanilla and co-op; settlement and battle missions are local on every peer.
  `Payload/CivilianGateCloseFix.cs:25-26,42,49`; `Payload/SiegeGatePromptFix.cs:27,43,50`.
  BT is **not** gate-blind, though, and those two file comments overstate it: it replicates gate state
  in SP-native battles. `BannerlordTogether.SpNativeBattle.SpNativeSiegeObjectSyncBehavior` builds a
  packet on the authority (`BuildGatePacket(CastleGate, string)` → `BuildBasePacket(MissionObject,
  byte kind, string reason)` with kind `2`, carrying `CastleGate.State`, `IsGateOpen`, `IsDestroyed`
  and the destructible's `HitPoint`) and applies it on the client (`ApplyGate(CastleGate,
  BattleSiegeObjectStatePacket)`, which calls native `CastleGate.OpenDoor` / `CloseDoor` and skips a
  destroyed gate). Log tag: `[CLIENT SPNATIVE SIEGE GATE]`. **Assume gate state IS networked inside a
  co-op battle mission.** Verified by IL dump of both methods in the pinned build.
- **`ClanPartiesVM` / `CreateNewClanParty`** — untouched. Greyed-out leader cards and leader-only
  parties are **pure vanilla** behaviour; only the *provisionality* of a client's new party is
  BT-caused. `CHANGELOG.md:93,98-100`.
- **No stash code** — see §11.

---

## 13. Known BT-side bugs, with evidence

Every row below is reproduced and evidenced locally; the full write-ups are in
`UPSTREAM_BUG_REPORT.md` and `docs/UPSTREAM_CONTRIBUTION.md`.

| # | Bug | Evidence |
|---|---|---|
| 1 | **Client sessions permanently half-loaded.** The action-cache audit false-negatives on every client, `_harmonyPatchBootstrapAttempted` blocks retry, and the whole session runs with deferred sync patches unapplied. Silent — no in-game signal. | `UPSTREAM_BUG_REPORT.md:3-32` (§8) |
| 2 | **The shipped `.rdc` never loads and is never regenerated**, so `restartRequired=True` can never become a working next launch. | `UPSTREAM_BUG_REPORT.md:17-22` |
| 3 | **Host-alone battles strip the player side.** Hosting with zero clients on `LegacyPlayerHost`: `DeploymentMissionController.SetupTeams` NREs on every siege (native `SetupTeams` dereferences `Mission.InitialPlayerAgent`, which is only assigned when the player-side spawn builds a `Controller == AgentControllerType.Player` agent); village-raid battles open with every player formation 0/0 while the map-layer raid loot ticks normally. | `UPSTREAM_BUG_REPORT.md:41-101` |
| 4 | **Background campaign tick has no time budget** → whole-game freeze (10+ minutes, all cores pegged) when a campaign tick becomes multi-second. Nothing throws. | `UPSTREAM_BUG_REPORT.md:132-161` (§9) |
| 5 | **Stuck encounter-request queue entry** re-applied every campaign tick → the `encounter_meeting` menu re-opens forever. | `Payload/EncounterLoopGuard.cs:8-14` |
| 6 | **Army-siege attach gap.** A peer's `MainParty` rides in a besieging army without being attached to the army's `BesiegerCamp`, so every `PlayerSiege`-derived path reads null on that peer while the siege is **live** — vanilla's incident consequence then NREs (CTD, crashreport1.html 2026-08-30 15:04). The `[INCIDENT-GUARD] REPAIRED … (co-op army attach gap)` log lines are the field evidence. | `UPSTREAM_BUG_REPORT.md:117-125`; `Payload/MapIncidentCrashGuard.cs:23-28` |
| 7 | **Map incidents are not synced.** They spawn and resolve locally per peer; an incident's world effects (e.g. siege progress) apply only in the confirming peer's process. Needs a sync/authority decision like other campaign actions. | `UPSTREAM_BUG_REPORT.md:126-128` |
| 8 | **Party fields sync piecemeal on a client join.** A `MobileParty` can sit for a few ticks in a state vanilla can never produce (`DefaultBehavior == DefendSettlement` with `TargetSettlement`, `TargetParty` and `ShortTermTargetParty` all null) → NRE in `MobilePartyAi.GetBehaviors` via `Campaign.PartiesThink`. It self-heals when the rest of the state arrives, so skipping a tick beats repairing the state. | `Payload/PartyAiCrashGuard.cs:10-25,86-93` |
| 9 | **Marriage dowry lost on a rejected marriage** — the suppressed-leg / native-sibling barter problem (§11). | `CHANGELOG.md:296-297` |
| 10 | **Clan mode never synchronizes when hosting alone**, blocking marriage (and every other clan-mode-gated action) for a solo host. | `CHANGELOG.md:294-295`; `Payload/ClanModeSoloFix.cs:9-15` |
| 11 | **Join hold freezes the host** and the host's unpause is silently swallowed (§10). | `CHANGELOG.md:298-302` |
| 12 | **`AllowClientTimeControl` ships off**, although many players expect either-player time control as in 2-player mode. | `docs/UPSTREAM_CONTRIBUTION.md:64-67`; `CHANGELOG.md:371-373` |
| 13 | **No settlement-stash sync** (§11). | `UPSTREAM_BUG_REPORT.md:165-176` |
| 14 | **Shared-save identity is unfixed for the loading host** (§7). | `CHANGELOG.md:137-142` |
| 15 | **Dedicated server: port 47770 self-contention** — the owner window binds it, the spawned authority instance then fails five bind attempts and self-destructs. | `UPSTREAM_BUG_REPORT.md:34-38` |
| 16 | **Dedicated server: role drops to player-host on an in-game save load**, and **two clients form separate battles** (per-client-ghost encounters) — the shared-battle lease formation is an open item. | `CHANGELOG.md:378-380`; `Payload/RoleTrace.cs:9-15` |
| 17 | **Client formation command is unusable with vanilla formations** — mixed formations empty the `AllowedFormationMask` (§5). | `CHANGELOG.md:31-38` |

### Corroboration from the public tracker (audited 2026-09-01)

The 66 open Nexus reports contain **no stack traces**; they corroborate only by scenario, so treat them
as leads, not diagnoses. Record the BT version an audit stops at and re-run it per BT release — the
useful output is the mapping, not the count. `UPSTREAM_BUG_REPORT.md:180-190`.

- **Corroborating locally-proven causes:** army-join crashes (#1060091, #1098818, #1098717);
  shared-save identity clone (#1106238, *"There's another me"*); marriage failures (#1100305, #1103471,
  #1121344, *"Marriage could not be safely completed by host-owned sync"*); siege-launch failures
  (#1089833, #1121970, #1090974, #1109683); client end-of-battle crash (#1095736, 3 confirmations on
  0.4.1.2).
- **BT-owned, not addressable from a companion mod:** Co-op Assault second-player freeze (#1100453,
  #1103508, #1106535); client renown / faction-aggro / caravan / cavalry-upgrade gaps (#1089367,
  #1089371, #1089874, #1100098); *"clan was destroyed"* on town entry (#1094061); soldier-mode
  rock-pickup ejection (#1099722).

---

## 14. BT behaviours a companion mod's fixes are built on

These are the load-bearing assumptions. If BT changes one, the named fix must change with it. Today
several of them exist only as doc strings in the generated `guardconfig.json`
(`Harness/GuardConfig.cs:86-113`).

| BT behaviour | Depended on by |
|---|---|
| BT only lets a player command a formation made **purely** of that player's troops; vanilla mixes both armies by class, which empties the mask | `coopOwnArmyCommand` — host troops to formations I–IV, client troops to V–VIII (`Harness/GuardConfig.cs:100`) |
| Siege formation assignment is **host-authoritative**; a co-op client follows the host | `siegeCommandAll` applies to solo + host only (`Harness/GuardConfig.cs:102`). The same doc string also carries the *vanilla* half of the guard's rationale — vanilla's siege default hands formations to the AI — which lives in `docs/ENGINE-NOTES.md` §3 (`:365`, "Siege defense: vanilla's default is AI control ON") |
| BT **disables pregnancy for the client**; host rolls run normally | `pregnancySync` replicates host births host → client (`Harness/GuardConfig.cs:94`) |
| BT already syncs the workshop warehouse, but not settlement stashes | `stashSync` follows the same shape (`Harness/GuardConfig.cs:96`) |
| A newly created clan party must be **confirmed by BT** before a client may touch it | `partyTroopsOnCreate` waits on a client (`Harness/GuardConfig.cs:98`) |
| Time control is a host-granted permission, shipped off | `shareTimeControl` auto-grants it (`Harness/GuardConfig.cs:90`) |
| Peer/connection state is readable at runtime | `battleMode: auto` — vanilla when hosting alone, co-op sync when a peer is connected (`Harness/GuardConfig.cs:88`) |
| A shared save can load as the *other* player's hero | `myHero` + `hero-identity.json` (`Harness/GuardConfig.cs:104`) |
| BT's battle patches are liftable and restorable at runtime | `battleMode` solo/coop (`HOTRELOAD.md:65-68`) |
| BT **method renames** are the expected breakage mode for by-name reflection | the health report annotates degraded components accordingly (`Harness/Diag.cs:92-96`) |

One row in that generated-config run is **not** a BT behaviour and so has no BT dependency:
`noSickness` blocks the vanilla die-of-illness outcome for the **local** player's hero only (each
machine protects its own player). It **coexists** with the third-party **NoSickness** mod rather than
standing down for it: this guard only ever *cures* and never increments ill days, so that mod's own
check sees a healthy hero and passes through (`Harness/GuardConfig.cs:92`;
`Payload/IllnessDeathGuard.cs:14-27`). There is no detection of the other mod and no stand-down path
in the code — the two simply do not conflict. It is listed here only because that doc string is the
one place the coexistence rule is written down.

To isolate whether a symptom comes from this mod or from BT, `safeMode=true` disables **all** guards,
fixes and tracers (`Harness/GuardConfig.cs:86`).

---

## 15. Rules of thumb when reading or extending this reference

1. **Positional arguments are unvalidated.** The battle tracer logs `SendEncounterRequest`'s arg0 as
   attacker and arg1 as defender ghost, and `ApplyClientStartedBattleLeaseState`'s args as
   (sessionId, authKey, leased party ids, active) — by position. A BT signature change silently
   **mislabels** fields rather than failing loudly. `Payload/CoopBattleTrace.cs:19-22,96-126`.
   The pinned build's real arities are **4** and **6** (§5), so the tracer already reads a prefix of
   each list and never touches `kind`/`settlementStringId` or `routeAsRemoteHeld`/`source`. Print the
   arity alongside the labels and a drift shows up as a changed count instead of a wrong label.
2. **Prefer a postfix over a prefix where BT already prefixes** — you then record the value BT's prefix
   actually produced (`Payload/TracePatches.cs:28-30`).
3. **Never force-pass a by-name lookup.** Report "not resolved" into the health line and stand down;
   the 2026-09-01 namespace move is the reason (`CHANGELOG.md:130-132`).
4. **Unknown is not "no".** Every session read is tri-state; a confident negative from an unreadable
   state is what produced the mid-session false-alone (`Payload/BattleMode.cs:529-539`).
5. **A hard-coded constant against an obfuscated BT enum needs re-dumping on every BT version bump** —
   a renumbered enum turns the guard into a silent pass-through (`Payload/MarriageBarterGuard.cs:84`).
6. **Read the sync log before theorising.** BT records things it never surfaces in game;
   `bt-sync-<role>.txt` on the Desktop is the primary source (`collect-diagnostics.cmd:36-38`).
