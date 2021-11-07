namespace TorrentBear.Enum
{
    public enum PeerMessageType : byte
    {
        Choke,
        Unchoke,
        Interested,
        NotInterested,
        Have,
        Bitfield,
        Request,
        Piece,
        Cancel,
        Handshake = byte.MaxValue
    }
}