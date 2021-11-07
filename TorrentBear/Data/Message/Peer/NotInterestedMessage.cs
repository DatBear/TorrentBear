using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class NotInterestedMessage : BasePeerMessage
    {
        public override int Length => 1;
        public override byte Type => (byte)PeerMessageType.NotInterested;
    }
}