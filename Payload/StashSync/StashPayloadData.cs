using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BLTDeploymentCrashGuard.StashSync
{
    /// <summary>
    /// Pure, engine-free wire model for "a settlement stash's full contents". Deliberately has
    /// NO TaleWorlds dependency so the byte round-trip is unit-testable headless
    /// (tests/StashPayloadTest links this exact file). The game-integration layer
    /// (StashSyncGuard) builds one from a real Settlement.Stash roster and applies one to it.
    ///
    /// A full SNAPSHOT, not a delta: idempotent to re-apply, immune to ordering, and the
    /// receiver converges on the sender's state in one packet (last-close-wins on the rare
    /// simultaneous edit).
    ///
    /// Format (BinaryWriter/BinaryReader, length-prefixed strings, little-endian):
    ///   byte    FormatVersion
    ///   string  SettlementStringId
    ///   int32   entryCount
    ///   entryCount × { string ItemStringId, string ModifierStringId ("" = none), int32 Count }
    /// The transport marker/magic is NOT part of this payload — the framing layer prepends it.
    /// </summary>
    public sealed class StashPayloadData
    {
        public const byte CurrentFormatVersion = 1;

        public byte FormatVersion = CurrentFormatVersion;
        public string SettlementStringId = "";
        public List<Entry> Entries = new List<Entry>();

        public sealed class Entry
        {
            public string ItemStringId = "";
            public string ModifierStringId = "";
            public int Count;

            public bool ValueEquals(Entry other)
            {
                return other != null
                    && ItemStringId == other.ItemStringId
                    && ModifierStringId == other.ModifierStringId
                    && Count == other.Count;
            }
        }

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(FormatVersion);
                writer.Write(SettlementStringId ?? "");
                writer.Write(Entries.Count);
                foreach (Entry entry in Entries)
                {
                    writer.Write(entry.ItemStringId ?? "");
                    writer.Write(entry.ModifierStringId ?? "");
                    writer.Write(entry.Count);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>Parse payload bytes; null on anything malformed (never throws).</summary>
        public static StashPayloadData FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1)
            {
                return null;
            }
            try
            {
                using (var stream = new MemoryStream(bytes))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var payload = new StashPayloadData
                    {
                        FormatVersion = reader.ReadByte()
                    };
                    if (payload.FormatVersion != CurrentFormatVersion)
                    {
                        return null; // unknown future format — drop rather than misparse
                    }
                    payload.SettlementStringId = reader.ReadString();
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 100000)
                    {
                        return null;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        payload.Entries.Add(new Entry
                        {
                            ItemStringId = reader.ReadString(),
                            ModifierStringId = reader.ReadString(),
                            Count = reader.ReadInt32()
                        });
                    }
                    return payload;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
