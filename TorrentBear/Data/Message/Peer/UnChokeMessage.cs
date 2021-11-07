using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class UnChokeMessage : BasePeerMessage
    {
        public override int Length => 1;
        public override byte Type => (byte)PeerMessageType.Unchoke;
    }
}