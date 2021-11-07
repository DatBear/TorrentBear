using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TorrentBear.Data.Message.Peer;
using TorrentBear.Enum;
using static TorrentBear.Service.Utils;

namespace TorrentBear.Service
{
    //todo separate reader + handler
    public class PeerConnection
    {
        public volatile byte[] PeerId;
        public IPEndPoint Endpoint { get; set; }
        public TcpClient Client { get; set; }
        public BitArray Bitfield { get; set; }
        public PeerHandshakeState HandshakeState { get; set; }
        public PeerConnectionState ConnectionState { get; set; }
        

        private byte[] _infoHash;
        private Queue<List<byte>> _rxPackets;
        private Queue<byte[]> _txPackets;
        private AutoResetEvent _packetsReady;

        private delegate void PacketHandler(byte type, List<byte> data);

        public PeerConnection(TcpClient client, byte[] infoHash)
        {
            _infoHash = infoHash;
            Endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            ConnectionState = new PeerConnectionState();
            Client = client;

            _rxPackets = new Queue<List<byte>>();
            _txPackets = new Queue<byte[]>();
            _packetsReady = new AutoResetEvent(false);

            Task.Run(ReadThread);
            Task.Run(WriteThread);
            Task.Run(HandlePacketThread);
            Task.Run(KeepAliveThread);
        }

        public void Start()
        {
            
        }

        private void KeepAliveThread()
        {
            while (Client.Connected)
            {
                Thread.Sleep(2*60*1000);
                if (!Client.Connected) return;
                Client.GetStream().WriteAsync(new KeepAliveMessage().GetBytes());
            }
        }

        private void Die()
        {
            if (Client.Connected)
            {
                Client.Close();
            }

            HandshakeState = PeerHandshakeState.Disconnected;
        }

        private void ReadThread()
        {
            var stream = Client.GetStream();
            var buffer = new List<byte>();
            var byteBuffer = new byte[4 * 1024];
            int bytesRead = 0;
            while (Client.Connected)
            {
                if (stream.DataAvailable)
                {
                    bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length);
                    buffer.AddRange(byteBuffer.Take(bytesRead));
                }
                else
                {
                    if (!Client.Connected)
                    {
                        Die();
                        return;
                    }

                    Thread.Sleep(100);
                }

                while (stream.DataAvailable)
                {
                    buffer.Add((byte)stream.ReadByte());
                }

                while (true)
                {
                    int packetLength = 0;
                    if (buffer.Count >= 4)
                        packetLength = BitConverter.ToInt32(buffer.ToArray(), 0);
                    var handshakeStartLength = HandshakeMessage.StartBytes().Length;
                    var isHandshake = HandshakeState == PeerHandshakeState.PreHandshake &&
                                      buffer.Count >= HandshakeMessage.Length &&
                                      HandshakeMessage.StartBytes()
                                          .SequenceEqual(buffer.Take(handshakeStartLength).ToArray());
                    var isKeepAlive = buffer.Count >= 4 && packetLength == 0;

                    if (isKeepAlive)
                    {
                        buffer.RemoveRange(0, 4);
                        continue;
                    }
                    if ((!isHandshake && packetLength + 4 > buffer.Count) || buffer.Count < 5)
                    {
                        break;
                    }

                    //Log("received packet");
                    List<byte> packet =
                        new List<byte>(buffer.GetRange(0, isHandshake ? HandshakeMessage.Length : packetLength + 4));
                    buffer.RemoveRange(0, packet.Count);
                    lock (_rxPackets)
                    {
                        _rxPackets.Enqueue(packet);
                    }

                    _packetsReady.Set();
                }
            }
        }

        private void WriteThread()
        {
            while (true)
            {
                while (_txPackets.Count > 0)
                {
                    var packet = _txPackets.Dequeue();
                    Client.GetStream().Write(packet);
                }
                Thread.Sleep(10);
            }
        }

        public void Write(byte[] bytes)
        {
            _txPackets.Enqueue(bytes);
        }

        private void HandlePacketThread()
        {
            while (Client.Connected)
            {
                _packetsReady.WaitOne();
                List<byte> packet;
                lock (_rxPackets)
                {
                    packet = _rxPackets.Dequeue();
                }

                byte type = HandshakeState == PeerHandshakeState.PreHandshake && packet[0] == 19
                    ? (byte)PeerMessageType.Handshake
                    : packet[4];
                DispatchPacket(type)(type, packet);
            }
        }

        private PacketHandler DispatchPacket(byte type)
        {
            switch ((PeerMessageType)type)
            {
                case PeerMessageType.Handshake:
                    return HandleHandshakeRequest;
                case PeerMessageType.Choke:
                    return HandleChoke;
                case PeerMessageType.Unchoke:
                    return HandleUnChoke;
                case PeerMessageType.Interested:
                    return HandleInterested;
                case PeerMessageType.NotInterested:
                    return HandleNotInterested;
                case PeerMessageType.Have:
                    return HandleHave;
                case PeerMessageType.Bitfield:
                    return HandleBitfield;
                case PeerMessageType.Request:
                    return HandleRequest;
                case PeerMessageType.Piece:
                    return HandlePiece;
                case PeerMessageType.Cancel:
                    return HandleCancel;
                default:
                    return HandleUnknownRequest;
            }
        }

        #region Handlers

        private void HandleHandshakeRequest(byte type, List<byte> data)
        {
            Log("received handshake");
            var hash = data.GetRange(HandshakeMessage.StartBytes().Length, 20);
            var peerId = data.GetRange(HandshakeMessage.StartBytes().Length + 20, 20);
            if (SequenceEqual(hash.ToArray(), _infoHash))
            {
                PeerId = peerId.ToArray();
                HandshakeState = PeerHandshakeState.HandshakeAccepted;
                Log("accepted handshake");
                return;
            }

            HandshakeState = PeerHandshakeState.HandshakeRejected;
        }

        private void HandleChoke(byte type, List<byte> data)
        {
            ConnectionState.IsChoked = true;
        }

        private void HandleUnChoke(byte type, List<byte> data)
        {
            ConnectionState.IsChoked = false;
        }

        private void HandleInterested(byte type, List<byte> data)
        {
            //Log("interested");
            ConnectionState.IsInterested = true;
        }

        private void HandleNotInterested(byte type, List<byte> data)
        {
            ConnectionState.IsInterested = false;
        }

        private void HandleHave(byte type, List<byte> data)
        {
            var index = BitConverter.ToInt32(data.GetRange(5, 4).ToArray());
            Bitfield.Set(index, true);//should it even do this?
        }

        private void HandleBitfield(byte type, List<byte> data)
        {
            BitArray bitArray = new BitArray(data.GetRange(4, data.Count - 5).ToArray());
            Bitfield = bitArray;
        }

        public delegate void RequestHandler(PeerConnection sender, RequestMessage request);

        public event RequestHandler Request;
        private void HandleRequest(byte type, List<byte> data)
        {
            var index = BitConverter.ToInt32(data.ToArray(), 5);
            var begin = BitConverter.ToInt32(data.ToArray(), 9);
            Request?.Invoke(this, new RequestMessage(index, begin));
        }

        public delegate void PieceHandler(PeerConnection sender, PieceMessage piece);

        public event PieceHandler Piece;

        private void HandlePiece(byte type, List<byte> data)
        {
            var index = BitConverter.ToInt32(data.ToArray(), 5);
            var begin = BitConverter.ToInt32(data.ToArray(), 9);
            var block = data.Skip(13).Take(data.Count - 13).ToArray();
            Piece?.Invoke(this, new PieceMessage(index, begin, block));
        }

        private void HandleCancel(byte type, List<byte> data)
        {
            throw new NotImplementedException();
        }

        private void HandleUnknownRequest(byte type, List<byte> data)
        {
            Log("UNKNOWN REQUEST");
        }

        #endregion

        void Log(string str)
        {
            if (PeerId != null)
            {
                Debug.WriteLine($"{PeerId[0]:X2}{PeerId[1]:X2}>{str}");
            }
            else
            {
                Debug.WriteLine($"0000!>{str}");
            }
        }


        protected bool Equals(PeerConnection other)
        {
            return Equals(Endpoint, other.Endpoint);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((PeerConnection)obj);
        }

        public override int GetHashCode()
        {
            return (Endpoint != null ? Endpoint.GetHashCode() : 0);
        }
    }
}