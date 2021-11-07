using TorrentBear.Data.Message.Peer;

namespace TorrentBear.Service
{
    public class TorrentPeerConnectionState : PeerConnectionState
    {
        public PieceManager PieceManager { get; set; }
    }
}