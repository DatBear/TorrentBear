using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TorrentBear.Data.Message.Peer;
using TorrentBear.Enum;

namespace TorrentBear.Service
{
    public class PieceManager
    {
        private const int MaxQueueLength = 4;
        public int Piece { get; set; }
        public Stream Stream { get; set; }
        private long _pieceSize;
        public bool IsPieceComplete => Stream.Length >= _pieceSize && _requests.All(x => x.Value == SendState.Received);

        private Dictionary<RequestMessage, SendState> _requests;

        public PieceManager(int piece, int requestLength, long pieceSize)
        {
            Piece = piece;
            _pieceSize = pieceSize;
            Stream = new MemoryStream();
            _requests = new Dictionary<RequestMessage, SendState>();
            for (var i = 0; i <= pieceSize - requestLength; i += requestLength)
            {
                _requests.Add(new RequestMessage(piece, i), SendState.NotSent);
            }
        }

        public void SendRequest(PeerConnection conn, RequestMessage request)
        {
            Debug.WriteLine($"sending request for {request.Index}:{request.Begin}");
            conn.Write(request.GetBytes());
            _requests[request] = SendState.Sent;
        }

        public void Write(PieceMessage msg)
        {
            Stream.Seek(msg.Begin, SeekOrigin.Begin);
            Stream.Write(msg.Block);
            var request = _requests.FirstOrDefault(x => x.Key.Index == msg.Index && x.Key.Begin == msg.Begin).Key;
            if (request != null)
            {
                lock (_requests)
                {
                    _requests[request] = SendState.Received;
                }
            }
            IsRequestComplete = true;
        }

        public RequestMessage GetNextRequest()
        {
            if (_requests.Values.Count(x => x == SendState.Sent) >= MaxQueueLength) return null;//!ShouldSendRequest?

            var unsent = _requests.Where(x => x.Value == SendState.NotSent).Select(x => x.Key);
            if (unsent.Any())
            {
                return unsent.First();
            }

            return null;
        }


        private List<RequestMessage> UnsentRequests =>
            _requests.Where(x => x.Value == SendState.NotSent).Select(x => x.Key).ToList();

        public List<RequestMessage> PendingRequests => _requests.Where(x => x.Value == SendState.Sent)
            .Select(x => x.Key).ToList();

        public bool ShouldSendRequest => PendingRequests.Count < MaxQueueLength && UnsentRequests.Any();

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