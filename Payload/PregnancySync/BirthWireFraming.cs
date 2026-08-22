using System;

namespace BLTDeploymentCrashGuard.PregnancySync
{
    /// <summary>
    /// Engine-free framing that lets our birth packets ride BannerlordTogether's LiteNetLib
    /// channel without colliding with any BT packet. Decompile-proven facts this relies on:
    ///
    ///  - BT dispatches by first byte: PacketSerializer.Dispatch(data) = (PacketType)data[0].
    ///  - PacketType (a byte enum) uses EVERY value 1..255 — the ONLY free byte is 0.
    ///  - Byte 0 is BT's "empty/null packet" sentinel: OnNetworkReceive already rejects
    ///    zero-length packets, and the dispatch switch has NO case for value 0 and NO default,
    ///    so a non-empty packet whose first byte is 0 is a guaranteed no-op inside BT even if
    ///    our interception ever missed it. So leading byte 0 is safe twice over.
    ///
    /// Frame = [0x00 marker] [4-byte MAGIC "BTCG"] [BirthPayloadData bytes]. The magic makes our
    /// packets unambiguous among any theoretical leading-0 traffic, and makes misreading a real
    /// BT packet as ours impossible (a real BT packet never starts with 0).
    /// </summary>
    public static class BirthWireFraming
    {
        public const byte Marker = 0x00;
        // "BTCG" = BannerlordTogether Child Guard.
        private static readonly byte[] Magic = { (byte)'B', (byte)'T', (byte)'C', (byte)'G' };
        private const int HeaderLength = 1 + 4; // marker + magic

        public static byte[] Frame(BirthPayloadData payload)
        {
            if (payload == null)
            {
                return null;
            }
            byte[] body = payload.ToBytes();
            var framed = new byte[HeaderLength + body.Length];
            framed[0] = Marker;
            Array.Copy(Magic, 0, framed, 1, Magic.Length);
            Array.Copy(body, 0, framed, HeaderLength, body.Length);
            return framed;
        }

        /// <summary>True only if these bytes are one of OUR framed birth packets. Cheap enough to
        /// call on every inbound packet (checks 5 leading bytes before doing anything else).</summary>
        public static bool IsOurPacket(byte[] data)
        {
            if (data == null || data.Length < HeaderLength || data[0] != Marker)
            {
                return false;
            }
            for (int i = 0; i < Magic.Length; i++)
            {
                if (data[1 + i] != Magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Parse a framed packet back to its payload. Returns null on anything that is
        /// not our well-formed packet (never throws) — a bad packet is logged and dropped.</summary>
        public static BirthPayloadData TryUnframe(byte[] data)
        {
            if (!IsOurPacket(data))
            {
                return null;
            }
            var body = new byte[data.Length - HeaderLength];
            Array.Copy(data, HeaderLength, body, 0, body.Length);
            return BirthPayloadData.FromBytes(body);
        }
    }
}
