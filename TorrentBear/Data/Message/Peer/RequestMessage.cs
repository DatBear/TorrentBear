using System;
using System.Collections.Generic;
using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class RequestMessage : BasePeerMessage
    {
        public static int DefaultRequestLength => (int)Math.Pow(2, 14);
        public override int Length => 13;
        public override byte Type => (byte)PeerMessageType.Request;
        public int Index { get; set; }
        public int Begin { get; set; }
        public int RequestedLength { get; set; }

        public RequestMessage(int index, int begin)
        {
            Index = index;
            Begin = begin;
            RequestedLength = DefaultRequestLength;
        }

        public override byte[] GetBytes()
        {
            var bytes = new List<byte>();//base.GetBytes?
            bytes.AddRange(BitConverter.GetBytes(Length));
            bytes.Add(Type);
            bytes.AddRange(BitConverter.GetBytes(Index));
            bytes.AddRange(BitConverter.GetBytes(Begin));
            bytes.AddRange(BitConverter.GetBytes(RequestedLength));
            return bytes.ToArray();
        }
    }
}