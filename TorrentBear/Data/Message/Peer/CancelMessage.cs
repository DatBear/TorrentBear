using System;
using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class CancelMessage : BasePeerMessage
    {
        public override byte Type => (byte)PeerMessageType.Cancel;

        public override byte[] GetBytes()
        {
            throw new NotImplementedException();
        }
    }
}