using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using TorrentBear.Data.Message.Peer;
using TorrentBear.Enum;

namespace TorrentBear.Service
{
    public class TorrentConnection
    {
        private static Random r = new();
        private string _name;
        private TcpListener _listener;
        private Dictionary<PeerConnection, TorrentPeerConnectionState> _connections;
        private int _port;
        private List<IPEndPoint> _peers;
        private Torrent _torrent;
        private string _downloadDirectory;
        private byte[] _peerId;
        private BitArray _bitfield;
        private bool _hasAllPieces;

        public TorrentConnection(string name, int port, List<IPEndPoint> peers, string torrentFilePath,
            string downloadPath)
        {
            _name = name;
            _port = port;
            _peers = peers;
            _connections = new Dictionary<PeerConnection, TorrentPeerConnectionState>();
            _peerId = new byte[20];
            r.NextBytes(_peerId);
            var parser = new BencodeParser();
            _torrent = parser.Parse<Torrent>(torrentFilePath);
            _downloadDirectory = downloadPath;

            CreateFiles(_torrent, downloadPath);
            _bitfield = GetBitfield(_torrent, downloadPath);

            _listener = TcpListener.Create(port);
            _listener.Start();
            _listener.BeginAcceptTcpClient(TcpListener_AcceptTcpClient, _listener);
        }

        public void Start()
        {
            foreach (var peer in _peers)
            {
                var endPoint = new IPEndPoint(IPAddress.Loopback, _port);
                var conn = _connections.FirstOrDefault(x => Equals(x.Key.Endpoint, endPoint)).Key;
                if (conn == null)
                {
                    var client = new TcpClient(new IPEndPoint(IPAddress.Loopback, _port));
                    try
                    {
                        client.Connect(peer);
                        conn = new PeerConnection(client, _torrent.GetInfoHashBytes());
                        _connections.Add(conn, new TorrentPeerConnectionState());
                        SetupConnection(conn);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                    }
                }
            }

            Task.Run(DownloadThread);
        }

        private void TcpListener_AcceptTcpClient(IAsyncResult ar)
        {
            lock (_connections)
            {
                var client = _listener.EndAcceptTcpClient(ar);
                var conn = new PeerConnection(client, _torrent.GetInfoHashBytes());
                _connections.Add(conn, new TorrentPeerConnectionState());
                SetupConnection(conn);
                _listener.BeginAcceptTcpClient(TcpListener_AcceptTcpClient, _listener);
            }
        }

        private void SetupConnection(PeerConnection conn)
        {
            conn.Start();
            SendHandshake(conn);
            SendBitfield(conn);
            conn.Request += Connection_OnRequest;
            conn.Piece += Connection_OnPiece;
        }

        private void DownloadThread()
        {
            using var sha1 = new System.Security.Cryptography.SHA1CryptoServiceProvider();

            while (true)
            {
                foreach (var kvp in _connections.Where(
                    x => x.Key.HandshakeState == PeerHandshakeState.HandshakeAccepted))
                {
                    var peer = kvp.Key;
                    var state = kvp.Value;
                    if (peer.Bitfield != null)
                    {
                        var hasInterest = CheckInterest(peer.Bitfield);
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

                        if (hasInterest)
                        {
                            if (state.IsChoked)
                            {
                                SendUnChoke(peer);
                                state.IsChoked = false;
                            }
                        }

                        if ((state.PieceManager?.ShouldSendRequest ?? true) && state.IsInterested && !state.IsChoked)
                        {
                            var exceptPieces = _connections.Values
                                .Select(x => x.PieceManager?.Piece ?? -1)
                                .Where(x => x >= 0).ToArray();
                            var random = GetRandomInterest(peer.Bitfield, exceptPieces);

                            state.PieceManager ??= new PieceManager(random, RequestMessage.DefaultRequestLength,
                                _torrent.PieceSize);
                            var request = state.PieceManager.GetNextRequest();
                            if (request != null)
                            {
                                state.PieceManager.SendRequest(peer, request);
                            }
                        }
                        else if (state.PieceManager?.IsPieceComplete ?? false)
                        {
                            var torrentPieceHash = _torrent.Pieces.Skip(state.PieceManager.Piece * 20).Take(20).ToArray();
                            var pieceBytes = state.PieceManager.Stream.ReadAllBytes();
                            var pieceHash = sha1.ComputeHash(pieceBytes);
                            if (Utils.SequenceEqual(pieceHash, torrentPieceHash))
                            {
                                WritePiece(_torrent, _downloadDirectory, state.PieceManager.Piece, pieceBytes);
                                var filePieceBytes = GetPiece(_torrent, _downloadDirectory, state.PieceManager.Piece);
                                var fileSha = sha1.ComputeHash(filePieceBytes);
                                if (Utils.SequenceEqual(fileSha, torrentPieceHash))
                                {
                                    SendHave(peer, state.PieceManager.Piece);
                                    _bitfield.Set(state.PieceManager.Piece, true);
                                    state.PieceManager = null;
                                }
                                else
                                {
                                    //error writing to file
                                }
                            }
                        }
                    }
                }

                Thread.Sleep(1);
            }
        }

        void Connection_OnRequest(PeerConnection sender, RequestMessage request)
        {
            Log($"received request {request.Index}:{request.Begin}");
            var piece = GetPiece(_torrent, _downloadDirectory, request.Index);
            var block = new byte[request.RequestedLength];
            Buffer.BlockCopy(piece, request.Begin, block, 0, Math.Min(request.RequestedLength, piece.Length));
            var msg = new PieceMessage(request.Index, request.Begin, block);
            SendPiece(sender, msg);
        }

        void Connection_OnPiece(PeerConnection sender, PieceMessage piece)
        {
            Log($"received piece {piece.Index},{piece.Begin}");
            var state = GetConnectionState(sender);
            state.PieceManager.Write(piece);
        }

        TorrentPeerConnectionState GetConnectionState(PeerConnection conn)
        {
            return _connections.FirstOrDefault(x => x.Key == conn).Value;
        }

        private bool CheckInterest(BitArray bitfield)
        {
            if (_hasAllPieces) return false;
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
            if (_hasAllPieces) return -1;
            if (_bitfield.Length != bitfield.Length) return -1;
            var interest = new List<int>();
            for (var i = 0; i < bitfield.Length; i++)
            {
                if (!_bitfield.Get(i) && bitfield.Get(i))
                {
                    interest.Add(i);
                }
            }

            while (except.Contains(interest[0])) interest.Remove(0);
            var interestArray = interest.ToArray();
            r.Shuffle(interestArray);

            return interestArray[0];
        }


        void SendHandshake(PeerConnection conn)
        {
            Log("sending handshake");
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
            Log($"interested in {conn.PeerId[0]:X}{conn.PeerId[1]:X}");
            var bytes = new InterestedMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendNotInterested(PeerConnection conn)
        {
            var bytes = new NotInterestedMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendChoke(PeerConnection conn)
        {
            var bytes = new ChokeMessage().GetBytes();
            conn.Write(bytes);
        }

        void SendUnChoke(PeerConnection conn)
        {
            var bytes = new UnChokeMessage().GetBytes();
            conn.Write(bytes);
        }

        //void SendRequest(PeerConnection conn, RequestMessage request)
        //{
        //    Log($"sending request for {request.Index}:{request.Begin}");
        //    var bytes = request.GetBytes();
        //    conn.Write(bytes);
        //}

        void SendPiece(PeerConnection conn, PieceMessage piece)
        {
            Log($"sending piece {piece.Index}:{piece.Begin}");
            var bytes = piece.GetBytes();
            conn.Write(bytes);
        }

        void SendHave(PeerConnection conn, int piece)
        {
            var bytes = new HaveMessage(piece).GetBytes();
            conn.Write(bytes);
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
                    using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
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
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
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

        private byte[] GetPiece(Torrent torrent, string downloadDirectory, int piece)
        {
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
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
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
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
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