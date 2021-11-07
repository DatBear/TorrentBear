using System;

namespace TorrentBear.Data.Message.Peer
{
    public class KeepAliveMessage : BasePeerMessage
    {
        public override byte[] GetBytes()
        { 
            return BitConverter.GetBytes(0);
        }
    }
}