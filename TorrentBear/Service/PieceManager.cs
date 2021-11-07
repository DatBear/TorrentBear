using System.IO;
using TorrentBear.Data.Message.Peer;

namespace TorrentBear.Service
{
    //todo make this track piece progress
    public class PieceManager
    {
        public int Piece { get; set; }
        public Stream Stream { get; set; }
        private long _pieceSize;
        public bool IsComplete => Stream.Length >= _pieceSize;

        public PieceManager(int piece, long pieceSize)
        {
            Piece = piece;
            _pieceSize = pieceSize;
            Stream = new MemoryStream();
        }

        public void Write(PieceMessage msg)
        {
            Stream.Seek(msg.Begin, SeekOrigin.Begin);
            Stream.Write(msg.Block);
            IsRequestComplete = true;
        }

        public RequestMessage GetNextRequest()
        {
            if (CurrentRequestMessage.Begin + CurrentRequestMessage.RequestedLength > _pieceSize)
            {
                return null;
            }
            
            return new RequestMessage(CurrentRequestMessage.Index,
                CurrentRequestMessage.Begin + CurrentRequestMessage.RequestedLength);
        }

        private RequestMessage _currentRequestMessage;
        public RequestMessage CurrentRequestMessage
        {
            get => _currentRequestMessage;
            set
            {
                IsRequestComplete = false;
                _currentRequestMessage = value;
            }
        }
        public bool IsRequestComplete { get; private set; }
    }
}