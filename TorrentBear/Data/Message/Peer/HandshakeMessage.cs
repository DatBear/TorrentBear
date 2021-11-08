using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TorrentBear.Data.Message.Peer
{
    public class HandshakeMessage : BasePeerMessage
    {
        public static int PacketLength => StartBytes().Length + 40;
        private static byte[] _startBytes;
        static HandshakeMessage()
        {
            var bytes = new List<byte> { 19 };
            bytes.AddRange(Encoding.ASCII.GetBytes("BitTorrent Protocol"));
            bytes.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
            _startBytes = bytes.ToArray();
        }

        private byte[] _infoHash;
        private byte[] _peerId;

        public static ReadOnlySpan<byte> StartBytes()
        {
            return _startBytes;
        }
        
        public HandshakeMessage(byte[] infoHash, byte[] peerId)
        {
            _infoHash = infoHash;
            _peerId = peerId;
        }

        public override byte[] GetBytes()
        {
            var bytes = StartBytes().ToArray().ToList();
            bytes.AddRange(_infoHash);
            bytes.AddRange(_peerId);
            return bytes.ToArray();
        }
    }
}