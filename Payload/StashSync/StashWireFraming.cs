using System;

namespace BLTDeploymentCrashGuard.StashSync
{
    /// <summary>
    /// Framing for stash packets on BannerlordTogether's LiteNetLib channel. Same transport
    /// facts as BirthWireFraming (leading byte 0 is the one PacketType value BT never
    /// dispatches, so our packets are a no-op inside BT even unintercepted), but a DIFFERENT
    /// magic — "BTCS" — so birth packets and stash packets can never misparse as each other:
    /// each feature's receive hook recognizes exactly its own magic and passes everything
    /// else through.
    ///
    /// Frame = [0x00 marker] [4-byte MAGIC "BTCS"] [StashPayloadData bytes].
    /// </summary>
    public static class StashWireFraming
    {
        public const byte Marker = 0x00;
        // "BTCS" = BannerlordTogether Crash-guard Stash.
        private static readonly byte[] Magic = { (byte)'B', (byte)'T', (byte)'C', (byte)'S' };
        private const int HeaderLength = 1 + 4; // marker + magic

        public static byte[] Frame(StashPayloadData payload)
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

        /// <summary>True only for OUR framed stash packets (checks 5 leading bytes).</summary>
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

        /// <summary>Parse a framed packet back to its payload; null when it is not our
        /// well-formed stash packet (never throws).</summary>
        public static StashPayloadData TryUnframe(byte[] data)
        {
            if (!IsOurPacket(data))
            {
                return null;
            }
            var body = new byte[data.Length - HeaderLength];
            Array.Copy(data, HeaderLength, body, 0, body.Length);
            return StashPayloadData.FromBytes(body);
        }
    }
}
