using TorrentBear.Enum;

namespace TorrentBear.Service
{
    public class PeerConnectionState
    {
        public PeerState PeerState { get; private set; }
        public bool IsChoked
        {
            get => (PeerState & PeerState.Choked) > 0;
            set
            {
                if (value)
                {
                    PeerState |= PeerState.Choked;
                }
                else
                {
                    PeerState &= ~PeerState.Choked;
                }
            }
        }

        public bool IsInterested
        {
            get => (PeerState & PeerState.Interested) > 0;
            set
            {
                if (value)
                {
                    PeerState |= PeerState.Interested;
                }
                else
                {
                    PeerState &= ~PeerState.Interested;
                }
            }
        } 
    }
}