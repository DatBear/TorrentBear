using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using TorrentBear.Data.Message.Peer;
using TorrentBear.Enum;

namespace TorrentBear.Service
{
    public class TorrentDownloader
    {
        private const int Version = 1;
        private static readonly Random r = new();
        private readonly string _name;
        private readonly TcpListener _listener;
        private readonly int _port;
        private readonly Torrent _torrent;
        private readonly string _downloadDirectory;
        private readonly byte[] _peerId;
        private readonly BitArray _bitfield;
        private readonly MemoryCache _cache;
        private readonly Dictionary<PeerConnection, TorrentPeerConnectionState> _connections;
        private static readonly Dictionary<string, string> DownloaderPeerNames = new();

        private bool _hasAllPieces;
        private bool HasAllPieces => _hasAllPieces || !_bitfield.Cast<bool>().Contains(false);
        private long _bytesDownloaded;

        private Timer _bandwidthTimer;
        public TorrentDownloader(string name, int port, string torrentFilePath, string downloadPath, BitfieldType bitfieldType = BitfieldType.FromFile)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _listener.BeginAcceptTcpClient(TcpListener_AcceptTcpClient, _listener);

            _name = name;
            _port = port;
            _connections = new Dictionary<PeerConnection, TorrentPeerConnectionState>();
            var randomPeerId = new byte[12];
            r.NextBytes(randomPeerId);
            var peerIdVersion = Encoding.ASCII.GetBytes($"-TB{Version:D4}-");
            _peerId = peerIdVersion.Concat(randomPeerId).ToArray();
            var parser = new BencodeParser();
            _torrent = parser.Parse<Torrent>(torrentFilePath);
            _downloadDirectory = downloadPath;
            _cache = new MemoryCache($"torrentdownloader_cache_{name}");

            _bytesDownloaded = bitfieldType == BitfieldType.Full ? _torrent.TotalSize : 0;
            CreateFiles(_torrent, downloadPath);
            _bitfield = bitfieldType == BitfieldType.FromFile
                ? GetBitfield(_torrent, downloadPath)
                : new BitArray(_torrent.NumberOfPieces, bitfieldType == BitfieldType.Full);
            _hasAllPieces = HasAllPieces;
            

            Console.WriteLine($"[{_peerId[18]:X2}{_peerId[19]:X2}] {_name} started");
            lock (DownloaderPeerNames)
            {
                DownloaderPeerNames[_name] = PeerIdString(_peerId);
                DownloaderPeerNames[PeerIdString(_peerId)] = _name;
            }
            
            _bandwidthTimer = new Timer(OutputInformation, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private string PeerIdString(byte[] peerId)
        {
            return BitConverter.ToString(peerId).Replace("-", string.Empty);
        }

        private string GetName(PeerConnection conn)
        {
            return DownloaderPeerNames[conn.PeerIdString];
        }
        
        public void Start(List<IPEndPoint> peers)
        {
            foreach (var peer in peers)
            {
                var client = new TcpClient(AddressFamily.InterNetwork);
                var endPoint = new IPEndPoint(IPAddress.Loopback, _port);
                if (Equals(endPoint, peer))
                    continue;

                try
                {
                    client.Connect(peer);
                    SetupConnection(client);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error Connecting to {peer}");
                }
            }

            Task.Run(ManageConnectionStateThread);
            Task.Run(DownloadThread);
        }

        private void TcpListener_AcceptTcpClient(IAsyncResult ar)
        {
            lock (_connections)
            {
                var client = _listener.EndAcceptTcpClient(ar);
                SetupConnection(client);
                _listener.BeginAcceptTcpClient(TcpListener_AcceptTcpClient, _listener);
            }
        }

        private void SetupConnection(TcpClient client, [CallerMemberName] string caller = null)
        {
            Debug.WriteLine($"[{caller}] {_name} -> {client.Client?.RemoteEndPoint}");
            var conn = new PeerConnection(this, client, _torrent.GetInfoHashBytes());
            lock(_connections)
                _connections.Add(conn, new TorrentPeerConnectionState());
            conn.Request += Connection_OnRequest;
            conn.Piece += Connection_OnPiece;
            conn.Have += Connection_OnHave;
            conn.HandshakeAccepted += Connection_OnHandshakeAccepted;
            
            SendHandshake(conn);
            SendBitfield(conn);
        }

        private Stopwatch Stopwatch = new();
        private void DownloadThread()
        {
            using var sha1 = new System.Security.Cryptography.SHA1CryptoServiceProvider();
            
            while (true)
            {
                if (HasAllPieces)
                {
                    break;
                }

                List<KeyValuePair<PeerConnection, TorrentPeerConnectionState>> connections;
                lock (_connections)
                    connections = _connections.ToList();
                
                foreach (var kvp in connections.Where(
                    x => x.Key.HandshakeState == PeerHandshakeState.HandshakeAccepted  && x.Key.Bitfield != null))
                {
                    var peer = kvp.Key;
                    var state = kvp.Value;
                    var peerName = GetName(peer);

                    var hasInterest = CheckInterest(peer.PeerIdString, peer.Bitfield);
                    if (hasInterest != state.IsInterested)
                    {
                        if (hasInterest)
                        {
                            SendInterested(peer);
                            state.IsInterested = true;
                        }
                        else
                        {
                            SendNotInterested(peer);
                            state.IsInterested = false;
                        }
                    }


                    if (state.PieceManager == null)
                    {
                        var exceptPieces = _connections.Values
                            .Select(x => x.PieceManager?.Piece ?? -1)
                            .Where(x => x >= 0).ToArray();
                        var random = GetRandomInterest(peer.Bitfield, exceptPieces);

                        var pieceSize = random == _torrent.NumberOfPieces - 1
                            ? _torrent.TotalSize % _torrent.PieceSize
                            : _torrent.PieceSize;
                        if (random >= 0)
                        {
                            state.PieceManager = new PieceManager(random, RequestMessage.DefaultRequestLength, pieceSize);
                            Stopwatch = new Stopwatch();
                            Stopwatch.Start();
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if ((state.PieceManager.ShouldSendRequest) && !peer.ConnectionState.IsChoked)
                    {
                        var request = state.PieceManager.GetNextRequest();
                        if (request != null)
                        {
                            state.PieceManager.SendRequest(peer, request);
                        }
                    }
                    else if (state.PieceManager.IsPieceComplete)
                    {
                        var ms = Stopwatch.ElapsedMilliseconds;
                        Log($"{GetName(peer)} [P{state.PieceManager.Piece:D3}], {ms}ms");
                        var torrentPieceHash =
                            _torrent.Pieces.Skip(state.PieceManager.Piece * 20).Take(20).ToArray();
                        var pieceBytes = state.PieceManager.Stream.ReadAllBytes();
                        var pieceHash = sha1.ComputeHash(pieceBytes);
                        if (Utils.SequenceEqual(pieceHash, torrentPieceHash))
                        {
                            WritePiece(_torrent, _downloadDirectory, state.PieceManager.Piece, pieceBytes);
                            var filePieceBytes = GetPiece(_torrent, _downloadDirectory, state.PieceManager.Piece,
                                true);
                            var fileSha = sha1.ComputeHash(filePieceBytes);
                            if (Utils.SequenceEqual(fileSha, torrentPieceHash))
                            {
                                BroadcastHave(state.PieceManager.Piece);
                                _bitfield.Set(state.PieceManager.Piece, true);
                                state.PieceManager = null;
                                if (_hasAllPieces = HasAllPieces)
                                {
                                    Log($"ALL PIECES DOWNLOADED");
                                    BroadcastNotInterested();
                                    foreach (var kvp2 in connections)
                                    {
                                        kvp2.Value.IsInterested = false;
                                    }
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"Error writing to file");
                            }
                        }
                        else
                        {
                            state.PieceManager = null;//restart piece download
                            Debug.WriteLine($"Error downloading piece {state.PieceManager.Piece}");
                        }
                    }
                }
            }
        }


        private void OutputInformation(object? asdf = null)
        {
            List<KeyValuePair<PeerConnection, TorrentPeerConnectionState>> connections;
            lock (_connections)
                connections = _connections.ToList();
            
            foreach (var kvp in connections.Where(x =>
                x.Key.HandshakeState == PeerHandshakeState.HandshakeAccepted && x.Key.Bitfield != null))
            {
                var peer = kvp.Key;
                var download = peer.DownloadBandwidth.GetAverageSpeed();
                var upload = peer.UploadBandwidth.GetAverageSpeed();
                if (download > 0.01 || upload > 0.01)
                {
                    Log($"{GetName(peer)} Down:{peer.DownloadBandwidth.GetAverageSpeed():F1}, Up:{peer.UploadBandwidth.GetAverageSpeed():F1}");
                }
            }
        }

        //this changes who's choked once every 10 seconds
        //reciprocates by unchoking the 4 best peers based on upload rates (download rates if we have all pieces)
        //unchokes uninterested peers
        //todo implement optimistic unchoking (unchoke 1 random peer every 30 seconds, with new connections 3x as likely to start as the optimistic unchoke)
        private void ManageConnectionStateThread()
        {
            while (true)
            {
                List<KeyValuePair<PeerConnection, TorrentPeerConnectionState>> connections;
                lock (_connections)
                    connections = _connections.Where(x =>
                        x.Key.HandshakeState == PeerHandshakeState.HandshakeAccepted && x.Key.Bitfield != null).ToList();

                foreach (var kvp in connections.Where(
                    x => x.Key.HandshakeState == PeerHandshakeState.HandshakeAccepted && x.Key.Bitfield != null))
                {
                    var peer = kvp.Key;
                    var state = kvp.Value;

                    
                }

                var bestInterestedPeers = connections.Where(x => x.Key.ConnectionState.IsInterested)
                    .OrderByDescending(x => HasAllPieces ? x.Key.UploadBandwidth.GetAverageSpeed() : x.Key.DownloadBandwidth.GetAverageSpeed()).Take(4);
                var uninterestedPeers = connections.Where(x => !x.Key.ConnectionState.IsInterested);

                var peersToUnchoke = bestInterestedPeers.Concat(uninterestedPeers);
                var peersNeedingUnchoke = peersToUnchoke.Where(x => x.Value.IsChoked);
                var peersNeedingChoke = connections.Except(peersToUnchoke).Where(x => !x.Value.IsChoked);

                foreach (var kvp in peersNeedingUnchoke)
                {
                    var peer = kvp.Key;
                    var state = kvp.Value;
                    SendUnChoke(peer);
                    state.IsChoked = false;
                }

                foreach (var kvp in peersNeedingChoke)
                {
                    var peer = kvp.Key;
                    var state = kvp.Value;
                    SendChoke(peer);
                    state.IsChoked = true;
                }

                Thread.Sleep(10000);
            }
        }


        public void Connection_OnRequest(PeerConnection sender, RequestMessage request)
        {
            var piece = GetPiece(_torrent, _downloadDirectory, request.Index);
            if (piece == null) return;
            var length = Math.Min(request.RequestedLength, piece.Length - request.Begin);
            var block = new byte[length];
            Buffer.BlockCopy(piece, request.Begin, block, 0, block.Length);
            var msg = new PieceMessage(request.Index, request.Begin, block);
            SendPiece(sender, msg);
        }
        
        public void Connection_OnPiece(PeerConnection sender, PieceMessage piece)
        {
            var state = GetPeerState(sender);
            state.PieceManager.Write(piece);
        }

        public void Connection_OnHave(PeerConnection sender, int have)
        {
            _cache.Remove($"piece_{have}");
        }

        public void Connection_OnHandshakeAccepted(PeerConnection sender)
        {
            var peerName = DownloaderPeerNames[sender.PeerIdString];
            Debug.WriteLine($"{_name}->{peerName}:{sender.Endpoint.Port} handshake accepted");
        }

        public TorrentPeerConnectionState GetPeerState(PeerConnection peer)
        {
            lock (_connections)
                return _connections[peer];
        }

        private bool CheckInterest(string peerId, BitArray bitfield)
        {
            if (HasAllPieces) return false;
            if (_bitfield.Length != bitfield.Length) return false;
            for (var i = 0; i < bitfield.Length; i++)
            {
                if (!_bitfield.Get(i) && bitfield.Get(i))
                {
                    return true;
                }
            }
            return false;
        }

        private int GetRandomInterest(BitArray bitfield, params int[] except)
        {
            if (HasAllPieces) return -1;
            if (_bitfield.Length != bitfield.Length) return -1;
            var interest = new List<int>();
            for (var i = 0; i < bitfield.Length; i++)
            {
                if (!_bitfield.Get(i) && bitfield.Get(i))
                {
                    interest.Add(i);
                }
            }

            while (interest.Any() && except.Contains(interest[0])) interest.Remove(interest[0]);
            var interestArray = interest.ToArray();
            r.Shuffle(interestArray);
            return interestArray.Any() ? interestArray[0] : -1;
        }


        void SendHandshake(PeerConnection conn)
        {
            var bytes = new HandshakeMessage(_torrent.GetInfoHashBytes(), _peerId).GetBytes();
            conn.Write(bytes);
        }

        void SendBitfield(PeerConnection conn)
        {
            var bitfield = _bitfield ?? GetBitfield(_torrent, _downloadDirectory);
            var bytes = new BitfieldMessage(bitfield).GetBytes();
            conn.Write(bytes);
        }

        void SendInterested(PeerConnection conn)
        {
            Log($"{GetName(conn)} [I]");
            var bytes = new InterestedMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendNotInterested(PeerConnection conn)
        {
            Log($"{GetName(conn)} [NI]");
            var bytes = new NotInterestedMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendChoke(PeerConnection conn)
        {
            Log($"{GetName(conn)} [C]");
            var bytes = new ChokeMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendUnChoke(PeerConnection conn)
        {
            Log($"{GetName(conn)} [UC]");
            var bytes = new UnChokeMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendPiece(PeerConnection conn, PieceMessage piece)
        {
            var bytes = piece.GetBytes();
            conn.Write(bytes);
        }

        void BroadcastHave(int piece)
        {
            var bytes = new HaveMessage(piece).GetBytes();
            foreach(var peer in _connections.Keys)//lock _connections?
            {
                peer.Write(bytes);
            }
        }

        void BroadcastNotInterested()
        {
            var bytes = new NotInterestedMessage().GetBytes();
            foreach (var peer in _connections.Keys)
            {
                peer.Write(bytes);
            }
        }

        private void CreateFiles(Torrent torrent, string downloadDirectory)
        {
            foreach (var file in torrent.Files)
            {
                var dirPath = downloadDirectory;
                foreach (var dir in file.Path)
                {
                    if (!Directory.Exists(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }

                    dirPath = Path.Combine(dirPath, dir);
                }

                var filePath = Path.Combine(downloadDirectory, file.FullPath);
                if (!File.Exists(filePath))
                {
                    using var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    fs.SetLength(file.FileSize);
                }
            }
        }

        private bool WritePiece(Torrent torrent, string downloadDirectory, int piece, byte[] pieceBytes)
        {
            var pieceSize = torrent.PieceSize;
            var files = torrent.Files;
            var offset = pieceSize * piece;
            var bytesWritten = 0;

            foreach (var file in files)
            {
                if (offset - file.FileSize >= 0)
                {
                    offset -= file.FileSize;
                    continue;
                }

                var path = Path.Combine(downloadDirectory, file.FullPath);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                if (offset > 0)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                }

                var writeLength = Math.Min(stream.Length - offset, pieceSize - bytesWritten);
                stream.Write(pieceBytes, bytesWritten, (int)writeLength);
                bytesWritten += (int)writeLength;
                offset = 0;
                if (bytesWritten == pieceSize) break;
            }

            return true;
        }


        private byte[] GetPiece(Torrent torrent, string downloadDirectory, int piece, bool skipCache = false)
        {
            var cacheKey = $"piece_{piece}";
            if (!skipCache)
            {
                if (_cache.Contains(cacheKey))
                {
                    return (byte[])_cache.Get(cacheKey);
                }
            }

            var pieceSize = torrent.PieceSize;
            var files = torrent.Files;
            var offset = pieceSize * piece;
            var bytesRemaining = pieceSize;
            byte[] byteBuffer = new byte[pieceSize];
            var buffer = new byte[pieceSize];
            var totalBytesRead = 0;
            foreach (var file in files)
            {
                if (offset - file.FileSize >= 0)
                {
                    offset -= file.FileSize;
                    continue;
                }

                var path = Path.Combine(downloadDirectory, file.FullPath);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (offset > 0)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                }

                var bytesRead = stream.Read(byteBuffer, 0, (int)Math.Min(stream.Length - offset, bytesRemaining));
                offset = 0;
                bytesRemaining -= bytesRead;
                Buffer.BlockCopy(byteBuffer, 0, buffer, totalBytesRead, bytesRead);
                totalBytesRead += bytesRead;

                if (bytesRemaining == 0) break;
            }

            Array.Resize(ref buffer, totalBytesRead);
            if(!skipCache)
                _cache.Set(cacheKey, buffer, DateTimeOffset.Now.Add(TimeSpan.FromSeconds(30)));
            return buffer.ToArray();
        }


        private BitArray GetBitfield(Torrent torrent, string downloadDirectory)
        {
            var bitfield = new BitArray(torrent.NumberOfPieces);
            using var sha1 = new System.Security.Cryptography.SHA1CryptoServiceProvider();
            var fileIdx = 0;
            var piece = 0;
            var pieceSize = torrent.PieceSize;
            int pieceBytesRead = 0;
            var byteBuffer = new byte[pieceSize];
            var buffer = new byte[pieceSize];

            while (piece < torrent.NumberOfPieces)
            {
                var file = torrent.Files[fileIdx];
                var path = Path.Combine(downloadDirectory, file.FullPath);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var prevBytesRead =
                    fileIdx > 0 ? (int)(torrent.Files.Take(fileIdx).Sum(x => x.FileSize) % pieceSize) : 0;
                int bytesRead;

                var fileOffset = 0;
                do
                {
                    bytesRead = stream.Read(byteBuffer, 0,
                        (int)Math.Min(stream.Length - fileOffset, pieceSize - prevBytesRead));
                    fileOffset += bytesRead;
                    pieceBytesRead += bytesRead;
                    Buffer.BlockCopy(byteBuffer, 0, buffer, prevBytesRead, bytesRead);

                    if (pieceBytesRead < pieceSize)
                    {
                        if (fileIdx < torrent.Files.Count - 1)
                        {
                            fileIdx++;
                            break;
                        }

                        Array.Resize(ref buffer, pieceBytesRead);
                    }

                    prevBytesRead = 0;
                    pieceBytesRead = 0;

                    //buffer right now is bytes of piece, so when the sha1 of buffer = torrent's piece hash, piece is valid
                    var hash = sha1.ComputeHash(buffer);
                    var torrentHash = torrent.Pieces.Skip(piece * 20).Take(20).ToArray();
                    if (Utils.SequenceEqual(hash, torrentHash))
                    {
                        bitfield.Set(piece, true);
                        _bytesDownloaded += torrent.PieceSize;
                    }
                    else
                    {
                        //Log($"Piece {piece} is incorrect");
                    }

                    piece++;
                } while (bytesRead > 0 && piece < torrent.NumberOfPieces);
            }

            return bitfield;
        }

        void Log(string str)
        {
            Debug.WriteLine($"{_name}>{str}");
        }
    }
}