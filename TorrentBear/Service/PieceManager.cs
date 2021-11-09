using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using TorrentBear.Data.Message.Peer;
using TorrentBear.Enum;

namespace TorrentBear.Service
{
    public class PieceManager
    {
        private const int MaxQueueLength = 10;
        public int Piece { get; set; }
        public Stream Stream { get; set; }
        private long _pieceSize;
        public bool IsPieceComplete => Stream.Length >= _pieceSize && _requests.All(x => x.Value);

        private Dictionary<RequestMessage, bool> _requests;
        private MemoryCache _pendingRequestCache = MemoryCache.Default;

        public PieceManager(int piece, int requestLength, long pieceSize)
        {
            Piece = piece;
            _pieceSize = pieceSize;
            Stream = new MemoryStream();
            _requests = new Dictionary<RequestMessage, bool>();
            int i;
            for (i = 0; i + requestLength <= pieceSize; i += requestLength)
            {
                _requests.Add(new RequestMessage(piece, i, requestLength), false);
            }

            if (i + requestLength > pieceSize && i < pieceSize)
            {
                _requests.Add(new RequestMessage(piece, i, (int)(pieceSize - i)), false);
            }
        }

        ~PieceManager()
        {
            Stream.Dispose();
            _pendingRequestCache.Dispose();
        }

        public void SendRequest(PeerConnection conn, RequestMessage request)
        {
            //Debug.WriteLine($"sending request for {request.Index}:{request.Begin}");
            conn.Write(request.GetBytes());
            _pendingRequestCache.Set($"request_{request.Index}:{request.Begin}", request, DateTimeOffset.Now.Add(TimeSpan.FromSeconds(3)));
        }

        public void Write(PieceMessage msg)
        {
            Stream.Seek(msg.Begin, SeekOrigin.Begin);
            Stream.Write(msg.Block);
            var request = _requests.FirstOrDefault(x => x.Key.Index == msg.Index && x.Key.Begin == msg.Begin).Key;
            if (request != null)
            {
                _pendingRequestCache.Remove($"request_{msg.Index}:{msg.Begin}");
                _requests[request] = true;
            }
        }

        public RequestMessage GetNextRequest()
        {
            if (!ShouldSendRequest) return null;

            var unsent = UnsentRequests;
            return unsent.Any() ? unsent.First() : null;
        }


        private List<RequestMessage> UnsentRequests =>
            _requests.Where(x => !x.Value).Select(x => x.Key)
                .Where(x => !_pendingRequestCache.Contains($"request_{x.Index}:{x.Begin}"))
                .ToList();

        public List<RequestMessage> PendingRequests => _requests.Select(x => x.Key)
            .Where(x => _pendingRequestCache.Contains($"request_{x.Index}:{x.Begin}"))
            .ToList();

        public bool ShouldSendRequest => PendingRequests.Count < MaxQueueLength && UnsentRequests.Any();
    }
}