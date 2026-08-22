using System;
using System.Collections.Generic;
using BLTDeploymentCrashGuard.PregnancySync;

// Headless proof that a "child born" event survives host->wire->client byte-for-byte.
// This is the network-independent half of the pregnancy-sync feature's proof; the in-game
// loopback self-test proves the game-object reconstruction. No Bannerlord assemblies needed.
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        // 1. Full round-trip of a realistic single birth.
        var single = new BirthPayloadData
        {
            MotherStringId = "lord_1_2_Aserai_1",
            StillbornCount = 0,
            Children =
            {
                new BirthPayloadData.ChildIdentity
                {
                    StringId = "coopchild_00a3f1c2",
                    IsFemale = true,
                    FirstName = "Aisha",
                    BodyPropertiesXml = "<BodyProperties version=\"4\" age=\"0\" weight=\"0.1\" build=\"0.2\" key=\"00FF3A...\" />",
                    FatherStringId = "main_hero",
                    ClanStringId = "clan_player",
                    CultureStringId = "aserai",
                    BirthDayRaw = 123456789L
                }
            }
        };
        RoundTrips("single birth", single);

        // 2. Twins (the 3% path) — two children in one payload.
        var twins = new BirthPayloadData
        {
            MotherStringId = "spouse_hero_42",
            StillbornCount = 1,
            Children =
            {
                new BirthPayloadData.ChildIdentity { StringId = "coopchild_a", IsFemale = false, FirstName = "Derthert", BodyPropertiesXml = "<b/>", FatherStringId = "main_hero", ClanStringId = "clan_player", CultureStringId = "vlandia", BirthDayRaw = 1 },
                new BirthPayloadData.ChildIdentity { StringId = "coopchild_b", IsFemale = true,  FirstName = "Ira",      BodyPropertiesXml = "<b/>", FatherStringId = "main_hero", ClanStringId = "clan_player", CultureStringId = "vlandia", BirthDayRaw = 2 }
            }
        };
        RoundTrips("twins + one stillborn", twins);

        // 3. Unicode and awkward strings (Bannerlord names carry accents; body xml has quotes/brackets).
        var unicode = new BirthPayloadData
        {
            MotherStringId = "mère_héros",
            Children =
            {
                new BirthPayloadData.ChildIdentity { StringId = "coopchild_ü", IsFemale = false, FirstName = "Ölaf Ærling 我", BodyPropertiesXml = "<BodyProperties key=\"<&>\"/>", FatherStringId = "父", ClanStringId = "clan_ê", CultureStringId = "empire", BirthDayRaw = long.MaxValue }
            }
        };
        RoundTrips("unicode + special chars", unicode);

        // 4. Empty / default fields must round-trip to empty (never null).
        var empty = new BirthPayloadData { MotherStringId = "", Children = { new BirthPayloadData.ChildIdentity() } };
        RoundTrips("empty fields", empty);

        // 5. Zero children (defensive — a stillborn-only birth carries no live child).
        RoundTrips("stillborn only, no live child", new BirthPayloadData { MotherStringId = "m", StillbornCount = 2 });

        // 6. Malformed inputs must return null, never throw.
        NullOnGarbage("null bytes", null);
        NullOnGarbage("empty array", new byte[0]);
        NullOnGarbage("random noise", new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
        NullOnGarbage("truncated valid payload", Truncate(single.ToBytes()));
        NullOnGarbage("wrong format version", WrongVersion(single.ToBytes()));

        // 7. A stable byte length is deterministic (same input -> same bytes; guards accidental
        //    nondeterminism like dictionary ordering creeping in later).
        Check("deterministic serialization", AreEqual(single.ToBytes(), single.ToBytes()));

        // 8. WIRE FRAMING — the collision-proof transport layer.
        // 8a. Framed packet round-trips through unframe to an equal payload.
        byte[] framed = BirthWireFraming.Frame(single);
        BirthPayloadData unframed = BirthWireFraming.TryUnframe(framed);
        Check("framed round-trip", unframed != null && single.PayloadEquals(unframed));
        // 8b. Our frame is recognized as ours and starts with the free marker byte 0.
        Check("our frame recognized", BirthWireFraming.IsOurPacket(framed));
        Check("frame leads with marker 0", framed.Length > 0 && framed[0] == 0);
        // 8c. CRITICAL: no real BT packet can be misread as ours. BT PacketType uses every byte
        //     1..255 as the FIRST byte; simulate one of each and assert none is "ours".
        bool anyBtCollision = false;
        for (int firstByte = 1; firstByte <= 255; firstByte++)
        {
            // A plausible BT packet: its type byte then arbitrary body (even a body that happens
            // to contain our magic later must not match — the marker gate is byte 0 only).
            byte[] btPacket = { (byte)firstByte, (byte)'B', (byte)'T', (byte)'C', (byte)'G', 1, 2, 3 };
            if (BirthWireFraming.IsOurPacket(btPacket))
            {
                anyBtCollision = true;
                break;
            }
        }
        Check("no BT packet (type 1..255) misread as ours", !anyBtCollision);
        // 8d. A leading-0 packet WITHOUT our magic is not ours (BT's empty-sentinel space stays clear).
        Check("leading-0 without magic is not ours", !BirthWireFraming.IsOurPacket(new byte[] { 0, 1, 2, 3, 4, 5 }));
        // 8e. Framing garbage/None is null/false, never a throw.
        Check("unframe null -> null", BirthWireFraming.TryUnframe(null) == null);
        Check("unframe too-short -> null", BirthWireFraming.TryUnframe(new byte[] { 0, (byte)'B' }) == null);
        Check("unframe framed-but-corrupt-body -> null", BirthWireFraming.TryUnframe(CorruptBody(framed)) == null);

        Console.WriteLine(_failures == 0
            ? "\nALL BIRTH-PAYLOAD TESTS PASSED"
            : "\n" + _failures + " TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void RoundTrips(string label, BirthPayloadData original)
    {
        byte[] wire = original.ToBytes();
        BirthPayloadData parsed = BirthPayloadData.FromBytes(wire);
        bool ok = parsed != null && original.PayloadEquals(parsed);
        // Prove idempotence too: re-serializing the parsed copy yields identical bytes.
        bool stable = parsed != null && AreEqual(wire, parsed.ToBytes());
        Check(label + " round-trip", ok);
        Check(label + " re-serialize identical", stable);
    }

    private static void NullOnGarbage(string label, byte[] bytes)
    {
        BirthPayloadData parsed = BirthPayloadData.FromBytes(bytes);
        Check(label + " -> null (no throw)", parsed == null);
    }

    private static byte[] CorruptBody(byte[] framed)
    {
        // Keep our valid 5-byte header, then a payload body that FromBytes will reject
        // (wrong format version byte at the body's first position).
        var corrupt = new byte[] { 0, (byte)'B', (byte)'T', (byte)'C', (byte)'G', 200, 1, 2 };
        return corrupt;
    }

    private static byte[] Truncate(byte[] full)
    {
        int keep = Math.Max(1, full.Length / 2);
        var cut = new byte[keep];
        Array.Copy(full, cut, keep);
        return cut;
    }

    private static byte[] WrongVersion(byte[] full)
    {
        var copy = (byte[])full.Clone();
        copy[0] = 200; // not CurrentFormatVersion
        return copy;
    }

    private static bool AreEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static void Check(string label, bool pass)
    {
        Console.WriteLine((pass ? "PASS " : "FAIL ") + label);
        if (!pass)
        {
            _failures++;
        }
    }
}
