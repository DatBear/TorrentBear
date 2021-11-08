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
        public int Index { get; }
        public int Begin { get; }
        public int RequestedLength { get; }

        public RequestMessage(int index, int begin, int? requestedLength = null)
        {
            Index = index;
            Begin = begin;
            RequestedLength = requestedLength ?? DefaultRequestLength;
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

        protected bool Equals(RequestMessage other)
        {
            return Index == other.Index && Begin == other.Begin;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((RequestMessage)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Index, Begin);
        }
    }
}