using System;
using System.Collections;
using System.Collections.Generic;
using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class BitfieldMessage : BasePeerMessage
    {
        public override byte Type => (byte)PeerMessageType.Bitfield;
        public BitArray Bitfield { get; set; }

        public BitfieldMessage(BitArray bitfield)
        {
            Bitfield = bitfield;
        }

        private static byte[] BitArrayToByteArray(BitArray bits)
        {
            byte[] bytes = new byte[(bits.Length - 1) / 8 + 1];
            bits.CopyTo(bytes, 0);
            return bytes;
        }

        public override byte[] GetBytes()
        {
            List<byte> bytes = new List<byte>();
            var bitBytes = BitArrayToByteArray(Bitfield);
            bytes.AddRange(BitConverter.GetBytes(bitBytes.Length + 1));
            bytes.Add(Type);
            bytes.AddRange(bitBytes);
            return bytes.ToArray();
        }
    }
}