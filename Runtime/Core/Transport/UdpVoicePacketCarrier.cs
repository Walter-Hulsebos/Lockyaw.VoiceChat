using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace Lockyaw.VoiceChat {

    public sealed class UdpVoicePacketCarrier : IVoicePacketCarrier, IVoicePacketArrivalNotifier {

        private sealed class Peer {
            internal readonly ulong PeerId;
            internal readonly HMACSHA256 Authentication;
            internal IPEndPoint Endpoint;
            internal ulong SendSequence;
            internal ulong ReceiveSequence;
            internal ulong ReceivedSequences;
            internal long LastReceivedAt;

            internal Peer(ulong peerId, byte[] secret, IPEndPoint endpoint) {
                PeerId = peerId;
                Authentication = new HMACSHA256(secret);
                Endpoint = endpoint;
            }
        }

        private readonly struct PeerChange {
            internal readonly ulong PeerId;
            internal readonly byte[] Secret;
            internal readonly IPEndPoint Endpoint;

            internal PeerChange(ulong peerId, byte[] secret, IPEndPoint endpoint) {
                PeerId = peerId;
                Secret = secret;
                Endpoint = endpoint;
            }
        }

        public const int SECRET_BYTES = 32;
        public VoiceTransportCapability Capability => VoiceTransportCapability.Independent;
        public VoiceTransportState State => state;
        public string FailureReason => failureReason;
        public ushort BoundPort { get; }

        // Magic, version, kind, epoch, peer and sequence. The payload length is the remainder of the datagram.
        internal const int HEADER_BYTES = 30;
        // HMAC-SHA256 truncated to 128 bits.
        internal const int AUTHENTICATION_BYTES = 16;
        internal const uint MAGIC = 0x4455594C;
        private const int MAXIMUM_DATAGRAM_BYTES = HEADER_BYTES + VoicePacketCodec.MAXIMUM_PACKET_BYTES + AUTHENTICATION_BYTES;
        private const int RECEIVED_DATAGRAM_CAPACITY = 256;
        private const int RECEIVE_POLL_MICROSECONDS = 100000;
        private const byte HELLO = 1;
        private const byte ACKNOWLEDGMENT = 2;
        private const byte MEDIA = 3;

        private readonly Socket socket;
        private readonly ulong localPeerId;
        private readonly ulong authorityPeerId;
        private readonly ulong epoch;
        private readonly int maximumPeers;
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly Queue<PeerChange> changes = new();
        private readonly object changeSynchronization = new();
        private readonly VoicePacketQueue outgoing = new();
        private readonly VoicePacketQueue incoming = new();
        private readonly byte[] sendBuffer = new byte[MAXIMUM_DATAGRAM_BYTES];
        private readonly byte[] datagramBuffer = new byte[MAXIMUM_DATAGRAM_BYTES];
        private readonly byte[] hash = new byte[32];
        private readonly byte[] socketBuffer = new byte[MAXIMUM_DATAGRAM_BYTES + 1];
        private readonly byte[][] receivedDatagrams = new byte[RECEIVED_DATAGRAM_CAPACITY][];
        private readonly int[] receivedLengths = new int[RECEIVED_DATAGRAM_CAPACITY];
        private readonly IPEndPoint[] receivedEndpoints = new IPEndPoint[RECEIVED_DATAGRAM_CAPACITY];
        private readonly object receiveSynchronization = new();
        private readonly Thread receiver;
        private Action arrivalCallback;
        private volatile VoiceTransportState state;
        private volatile string failureReason = string.Empty;
        private volatile bool disposed;
        private long nextHelloAt;
        private int receivedRead;
        private int receivedCount;

        public UdpVoicePacketCarrier(ulong localPeerId, ulong authorityPeerId, ulong epoch, string listenAddress, ushort port, int maximumPeers = 128) {
            if (!IPAddress.TryParse(listenAddress, out IPAddress address)) { throw new ArgumentException("A numeric listen address is required.", nameof(listenAddress)); }
            if (epoch == 0) { throw new ArgumentOutOfRangeException(nameof(epoch)); }
            this.localPeerId = localPeerId;
            this.authorityPeerId = authorityPeerId;
            this.epoch = epoch;
            this.maximumPeers = Math.Max(1, Math.Min(maximumPeers, 4096));
            socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try {
                socket.Blocking = false;
                socket.ReceiveBufferSize = 256 * 1024;
                socket.SendBufferSize = 256 * 1024;
                if (address.AddressFamily == AddressFamily.InterNetworkV6) { socket.DualMode = true; }
                socket.Bind(new IPEndPoint(address, port));
                BoundPort = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
                state = localPeerId == authorityPeerId ? VoiceTransportState.Ready : VoiceTransportState.Connecting;
            } catch {
                socket.Dispose();
                throw;
            }
            receiver = new Thread(ReceiveDatagrams) { IsBackground = true, Name = "Lockyaw voice receive", Priority = ThreadPriority.Highest };
            receiver.Start();
        }

        public void SetArrivalCallback(Action callback) {
            Volatile.Write(ref arrivalCallback, callback);
        }

        public void SetPeerBinding(ulong peerId, byte[] secret, IPEndPoint endpoint = null) {
            if (secret == null || secret.Length != SECRET_BYTES) { throw new ArgumentException("A 32-byte session binding secret is required.", nameof(secret)); }
            if (peerId == localPeerId) { return; }
            byte[] secretCopy = (byte[])secret.Clone();
            IPEndPoint endpointCopy = endpoint == null ? null : new IPEndPoint(endpoint.Address, endpoint.Port);
            lock (changeSynchronization) {
                if (disposed || changes.Count >= maximumPeers * 2) { CryptographicOperations.ZeroMemory(secretCopy); return; }
                changes.Enqueue(new PeerChange(peerId, secretCopy, endpointCopy));
            }
        }

        public void RemovePeer(ulong peerId) {
            lock (changeSynchronization) {
                if (disposed || changes.Count >= maximumPeers * 2) { return; }
                changes.Enqueue(new PeerChange(peerId, null, null));
            }
        }

        public bool TrySend(ulong peerId, VoicePacket packet) {
            if (disposed || packet.Epoch != epoch || !packet.IsValid || !packet.CanTransmit) { return false; }
            outgoing.Enqueue(peerId, packet);
            return true;
        }

        public void Service() {
            if (disposed) { return; }
            ApplyChanges();
            Receive();
            long currentTime = Stopwatch.GetTimestamp();
            if (localPeerId != authorityPeerId && peers.TryGetValue(authorityPeerId, out Peer authority)) {
                if (currentTime >= nextHelloAt) {
                    nextHelloAt = currentTime + Stopwatch.Frequency / 2;
                    Send(authority, HELLO, default);
                }
                if (authority.LastReceivedAt != 0 && currentTime - authority.LastReceivedAt > Stopwatch.Frequency * 10) {
                    state = VoiceTransportState.ConnectionLost;
                }
            }
            for (int i = 0; i < 256 && outgoing.TryDequeue(out ulong peerId, out VoicePacket packet); i++) {
                if (peers.TryGetValue(peerId, out Peer peer) && peer.Endpoint != null) { Send(peer, MEDIA, packet); }
            }
        }

        public void Dispose() {
            lock (changeSynchronization) {
                if (disposed) { return; }
                disposed = true;
                while (changes.Count > 0) {
                    PeerChange change = changes.Dequeue();
                    if (change.Secret != null) { CryptographicOperations.ZeroMemory(change.Secret); }
                }
            }
            socket.Dispose();
            if (Thread.CurrentThread != receiver) { receiver.Join(1000); }
            foreach (Peer peer in peers.Values) { peer.Authentication.Dispose(); }
            peers.Clear();
            lock (receiveSynchronization) { receivedCount = 0; }
            incoming.Clear();
            outgoing.Clear();
            state = VoiceTransportState.Stopped;
        }

        public bool TryReceive(out ulong peerId, out VoicePacket packet) => incoming.TryDequeue(out peerId, out packet);

        private void ApplyChanges() {
            lock (changeSynchronization) {
                while (changes.Count > 0) {
                    PeerChange change = changes.Dequeue();
                    if (peers.TryGetValue(change.PeerId, out Peer previous)) {
                        previous.Authentication.Dispose();
                        peers.Remove(change.PeerId);
                    }
                    if (change.Secret == null) { continue; }
                    if (peers.Count < maximumPeers) { peers.Add(change.PeerId, new Peer(change.PeerId, change.Secret, change.Endpoint)); }
                    CryptographicOperations.ZeroMemory(change.Secret);
                }
            }
        }

        // Blocks on the socket so datagrams are taken as soon as they arrive, then wakes the delivery service.
        private void ReceiveDatagrams() {
            EndPoint anyEndpoint = socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
            while (!disposed) {
                bool received = false;
                try {
                    if (!socket.Poll(RECEIVE_POLL_MICROSECONDS, SelectMode.SelectRead)) { continue; }
                    do {
                        EndPoint remoteEndpoint = anyEndpoint;
                        int receivedBytes;
                        try {
                            receivedBytes = socket.ReceiveFrom(socketBuffer, 0, socketBuffer.Length, SocketFlags.None, ref remoteEndpoint);
                        } catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize || exception.SocketErrorCode == SocketError.ConnectionReset) {
                            continue;
                        } catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock) {
                            break;
                        }
                        if (receivedBytes > MAXIMUM_DATAGRAM_BYTES) { continue; }
                        StoreDatagram(receivedBytes, (IPEndPoint)remoteEndpoint);
                        received = true;
                    } while (!disposed && socket.Available > 0);
                } catch (ObjectDisposedException) {
                    return;
                } catch (SocketException exception) {
                    if (disposed) { return; }
                    failureReason = exception.SocketErrorCode.ToString();
                    Thread.Sleep(10);
                }
                if (received) {
                    Action callback = Volatile.Read(ref arrivalCallback);
                    if (callback != null) { callback(); }
                }
            }
        }

        private void StoreDatagram(int length, IPEndPoint endpoint) {
            lock (receiveSynchronization) {
                if (receivedCount == RECEIVED_DATAGRAM_CAPACITY) {
                    receivedRead = (receivedRead + 1) % RECEIVED_DATAGRAM_CAPACITY;
                    receivedCount--;
                }
                int index = (receivedRead + receivedCount) % RECEIVED_DATAGRAM_CAPACITY;
                byte[] slot = receivedDatagrams[index] ??= new byte[MAXIMUM_DATAGRAM_BYTES];
                Buffer.BlockCopy(socketBuffer, 0, slot, 0, length);
                receivedLengths[index] = length;
                receivedEndpoints[index] = endpoint;
                receivedCount++;
            }
        }

        private bool TryTakeDatagram(out int length, out IPEndPoint endpoint) {
            lock (receiveSynchronization) {
                if (receivedCount == 0) {
                    length = 0;
                    endpoint = null;
                    return false;
                }
                int index = receivedRead;
                length = receivedLengths[index];
                endpoint = receivedEndpoints[index];
                Buffer.BlockCopy(receivedDatagrams[index], 0, datagramBuffer, 0, length);
                receivedEndpoints[index] = null;
                receivedRead = (receivedRead + 1) % RECEIVED_DATAGRAM_CAPACITY;
                receivedCount--;
                return true;
            }
        }

        private void Receive() {
            for (int i = 0; i < RECEIVED_DATAGRAM_CAPACITY && TryTakeDatagram(out int receivedBytes, out IPEndPoint receivedEndpoint); i++) {
                if (receivedBytes < HEADER_BYTES + AUTHENTICATION_BYTES) { continue; }
                ReadOnlySpan<byte> datagram = datagramBuffer.AsSpan(0, receivedBytes);
                if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != MAGIC || datagram[4] != VoicePacketCodec.PROTOCOL_VERSION ||
                    BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(6)) != epoch) { continue; }
                byte kind = datagram[5];
                if (kind < HELLO || kind > MEDIA) { continue; }
                ulong peerId = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(14));
                if (!peers.TryGetValue(peerId, out Peer peer)) { continue; }
                int signedLength = receivedBytes - AUTHENTICATION_BYTES;
                int payloadLength = signedLength - HEADER_BYTES;
                if (kind != MEDIA && payloadLength != 0) { continue; }
                if (!peer.Authentication.TryComputeHash(datagram.Slice(0, signedLength), hash, out int hashLength) || hashLength != hash.Length ||
                    !CryptographicOperations.FixedTimeEquals(hash.AsSpan(0, AUTHENTICATION_BYTES), datagram.Slice(signedLength))) { continue; }
                ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(22));
                if (peer.Endpoint == null) {
                    if (kind != HELLO || localPeerId != authorityPeerId) { continue; }
                } else if (!peer.Endpoint.Equals(receivedEndpoint) &&
                           (kind != HELLO || localPeerId != authorityPeerId || sequence <= peer.ReceiveSequence)) { continue; }
                if (!AcceptSequence(peer, sequence)) { continue; }
                peer.Endpoint = receivedEndpoint;
                peer.LastReceivedAt = Stopwatch.GetTimestamp();
                if (kind == HELLO) { Send(peer, ACKNOWLEDGMENT, default); }
                else if (kind == ACKNOWLEDGMENT && peerId == authorityPeerId) { state = VoiceTransportState.Ready; }
                else if (kind == MEDIA && VoicePacketCodec.TryRead(datagram.Slice(HEADER_BYTES, payloadLength), out VoicePacket packet) && packet.Epoch == epoch) {
                    incoming.Enqueue(peerId, packet);
                }
            }
        }

        private void Send(Peer peer, byte kind, VoicePacket packet) {
            if (peer.Endpoint == null) { return; }
            int payloadLength = 0;
            if (kind == MEDIA && !VoicePacketCodec.TryWrite(packet, sendBuffer.AsSpan(HEADER_BYTES), out payloadLength)) { return; }
            Span<byte> datagram = sendBuffer;
            BinaryPrimitives.WriteUInt32LittleEndian(datagram, MAGIC);
            datagram[4] = (byte)VoicePacketCodec.PROTOCOL_VERSION;
            datagram[5] = kind;
            BinaryPrimitives.WriteUInt64LittleEndian(datagram.Slice(6), epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(datagram.Slice(14), localPeerId);
            BinaryPrimitives.WriteUInt64LittleEndian(datagram.Slice(22), ++peer.SendSequence);
            int signedLength = HEADER_BYTES + payloadLength;
            if (!peer.Authentication.TryComputeHash(datagram.Slice(0, signedLength), hash, out int hashLength) || hashLength != hash.Length) { return; }
            hash.AsSpan(0, AUTHENTICATION_BYTES).CopyTo(datagram.Slice(signedLength));
            if (!packet.CanTransmit) { return; }
            try {
                socket.SendTo(sendBuffer, 0, signedLength + AUTHENTICATION_BYTES, SocketFlags.None, peer.Endpoint);
            } catch (SocketException exception) {
                if (exception.SocketErrorCode == SocketError.WouldBlock || exception.SocketErrorCode == SocketError.NoBufferSpaceAvailable) { return; }
                failureReason = exception.SocketErrorCode.ToString();
            }
        }

        private static bool AcceptSequence(Peer peer, ulong sequence) {
            if (sequence == 0) { return false; }
            if (sequence > peer.ReceiveSequence) {
                ulong advance = sequence - peer.ReceiveSequence;
                peer.ReceivedSequences = advance >= 64 ? 1UL : peer.ReceivedSequences << (int)advance | 1UL;
                peer.ReceiveSequence = sequence;
                return true;
            }
            ulong age = peer.ReceiveSequence - sequence;
            if (age >= 64) { return false; }
            ulong bit = 1UL << (int)age;
            if ((peer.ReceivedSequences & bit) != 0) { return false; }
            peer.ReceivedSequences |= bit;
            return true;
        }

    }

}
