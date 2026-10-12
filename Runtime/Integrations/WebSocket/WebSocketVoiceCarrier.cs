using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Netcode.Transports.WebSocket;
using WebSocketSharp;
using WebSocketSharp.Server;
using WebSocket = WebSocketSharp.WebSocket;

namespace Lockyaw.VoiceChat {

    internal sealed class WebSocketVoiceCarrier : IVoiceIndependentCarrier, IVoicePacketArrivalNotifier {

        private sealed class Peer {
            internal readonly ulong PeerId;
            internal readonly VoiceConnectionAuthentication Authentication;
            internal WebSocket Socket;
            internal int IsSending;
            internal long SendStartedAt;
            internal bool IsBound;
            internal byte[] PendingBytes;
            internal VoicePacket PendingPacket;
            internal WebSocket PendingSocket;

            internal Peer(ulong peerId, VoiceConnectionAuthentication authentication) {
                PeerId = peerId;
                Authentication = authentication;
            }

            internal void SendPending(object state) {
                try {
                    if (PendingPacket.CanTransmit && Stopwatch.GetTimestamp() - SendStartedAt <= Stopwatch.Frequency / 5) { PendingSocket.Send(PendingBytes); }
                } catch (Exception) {
                    if (PendingSocket != null) { PendingSocket.Close(CloseStatusCode.Abnormal); }
                } finally {
                    PendingBytes = null;
                    PendingSocket = null;
                    Interlocked.Exchange(ref IsSending, 0);
                }
            }
        }

        private readonly struct Received {
            internal readonly WebSocket Socket;
            internal readonly byte[] Bytes;
            internal readonly long ReceivedAt;

            internal Received(WebSocket socket, byte[] bytes) {
                Socket = socket;
                Bytes = bytes;
                ReceivedAt = Stopwatch.GetTimestamp();
            }
        }

        private readonly struct BindingChange {
            internal readonly ulong PeerId;
            internal readonly byte[] Secret;

            internal BindingChange(ulong peerId, byte[] secret) {
                PeerId = peerId;
                Secret = secret;
            }
        }

        public ushort BoundPort { get; }
        public string AdvertisedAddress { get; }
        public VoiceTransportCapability Capability => VoiceTransportCapability.Independent;
        public VoiceTransportState State => state;
        public string FailureReason => failureReason;

        private readonly VoiceCarrierConfiguration configuration;
        private readonly WebSocketServer server;
        private readonly string clientAddress;
        private readonly bool secureConnection;
        private WebSocket client;
        private readonly X509Certificate2 serverCertificate;
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly Queue<BindingChange> bindings = new();
        private readonly Queue<Received> received = new();
        private readonly object bindingSynchronization = new();
        private readonly object receiveSynchronization = new();
        private readonly VoicePacketQueue outgoing = new();
        private readonly VoicePacketQueue incoming = new();
        private readonly byte[] sendBuffer = new byte[VoiceConnectionAuthentication.MAXIMUM_MESSAGE_BYTES];
        private Action arrivalCallback;
        private volatile VoiceTransportState state;
        private volatile string failureReason = string.Empty;
        private volatile bool disposed;
        private long nextHelloAt;
        private long nextReconnectAt;
        private volatile bool needsReconnect;
        private int isConnecting;

        internal WebSocketVoiceCarrier(VoiceCarrierConfiguration configuration) {
            this.configuration = configuration;
            try {
                WebSocketTransport gameTransport = (WebSocketTransport)configuration.NetworkManager.NetworkConfig.NetworkTransport;
                string path = gameTransport.Path.TrimEnd('/') + "/voice";
                AdvertisedAddress = configuration.Options.AdvertisedAddress;
                if (configuration.IsAuthority) {
                    if (!IPAddress.TryParse(configuration.Options.ListenAddress, out IPAddress address)) { throw new ArgumentException("A numeric voice listen address is required."); }
                    server = new WebSocketServer(address, configuration.Options.Port, gameTransport.SecureConnection);
                    server.WaitTime = TimeSpan.FromSeconds(1);
                    server.AllowForwardedRequest = gameTransport.AllowForwardedRequest;
                    server.Log.Output = HandleLog;
                    if (gameTransport.SecureConnection) {
                        byte[] certificate = Convert.FromBase64String(gameTransport.CertificateBase64String);
                        serverCertificate = new X509Certificate2(certificate);
                        server.SslConfiguration.ServerCertificate = serverCertificate;
                        server.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12;
                    }
                    server.AddWebSocketService<WebSocketVoiceBehavior>(path, InitializeBehavior);
                    server.Start();
                    if (!server.IsListening) { throw new InvalidOperationException("The voice WebSocket listener could not start."); }
                    BoundPort = (ushort)server.Port;
                    state = VoiceTransportState.Ready;
                } else {
                    string host = configuration.RemoteAddress.Length == 0 ? gameTransport.ConnectAddress : configuration.RemoteAddress;
                    UriBuilder address = new(gameTransport.SecureConnection ? "wss" : "ws", host, configuration.RemotePort, path);
                    clientAddress = address.Uri.AbsoluteUri;
                    secureConnection = gameTransport.SecureConnection;
                    state = VoiceTransportState.Connecting;
                    BeginConnect();
                }
            } catch {
                Dispose();
                throw;
            }
        }

        public void BindPeer(ulong peerId, byte[] secret) {
            if (secret == null || secret.Length != 32) { throw new ArgumentException("A voice peer secret is required.", nameof(secret)); }
            lock (bindingSynchronization) {
                if (disposed || bindings.Count >= configuration.Options.MaxPeers * 2) { return; }
                bindings.Enqueue(new BindingChange(peerId, (byte[])secret.Clone()));
            }
        }

        public void RemovePeer(ulong peerId) {
            lock (bindingSynchronization) {
                if (!disposed && bindings.Count < configuration.Options.MaxPeers * 2) { bindings.Enqueue(new BindingChange(peerId, null)); }
            }
        }

        public void PumpMainThread() { }

        public void SetArrivalCallback(Action callback) {
            Volatile.Write(ref arrivalCallback, callback);
        }

        public bool TrySend(ulong peerId, VoicePacket packet) {
            if (disposed || !packet.IsValid || !packet.CanTransmit) { return false; }
            outgoing.Enqueue(peerId, packet);
            return true;
        }

        public void Service() {
            if (disposed) { return; }
            ApplyBindings();
            ProcessReceived();
            long currentTime = Stopwatch.GetTimestamp();
            WebSocket currentClient = Volatile.Read(ref client);
            if (!configuration.IsAuthority && needsReconnect && currentTime >= nextReconnectAt && (currentClient == null || currentClient.ReadyState == WebSocketState.Closed)) {
                nextReconnectAt = currentTime + Stopwatch.Frequency * 2;
                if (peers.TryGetValue(configuration.AuthorityPeerId, out Peer reconnectingPeer)) { reconnectingPeer.IsBound = false; }
                needsReconnect = false;
                state = VoiceTransportState.Connecting;
                BeginConnect();
            }
            if (!configuration.IsAuthority && currentClient != null && currentClient.ReadyState == WebSocketState.Open &&
                peers.TryGetValue(configuration.AuthorityPeerId, out Peer authority) && !authority.IsBound && currentTime >= nextHelloAt) {
                nextHelloAt = currentTime + Stopwatch.Frequency / 2;
                authority.Socket = currentClient;
                Send(authority, VoiceConnectionAuthentication.HELLO, default);
            }
            foreach (Peer peer in peers.Values) {
                if (Volatile.Read(ref peer.IsSending) != 0 && currentTime - peer.SendStartedAt > Stopwatch.Frequency / 2 && peer.Socket != null) {
                    peer.Socket.Close(CloseStatusCode.Abnormal);
                    peer.IsBound = false;
                    if (!configuration.IsAuthority) { state = VoiceTransportState.ConnectionLost; }
                }
            }
            for (int i = 0; i < 256 && outgoing.TryDequeue(out ulong peerId, out VoicePacket packet); i++) {
                if (peers.TryGetValue(peerId, out Peer peer) && peer.IsBound) { Send(peer, VoiceConnectionAuthentication.MEDIA, packet); }
            }
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            WebSocket previousClient = Interlocked.Exchange(ref client, null);
            if (previousClient != null) { DetachAndClose(previousClient); }
            if (server != null) { server.Stop(); }
            if (serverCertificate != null) { serverCertificate.Dispose(); }
            foreach (Peer peer in peers.Values) { peer.Authentication.Dispose(); }
            peers.Clear();
            lock (bindingSynchronization) {
                while (bindings.Count > 0) {
                    byte[] secret = bindings.Dequeue().Secret;
                    if (secret != null) { CryptographicOperations.ZeroMemory(secret); }
                }
            }
            lock (receiveSynchronization) { received.Clear(); }
            incoming.Clear();
            outgoing.Clear();
            state = VoiceTransportState.Stopped;
        }

        internal void Enqueue(WebSocket socket, byte[] bytes) {
            if (disposed || bytes == null || bytes.Length > VoiceConnectionAuthentication.MAXIMUM_MESSAGE_BYTES) { return; }
            lock (receiveSynchronization) {
                if (received.Count >= 512) { received.Dequeue(); }
                received.Enqueue(new Received(socket, bytes));
            }
            Action callback = Volatile.Read(ref arrivalCallback);
            if (callback != null) { callback(); }
        }

        internal void SetFailure(string reason) {
            failureReason = reason;
        }

        public bool TryReceive(out ulong peerId, out VoicePacket packet) => incoming.TryDequeue(out peerId, out packet);

        private void BeginConnect() {
            if (Interlocked.CompareExchange(ref isConnecting, 1, 0) == 0) { ThreadPool.QueueUserWorkItem(ConnectClient); }
        }

        private void ConnectClient(object state) {
            WebSocket nextClient = null;
            try {
                if (disposed) { return; }
                nextClient = CreateClient();
                WebSocket previousClient = Interlocked.Exchange(ref client, nextClient);
                if (previousClient != null) { DetachAndClose(previousClient); }
                if (!disposed) { nextClient.Connect(); }
            } catch (Exception exception) {
                failureReason = exception.GetType().Name + ": " + exception.Message;
            } finally {
                if (disposed) {
                    if (nextClient != null) {
                        Interlocked.CompareExchange(ref client, null, nextClient);
                        DetachAndClose(nextClient);
                    }
                } else if (nextClient == null || nextClient.ReadyState != WebSocketState.Open) {
                    this.state = VoiceTransportState.ConnectionLost;
                    needsReconnect = true;
                }
                Interlocked.Exchange(ref isConnecting, 0);
            }
        }

        private void HandleClientMessage(object sender, MessageEventArgs e) {
            if (sender is WebSocket socket && socket == Volatile.Read(ref client) && e.IsBinary) { Enqueue(socket, e.RawData); }
        }

        private void HandleClientError(object sender, ErrorEventArgs e) {
            if (sender == Volatile.Read(ref client)) { failureReason = e.Message; }
        }

        private void HandleClientClosed(object sender, CloseEventArgs e) {
            if (!disposed && sender == Volatile.Read(ref client)) { state = VoiceTransportState.ConnectionLost; needsReconnect = true; }
        }

        private void DetachAndClose(WebSocket socket) {
            socket.OnMessage -= HandleClientMessage;
            socket.OnError -= HandleClientError;
            socket.OnClose -= HandleClientClosed;
            socket.Close(CloseStatusCode.Abnormal);
        }

        private WebSocket CreateClient() {
            WebSocket socket = new(clientAddress);
            socket.WaitTime = TimeSpan.FromSeconds(1);
            socket.Log.Output = HandleLog;
            if (secureConnection) {
                socket.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12;
                socket.SslConfiguration.ServerCertificateValidationCallback = ValidateServerCertificate;
            }
            socket.OnMessage += HandleClientMessage;
            socket.OnError += HandleClientError;
            socket.OnClose += HandleClientClosed;
            return socket;
        }

        private void HandleLog(LogData data, string path) {
            failureReason = data.Message;
        }

        private void InitializeBehavior(WebSocketVoiceBehavior behavior) {
            behavior.Carrier = this;
        }

        private void ApplyBindings() {
            lock (bindingSynchronization) {
                while (bindings.Count > 0) {
                    BindingChange change = bindings.Dequeue();
                    if (peers.TryGetValue(change.PeerId, out Peer previous)) {
                        previous.Authentication.Dispose();
                        if (previous.Socket != null) { previous.Socket.Close(CloseStatusCode.Abnormal); }
                        peers.Remove(change.PeerId);
                    }
                    if (change.Secret == null) { continue; }
                    if (peers.Count < configuration.Options.MaxPeers) {
                        peers.Add(change.PeerId, new Peer(change.PeerId, new VoiceConnectionAuthentication(configuration.Epoch, configuration.LocalPeerId, change.PeerId, change.Secret)));
                    }
                    CryptographicOperations.ZeroMemory(change.Secret);
                }
            }
        }

        private void ProcessReceived() {
            for (int i = 0; i < 512; i++) {
                Received message;
                lock (receiveSynchronization) {
                    if (received.Count == 0) { return; }
                    message = received.Dequeue();
                }
                if (!configuration.IsAuthority && message.Socket != Volatile.Read(ref client)) { continue; }
                if (Stopwatch.GetTimestamp() - message.ReceivedAt > Stopwatch.Frequency / 5 ||
                    !VoiceConnectionAuthentication.TryReadPeerId(message.Bytes, out ulong peerId) || !peers.TryGetValue(peerId, out Peer peer) ||
                    peer.Socket != null && peer.Socket != message.Socket && peer.Socket.ReadyState == WebSocketState.Open ||
                    !peer.Authentication.TryRead(message.Bytes, out byte kind, out VoicePacket packet)) { continue; }
                if (kind == VoiceConnectionAuthentication.HELLO && configuration.IsAuthority) {
                    peer.Socket = message.Socket;
                    peer.IsBound = true;
                    Send(peer, VoiceConnectionAuthentication.ACKNOWLEDGMENT, default);
                } else if (kind == VoiceConnectionAuthentication.ACKNOWLEDGMENT && peerId == configuration.AuthorityPeerId) {
                    peer.IsBound = true;
                    failureReason = string.Empty;
                    state = VoiceTransportState.Ready;
                } else if (kind == VoiceConnectionAuthentication.MEDIA && peer.IsBound) {
                    incoming.Enqueue(peerId, packet);
                }
            }
        }

        private void Send(Peer peer, byte kind, VoicePacket packet) {
            if (peer.Socket == null || peer.Socket.ReadyState != WebSocketState.Open || Interlocked.CompareExchange(ref peer.IsSending, 1, 0) != 0) { return; }
            if (!peer.Authentication.TryWrite(kind, packet, sendBuffer, out int length)) { Interlocked.Exchange(ref peer.IsSending, 0); return; }
            byte[] bytes = new byte[length];
            Buffer.BlockCopy(sendBuffer, 0, bytes, 0, length);
            peer.SendStartedAt = Stopwatch.GetTimestamp();
            peer.PendingBytes = bytes;
            peer.PendingPacket = packet;
            peer.PendingSocket = peer.Socket;
            ThreadPool.QueueUserWorkItem(peer.SendPending);
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) => errors == SslPolicyErrors.None;

    }

}
