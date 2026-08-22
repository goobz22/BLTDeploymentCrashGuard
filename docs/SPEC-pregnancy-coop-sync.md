# SPEC — Co-op pregnancy / birth sync (host-authoritative)

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
   stillbornCount)` on the host. For each alive child serialize identity into `BirthPayloadData`
   (StringId, IsFemale, FirstName, BodyProperties xml, father/mother/clan/culture StringId,
   birthday). Prepend the transport MARKER byte and send over BT's channel
   (`CoopSession.Server.BroadcastRawReliableOrdered`).
3. **Client reconstructs.** A Harmony prefix on BT's packet-receive path peeks the leading byte;
   if it is our MARKER we parse the payload and reconstruct the child on the client with the SAME
   StringId (via `HeroCreator` + `MBObjectManager` registration), set body properties/name, and
   link mother/father/clan so all references resolve — then return false so BT never sees our
   packet (its `default:` case is not exercised by us). All other packets pass through untouched.
4. **Self-disabling.** Inert unless a BT co-op session is active AND our config `pregnancySync`
   is on. If BT ever ships real family sync (their suppression patch changes, or a
   `PregnancySyncBehavior` appears) we stand down.

## Non-goals (this release)

- Succession/inheritance edge cases beyond the child hero existing on both sides.
- Client-initiated conception timing (host authority only; the client's spouse conceives in the
  host's simulation, which is where the client's hero lives as a synced object).

## Proof strategy

- **Headless wire round-trip** (`tests/BirthPayloadTest`, TaleWorlds-free, in `bun`/dotnet CI):
  host->bytes->client is byte-identical incl. unicode, twins, garbage-rejection, determinism.
  Links the SHIPPING `BirthPayloadData.cs` so a format regression fails the test. ✅ 16/16.
- **In-game loopback self-test** (`SelfHealing.RegisterTest`): take a REAL live hero, serialize
  its identity, feed the bytes through the SAME receive+reconstruct path in loopback, assert the
  reconstructed hero matches field-for-field and links to the right parents. Proves game-object
  reconstruction with no second player. Prints PASS/FAIL in CrashGuard.log.
- **Live 2-player acceptance** (needs Noah, tracked as owed): host has a child, client sees the
  identical hero in Clan → Members; save/reload on both; no desync log.

## Files

- `Payload/PregnancySync/BirthPayloadData.cs` — pure wire model (done, tested).
- `Payload/PregnancySync/PregnancySync.cs` — game layer: OnGivenBirth hook + send + reconstruct
  + receive hook + loopback self-test.
- `tests/BirthPayloadTest/` — headless round-trip proof (done).
- Config key `pregnancySync` (default: off until live-verified with Noah).
