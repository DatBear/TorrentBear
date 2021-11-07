using System;
using System.Collections.Generic;
using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class PieceMessage : BasePeerMessage
    {
        public override byte Type => (byte)PeerMessageType.Piece;
        public int Index { get; set; }
        public int Begin { get; set; }
        public byte[] Block { get; set; }

        public PieceMessage(int index, int begin, byte[] block)
        {
            Index = index;
            Begin = begin;
            Block = block;
            Length = 9 + Block.Length;
        }

        public override byte[] GetBytes()
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(Length));
            bytes.Add(Type);
            bytes.AddRange(BitConverter.GetBytes(Index));
            bytes.AddRange(BitConverter.GetBytes(Begin));
            bytes.AddRange(Block);
            return bytes.ToArray();
        }
    }
}