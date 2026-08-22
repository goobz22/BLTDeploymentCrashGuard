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
