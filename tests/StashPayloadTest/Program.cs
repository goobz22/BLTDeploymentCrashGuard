using System;
using System.Linq;
using BLTDeploymentCrashGuard.PregnancySync;
using BLTDeploymentCrashGuard.StashSync;

// Headless proof that a settlement-stash snapshot survives sender->wire->receiver
// byte-for-byte, and that stash packets, birth packets and real BT packets can never be
// mistaken for one another. No Bannerlord assemblies needed.
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        // 1. Full round-trip of a realistic stash.
        RoundTrips("typical stash", new StashPayloadData
        {
            SettlementStringId = "town_B2",
            Entries =
            {
                new StashPayloadData.Entry { ItemStringId = "grain",              ModifierStringId = "",            Count = 44 },
                new StashPayloadData.Entry { ItemStringId = "imperial_helmet_c",  ModifierStringId = "lordly_armor", Count = 1 },
                new StashPayloadData.Entry { ItemStringId = "noble_horse",        ModifierStringId = "",            Count = 3 }
            }
        });

        // 2. Empty stash (player withdrew everything) — must round-trip, not degrade to null.
        RoundTrips("emptied stash", new StashPayloadData { SettlementStringId = "castle_c7" });

        // 3. Unicode ids and negative counts (rosters can carry oddities; wire must not care).
        RoundTrips("awkward values", new StashPayloadData
        {
            SettlementStringId = "町_têst_9",
            Entries = { new StashPayloadData.Entry { ItemStringId = "épée_d'or", ModifierStringId = "rouillé", Count = -2 } }
        });

        // 4. Large stash (hoarder save) — size sanity, still exact.
        var big = new StashPayloadData { SettlementStringId = "town_hoard" };
        for (int i = 0; i < 500; i++)
        {
            big.Entries.Add(new StashPayloadData.Entry { ItemStringId = "item_" + i, ModifierStringId = i % 3 == 0 ? "fine" : "", Count = i + 1 });
        }
        RoundTrips("500-stack stash", big);

        // 5. Discrimination: stash vs birth vs real-BT first bytes.
        byte[] stashFramed = StashWireFraming.Frame(new StashPayloadData { SettlementStringId = "town_x" });
        byte[] birthFramed = BirthWireFraming.Frame(new BirthPayloadData { MotherStringId = "hero_x" });
        Check("stash frame recognized", StashWireFraming.IsOurPacket(stashFramed));
        Check("birth frame NOT read as stash", !StashWireFraming.IsOurPacket(birthFramed));
        Check("stash frame NOT read as birth", !BirthWireFraming.IsOurPacket(stashFramed));
        Check("birth frame still recognized by birth", BirthWireFraming.IsOurPacket(birthFramed));
        for (int firstByte = 1; firstByte <= 255; firstByte++)
        {
            if (StashWireFraming.IsOurPacket(new[] { (byte)firstByte, (byte)'B', (byte)'T', (byte)'C', (byte)'S' }))
            {
                Check("BT PacketType " + firstByte + " must never match", false);
            }
        }
        Check("all 255 BT packet types rejected", true);

        // 6. Malformed input never throws, always null.
        Check("null bytes -> null", StashWireFraming.TryUnframe(null) == null);
        Check("short bytes -> null", StashWireFraming.TryUnframe(new byte[] { 0, (byte)'B' }) == null);
        Check("magic only, no body -> null", StashWireFraming.TryUnframe(new byte[] { 0, (byte)'B', (byte)'T', (byte)'C', (byte)'S' }) == null);
        byte[] truncated = StashWireFraming.Frame(big).Take(40).ToArray();
        Check("truncated body -> null", StashWireFraming.TryUnframe(truncated) == null);
        byte[] futureVersion = StashWireFraming.Frame(new StashPayloadData { SettlementStringId = "v" });
        futureVersion[5] = 99; // format-version byte
        Check("unknown format version -> null", StashWireFraming.TryUnframe(futureVersion) == null);

        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    private static void RoundTrips(string name, StashPayloadData payload)
    {
        StashPayloadData parsed = StashWireFraming.TryUnframe(StashWireFraming.Frame(payload));
        bool ok = parsed != null
            && parsed.SettlementStringId == payload.SettlementStringId
            && parsed.Entries.Count == payload.Entries.Count;
        if (ok)
        {
            for (int i = 0; i < payload.Entries.Count; i++)
            {
                ok &= parsed.Entries[i].ValueEquals(payload.Entries[i]);
            }
        }
        Check("round-trip: " + name, ok);
    }

    private static void Check(string name, bool pass)
    {
        Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
        if (!pass)
        {
            _failures++;
        }
    }
}
