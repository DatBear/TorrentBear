using System.Collections;
using TorrentBear.Enum;

namespace TorrentBear.Service
{
    public class PeerConnectionState
    {
        public PeerState PeerState { get; private set; }
        public bool IsChoked
        {
            get => (PeerState & PeerState.Choked) > 0;
            set => PeerState = value ? PeerState | PeerState.Choked : PeerState & ~PeerState.Choked;
        }

        public bool IsInterested
        {
            get => (PeerState & PeerState.Interested) > 0;
            set => PeerState = value ? PeerState | PeerState.Interested : PeerState & ~PeerState.Interested;
        }

        //public BitArray Bitfield { get; set; }

        public PeerConnectionState()
        {
            IsChoked = true;
        }
    }
}