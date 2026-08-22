using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BLTDeploymentCrashGuard.PregnancySync
{
    /// <summary>
    /// Pure, engine-free wire model for a "child born on the host" event. Deliberately has NO
    /// TaleWorlds dependency so the byte round-trip is unit-testable headless (scratchpad/
    /// BirthPayloadTest links this exact file). The game-integration layer (PregnancySync)
    /// builds one of these from a real Hero and reconstructs the child from it.
    ///
    /// Format (all via BinaryWriter/BinaryReader, length-prefixed strings, little-endian):
    ///   byte    FormatVersion
    ///   string  MotherStringId
    ///   int32   StillbornCount
    ///   int32   childCount
    ///   childCount × ChildIdentity
    /// A leading transport MARKER byte is NOT part of this payload — the send layer prepends it.
    /// </summary>
    public sealed class BirthPayloadData
    {
        public const byte CurrentFormatVersion = 1;

        public byte FormatVersion = CurrentFormatVersion;
        public string MotherStringId = "";
        public int StillbornCount;
        public List<ChildIdentity> Children = new List<ChildIdentity>();

        public sealed class ChildIdentity
        {
            // Only the fields the client cannot deterministically re-derive are on the wire.
            // Clan, culture and birthday are NOT sent: DeliverOffSpring(mother, father) reproduces
            // them identically on the client from the (same) parents — serializing them would be
            // redundant, and CampaignTime has no public round-trippable form anyway. What IS forced
            // from the host (randomized by DeliverOffSpring, so must be replicated): id, gender,
            // name, appearance.
            public string StringId = "";
            public bool IsFemale;
            public string FirstName = "";
            public string BodyPropertiesXml = "";
            public string FatherStringId = "";

            public bool IdentityEquals(ChildIdentity other)
            {
                return other != null
                    && StringId == other.StringId
                    && IsFemale == other.IsFemale
                    && FirstName == other.FirstName
                    && BodyPropertiesXml == other.BodyPropertiesXml
                    && FatherStringId == other.FatherStringId;
            }
        }

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(FormatVersion);
                WriteString(writer, MotherStringId);
                writer.Write(StillbornCount);
                writer.Write(Children != null ? Children.Count : 0);
                if (Children != null)
                {
                    foreach (ChildIdentity child in Children)
                    {
                        WriteString(writer, child.StringId);
                        writer.Write(child.IsFemale);
                        WriteString(writer, child.FirstName);
                        WriteString(writer, child.BodyPropertiesXml);
                        WriteString(writer, child.FatherStringId);
                    }
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>Parse a payload. Returns null on any malformed input (never throws) — a bad
        /// packet must never take the game down; the caller logs and drops it.</summary>
        public static BirthPayloadData FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1)
            {
                return null;
            }
            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var data = new BirthPayloadData();
                    data.FormatVersion = reader.ReadByte();
                    if (data.FormatVersion != CurrentFormatVersion)
                    {
                        return null; // a newer/older peer — drop rather than misparse
                    }
                    data.MotherStringId = ReadString(reader);
                    data.StillbornCount = reader.ReadInt32();
                    int childCount = reader.ReadInt32();
                    if (childCount < 0 || childCount > 16)
                    {
                        return null; // sane bound: a birth is 1–2, allow slack, reject garbage
                    }
                    for (int i = 0; i < childCount; i++)
                    {
                        var child = new ChildIdentity
                        {
                            StringId = ReadString(reader),
                            IsFemale = reader.ReadBoolean(),
                            FirstName = ReadString(reader),
                            BodyPropertiesXml = ReadString(reader),
                            FatherStringId = ReadString(reader)
                        };
                        data.Children.Add(child);
                    }
                    return data;
                }
            }
            catch
            {
                return null;
            }
        }

        public bool PayloadEquals(BirthPayloadData other)
        {
            if (other == null
                || FormatVersion != other.FormatVersion
                || MotherStringId != other.MotherStringId
                || StillbornCount != other.StillbornCount
                || (Children?.Count ?? 0) != (other.Children?.Count ?? 0))
            {
                return false;
            }
            for (int i = 0; i < (Children?.Count ?? 0); i++)
            {
                if (!Children[i].IdentityEquals(other.Children[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value ?? "");
        }

        private static string ReadString(BinaryReader reader)
        {
            return reader.ReadString() ?? "";
        }
    }
}
