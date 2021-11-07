using System;
using System.Collections.Generic;
using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class HaveMessage : BasePeerMessage
    {
        public override int Length => 5;
        public override byte Type => (byte)PeerMessageType.Have;
        public int PieceIndex { get; set; }

        public HaveMessage(int piece)
        {
            PieceIndex = piece;
        }

        public override byte[] GetBytes()
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(Length));
            bytes.Add(Type);
            bytes.AddRange(BitConverter.GetBytes(PieceIndex));
            return bytes.ToArray();
        }
    }
}