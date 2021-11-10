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
        private byte[] _peerId;
        public byte[] PeerId
        {
            get => _peerId;
            set
            {
                _peerId = value;
                PeerIdString = BitConverter.ToString(value).Replace("-", string.Empty);
            }
        }

        public string PeerIdString { get; private set; }
        public IPEndPoint Endpoint { get; private set; }
        public TcpClient Client { get; private set; }
        public BitArray Bitfield { get; private set; }
        public PeerHandshakeState HandshakeState { get; private set; }
        public PeerConnectionState ConnectionState { get; private set; }

        
        private readonly byte[] _infoHash;
        private readonly Queue<List<byte>> _rxPackets;
        private readonly Queue<byte[]> _txPackets;
        private readonly AutoResetEvent _rxPacketsReady;
        private readonly AutoResetEvent _txPacketsReady;

        private delegate void PacketHandler(byte type, List<byte> data);

        public PeerConnection(TcpClient client, byte[] infoHash)
        {
            _infoHash = infoHash;
            Endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            ConnectionState = new PeerConnectionState();
            Client = client;

            _rxPackets = new Queue<List<byte>>();
            _txPackets = new Queue<byte[]>();
            _rxPacketsReady = new AutoResetEvent(false);
            _txPacketsReady = new AutoResetEvent(false);

            Task.Run(ReadThread);
            Task.Run(WriteThread);
            Task.Run(HandlePacketThread);
            Task.Run(KeepAliveThread);
        }
        
        private void KeepAliveThread()
        {
            while (Client.Connected)
            {
                Thread.Sleep(2*60*1000);
                if (!Client.Connected) return;
                Client.GetStream().Write(new KeepAliveMessage().GetBytes());
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
            while (!Client.Connected)
            {
                Thread.Sleep(100);
            }
            var stream = Client.GetStream();
            var buffer = new List<byte>();
            var byteBuffer = new byte[32 * 1024];
            var spanBuffer = new Span<byte>(byteBuffer);
            int bytesRead = 0;

            while (Client.Connected)
            {
                if (HandshakeState == PeerHandshakeState.HandshakeAccepted)
                {
                    if (ConnectionState.IsChoked)
                    {
                        Thread.Sleep(100);
                    }
                }
                if (stream.DataAvailable)
                {
                    bytesRead = stream.Read(spanBuffer);
                    buffer.AddRange(byteBuffer[..bytesRead]);
                }

                if (!Client.Connected)
                {
                    Die();
                    return;
                }

                while (stream.DataAvailable && HandshakeState == PeerHandshakeState.HandshakeAccepted && buffer.Count >= 4)
                {
                    var ogPacketSize = BitConverter.ToInt32(buffer.ToArray(), 0);
                    var remainingSize = ogPacketSize - buffer.Count;
                    if (remainingSize > 0)
                    {
                        spanBuffer = new Span<byte>(byteBuffer, 0, Math.Min(remainingSize, byteBuffer.Length));
                        bytesRead = stream.Read(spanBuffer);
                        buffer.AddRange(byteBuffer[..bytesRead]);
                    }
                    else
                    {
                        break;
                    }
                }


                while (true)
                {
                    int packetLength = 0;
                    if (buffer.Count >= 4)
                        packetLength = BitConverter.ToInt32(buffer.ToArray(), 0);
                    var handshakeStartLength = HandshakeMessage.StartBytes().Length;
                    var isHandshake = HandshakeState == PeerHandshakeState.PreHandshake &&
                                      buffer.Count >= HandshakeMessage.PacketLength &&
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
                    
                    List<byte> packet =
                        new List<byte>(buffer.GetRange(0, isHandshake ? HandshakeMessage.PacketLength : packetLength + 4));
                    buffer.RemoveRange(0, packet.Count);
                    lock (_rxPackets)
                    {
                        _rxPackets.Enqueue(packet);
                    }

                    _rxPacketsReady.Set();
                }
            }
        }

        private void WriteThread()
        {
            while (true)
            {
                _txPacketsReady.WaitOne();
                while (_txPackets.Count > 0)
                {
                    byte[] packet;
                    lock (_txPackets)
                    {
                        packet = _txPackets.Dequeue();
                    }
                    Client.GetStream().Write(packet);
                }
            }
        }

        public void Write(byte[] bytes)
        {
            lock(_txPackets)
                _txPackets.Enqueue(bytes);
            _txPacketsReady.Set();
        }

        private void HandlePacketThread()
        {
            while (Client.Connected)
            {
                _rxPacketsReady.WaitOne();
                while (_rxPackets.Count > 0)
                {
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
            ConnectionState.IsInterested = true;
        }

        private void HandleNotInterested(byte type, List<byte> data)
        {
            ConnectionState.IsInterested = false;
        }

        public delegate void HaveHandler(PeerConnection sender, int have);

        public event HaveHandler Have;
        private void HandleHave(byte type, List<byte> data)
        {
            var index = BitConverter.ToInt32(data.GetRange(5, 4).ToArray());
            Bitfield.Set(index, true);//should it even do this?
            Have?.Invoke(this, index);
        }

        private void HandleBitfield(byte type, List<byte> data)
        {
            BitArray bitArray = new BitArray(data.GetRange(5, data.Count - 5).ToArray());
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
                Debug.WriteLine($"{PeerId[18]:X2}{PeerId[19]:X2}>{str}");
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