# SPEC — Co-op pregnancy / birth sync (host-authoritative)

> This document was written before the feature was built. Where the implementation ended up
> elsewhere, the line is corrected in place and marked **(corrected 2026-09-04)** rather than
> rewritten silently, so the difference between the plan and the shipped code stays visible. The code
> is the authority: `Payload/PregnancySync/` and `Harness/GuardConfig.cs`.

## Problem

BannerlordTogether disables the entire pregnancy system for the CLIENT player
(`SuppressClientPregnancyBehaviorPatch` prefixes `PregnancyCampaignBehavior.RegisterEvents`
with `return !CoopSession.IsClient`). Their own note: "Pregnancy, children, succession,
inheritance, and broad family lifecycle state are blocked for this release." So a client's
spouse never conceives or delivers, and even a host's births are never replicated to the
client — BT has no family/hero replication among its ~10 hand-rolled sync behaviors.

## Goal

When either player marries and has a child, both games end up with the SAME child hero
(identical StringId, parents, clan) so clan roster, encyclopedia, inheritance and succession
agree forever — with no desync.

## Design — host authority + identity replication (mirrors BT's own companion/ghost-hero pattern)

1. **Host is the only simulator.** The host runs vanilla `PregnancyCampaignBehavior` (it already
   does). The client stays suppressed for SIMULATION (BT already prevents it) so the two sides
   can never roll divergent children. We do NOT re-enable client-side pregnancy.
2. **Host broadcasts births.** Hook `CampaignEvents.OnGivenBirthEvent(mother, aliveChildren,
   stillbornCount)` on the host — subscribed at **game start**, not at payload load, because
   `CampaignEvents` resolves through `Campaign.Current` and is per-campaign
   (`PregnancySyncGuard.cs:80-96`). *(corrected 2026-09-04)* The wire carries a format-version byte,
   the mother's StringId, the stillborn count, and per child exactly **StringId, IsFemale, FirstName
   and BodyProperties xml plus the father's StringId** — not clan, culture or birthday, which are
   re-derived identically on both sides and would be redundant on the wire (`BirthPayloadData.cs:26-43`
   and the comment at `:36-38`; `PregnancySyncGuard.cs:279-313`). The frame is
   `[0x00 marker][4-byte magic "BTCG"][payload]`, not a bare marker byte — the magic is what makes a
   misread of a real BT packet impossible (`BirthWireFraming.cs:5-20`). It is sent over BT's channel
   via `CoopSession.Server.BroadcastRawReliableOrdered`, resolved by reflection so nothing compiles
   against BT (`PregnancySyncGuard.cs:430-456`).
3. **Client reconstructs, on the main thread.** *(corrected 2026-09-04)* A Harmony prefix on BT's
   `ShouldAcceptIncomingPacket` — patched on every candidate type name, base **and** the `CoopServer`
   override — checks the 5-byte header; if the packet is ours it **queues** the parsed payload and
   sets `__result = false` so BT never processes it, then returns `false`. All other packets pass
   through untouched. Reconstruction happens on the main game tick, draining that queue
   (`PregnancySyncGuard.cs:99-127,225-241,316-346`): the receive hook runs on BT's network thread and
   `HeroCreator` / `MBObjectManager` are main-thread only. Reconstruction itself calls
   `HeroCreator.DeliverOffSpring(mother, father, isFemale)` — so clan, parents and birthday follow
   deterministically from the engine on both machines — and then forces the host's identity onto the
   result: body properties, name, and the StringId re-keyed by `UnregisterObject` → set `StringId` →
   `RegisterPresumedObject` (`PregnancySyncGuard.cs:348-426`). It is idempotent: a child whose
   StringId already exists is skipped, so a re-sent packet is harmless.
4. **Self-disabling.** Inert unless our config `pregnancySync` is on; the host half additionally
   requires `IsHost == true` and a connected remote peer, so nothing is sent solo
   (`PregnancySyncGuard.cs:47,54-58,243-258`). *(corrected 2026-09-04)* There is **no** detection of a future
   BT family-sync feature in the shipped code — no check of their suppression patch and no
   `PregnancySyncBehavior` probe. If BT ever ships real family sync, this guard must be stood down by
   hand (config `pregnancySync: false`) or taught to detect it; treat that as open work, not as
   implemented behaviour.
5. **Conception visibility is not part of the sync.** *(corrected 2026-09-04, shipped beyond this
   spec)* A postfix on `MakePregnantAction.Apply` (always) and a tracing-gated postfix on
   `CheckAreNearby` log conceptions under `[PREG]`. They are installed **regardless** of the
   `pregnancySync` flag, because "did the daily roll happen?" must stay answerable from the log
   (`PregnancySyncGuard.cs:50-53,129-137`).

## Non-goals (this release)

- Succession/inheritance edge cases beyond the child hero existing on both sides.
- Client-initiated conception timing (host authority only; the client's spouse conceives in the
  host's simulation, which is where the client's hero lives as a synced object).

## Proof strategy

- **Headless wire round-trip** (`tests/BirthPayloadTest`, TaleWorlds-free, in `bun`/dotnet CI):
  host->bytes->client is byte-identical incl. unicode, twins, garbage-rejection, determinism.
  Links the SHIPPING `BirthPayloadData.cs` so a format regression fails the test. ✅ **24/24**
  *(corrected 2026-09-04 — the suite grew the framing half: our frame recognised, leading byte 0,
  no BT packet type 1–255 misread as ours, leading-0-without-magic rejected, and the null /
  too-short / corrupt-body unframe cases; `tests/BirthPayloadTest/Program.cs`).*
- **In-game loopback self-test** `pregnancy-sync.loopback` (`SelfHealing.RegisterTest`, registered
  **before** the enabled check so the wiring is proven even with the feature off): take a REAL live
  hero (`Hero.MainHero`), serialize its identity as if it were a newborn, frame it, run the exact
  receive-path unframe, and assert the parsed identity matches field-for-field — plus that a BT
  packet type is not recognised as ours. Prints PASS/FAIL in CrashGuard.log.
  *(corrected 2026-09-04: it proves serialize → frame → collision-gate → parse against real engine
  data. It does **not** create a hero, so game-object reconstruction — `DeliverOffSpring`, the
  StringId re-key, parent links — is **not** covered by any automated test; no MainHero yet returns
  a pass with "pipeline untested this tick". `PregnancySyncGuard.cs:488-523`.)*
- **Live 2-player acceptance** (needs Noah, tracked as owed): host has a child, client sees the
  identical hero in Clan → Members; save/reload on both; no desync log. Still owed — and with
  reconstruction untested above, this is the only proof of the second half of the feature.

## Files

- `Payload/PregnancySync/BirthPayloadData.cs` — pure wire model (done, tested).
- `Payload/PregnancySync/BirthWireFraming.cs` — marker + magic framing and the BT-collision gate
  *(added 2026-09-04 to this list; engine-free, linked into the test project like the model)*.
- `Payload/PregnancySync/PregnancySyncGuard.cs` — game layer: OnGivenBirth hook + send + receive
  hook + main-thread reconstruct queue + loopback self-test + conception visibility.
  *(corrected 2026-09-04 — this spec named the file `PregnancySync.cs`; the shipped file is
  `PregnancySyncGuard.cs`.)*
- `tests/BirthPayloadTest/` — headless round-trip proof (done).
- Config key `pregnancySync`, **default `true`** — on. *(corrected 2026-09-04: the spec planned "off
  until live-verified with Noah", but `Harness/GuardConfig.cs:94` ships `"pregnancySync": true` and
  `PregnancySyncGuard.cs:47` reads it with a `true` fallback. The guard's own class header still says
  "Default OFF until validated live" and is the stale copy — see `docs/MODDING-PITFALLS.md` **P5**.)*
