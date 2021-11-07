using System;

namespace TorrentBear.Enum
{
    [Flags]
    public enum PeerState
    {
        Choked = 1,
        Interested = 1 << 1
    }
}