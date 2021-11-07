using TorrentBear.Enum;

namespace TorrentBear.Data.Message.Peer
{
    public class ChokeMessage : BasePeerMessage
    {
        public override byte Type => (byte)PeerMessageType.Choke;
    }
}