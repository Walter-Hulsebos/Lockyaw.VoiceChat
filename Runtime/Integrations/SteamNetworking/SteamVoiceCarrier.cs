using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Steamworks;
#if UNITY_SERVER
using SteamSockets = Steamworks.SteamGameServerNetworkingSockets;
#else
using SteamSockets = Steamworks.SteamNetworkingSockets;
#endif

namespace Lockyaw.VoiceChat {

    internal sealed class SteamVoiceCarrier : IVoiceIndependentCarrier {

        private sealed class Peer {
            internal readonly ulong PeerId;
            internal readonly ulong SteamId;
            internal readonly VoiceConnectionAuthentication Authentication;
            internal HSteamNetConnection Connection;
            internal bool IsBound;

            internal Peer(ulong peerId, ulong steamId, VoiceConnectionAuthentication authentication) {
                PeerId = peerId;
                SteamId = steamId;
                Authentication = authentication;
            }
        }

        private readonly struct BindingChange {
            internal readonly ulong PeerId;
            internal readonly ulong SteamId;
            internal readonly byte[] Secret;

            internal BindingChange(ulong peerId, ulong steamId, byte[] secret) {
                PeerId = peerId;
                SteamId = steamId;
                Secret = secret;
            }
        }

        public ushort BoundPort { get; }
        public string AdvertisedAddress { get; }
        public VoiceTransportCapability Capability => VoiceTransportCapability.Independent;
        public VoiceTransportState State => state;
        public string FailureReason => failureReason;

        private readonly VoiceCarrierConfiguration configuration;
        private readonly HSteamNetPollGroup pollGroup;
        private readonly HSteamListenSocket listenSocket;
        private HSteamNetConnection authorityConnection;
        private readonly Callback<SteamNetConnectionStatusChangedCallback_t> connectionCallback;
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly Dictionary<ulong, Peer> steamPeers = new();
        private readonly Queue<BindingChange> bindings = new();
        private readonly Queue<SteamNetConnectionStatusChangedCallback_t> connectionChanges = new();
        private readonly object bindingSynchronization = new();
        private readonly object connectionSynchronization = new();
        private readonly VoicePacketQueue incoming = new();
        private readonly VoicePacketQueue outgoing = new();
        private readonly byte[] sendBuffer = new byte[VoiceConnectionAuthentication.MAXIMUM_MESSAGE_BYTES];
        private readonly byte[] receiveBuffer = new byte[VoiceConnectionAuthentication.MAXIMUM_MESSAGE_BYTES];
        private readonly IntPtr[] receivedPointers = new IntPtr[32];
        private GCHandle pinnedSendBuffer;
        private volatile VoiceTransportState state;
        private volatile string failureReason = string.Empty;
        private volatile bool disposed;
        private long nextHelloAt;
        private long nextReconnectAt;

        internal SteamVoiceCarrier(VoiceCarrierConfiguration configuration) {
            this.configuration = configuration;
            BoundPort = configuration.IsAuthority ? configuration.Options.SteamVirtualPort : configuration.RemotePort;
            if (BoundPort == 0) { throw new ArgumentException("The voice Steam virtual port must differ from the gameplay port zero."); }
#if UNITY_SERVER
            AdvertisedAddress = SteamGameServer.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
            connectionCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(HandleConnectionChanged);
#else
            AdvertisedAddress = SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
            connectionCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(HandleConnectionChanged);
#endif
            try {
                pollGroup = SteamSockets.CreatePollGroup();
                if (pollGroup == HSteamNetPollGroup.Invalid) { throw new InvalidOperationException("Steam could not create the voice poll group."); }
                if (configuration.IsAuthority) {
                    listenSocket = SteamSockets.CreateListenSocketP2P(BoundPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
                    if (listenSocket == HSteamListenSocket.Invalid) { throw new InvalidOperationException("The Steam voice virtual port could not be opened."); }
                    state = VoiceTransportState.Ready;
                } else {
                    SteamNetworkingIdentity identity = new();
                    identity.SetSteamID(new CSteamID(ulong.Parse(configuration.RemoteAddress, CultureInfo.InvariantCulture)));
                    authorityConnection = SteamSockets.ConnectP2P(ref identity, BoundPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
                    if (authorityConnection == HSteamNetConnection.Invalid || !SteamSockets.SetConnectionPollGroup(authorityConnection, pollGroup)) {
                        throw new InvalidOperationException("Steam could not connect the voice channel.");
                    }
                    state = VoiceTransportState.Connecting;
                }
                pinnedSendBuffer = GCHandle.Alloc(sendBuffer, GCHandleType.Pinned);
            } catch {
                Dispose();
                throw;
            }
        }

        public void BindPeer(ulong peerId, byte[] secret) {
            if (secret == null || secret.Length != 32) { throw new ArgumentException("A voice peer secret is required.", nameof(secret)); }
            ulong steamId = configuration.IsAuthority
                ? configuration.NetworkManager.GetTransportIdFromClientId(peerId)
                : ulong.Parse(configuration.RemoteAddress, CultureInfo.InvariantCulture);
            if (steamId == 0 || steamId == ulong.MaxValue) { throw new InvalidOperationException("The game peer has no authenticated Steam transport identity."); }
            lock (bindingSynchronization) {
                if (!disposed && bindings.Count < configuration.Options.MaxPeers * 2) { bindings.Enqueue(new BindingChange(peerId, steamId, (byte[])secret.Clone())); }
            }
        }

        public void RemovePeer(ulong peerId) {
            lock (bindingSynchronization) {
                if (!disposed && bindings.Count < configuration.Options.MaxPeers * 2) { bindings.Enqueue(new BindingChange(peerId, 0, null)); }
            }
        }

        public void PumpMainThread() { }

        public bool TrySend(ulong peerId, VoicePacket packet) {
            if (disposed || !packet.IsValid || !packet.CanTransmit) { return false; }
            outgoing.Enqueue(peerId, packet);
            return true;
        }

        public void Service() {
            if (disposed) { return; }
            ApplyBindings();
            ProcessConnections();
            Receive();
            if (!configuration.IsAuthority && peers.TryGetValue(configuration.AuthorityPeerId, out Peer reconnectingPeer) &&
                reconnectingPeer.Connection == HSteamNetConnection.Invalid && Stopwatch.GetTimestamp() >= nextReconnectAt) {
                nextReconnectAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                SteamNetworkingIdentity identity = new();
                identity.SetSteamID(new CSteamID(reconnectingPeer.SteamId));
                authorityConnection = SteamSockets.ConnectP2P(ref identity, BoundPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
                if (authorityConnection != HSteamNetConnection.Invalid && SteamSockets.SetConnectionPollGroup(authorityConnection, pollGroup)) {
                    reconnectingPeer.Connection = authorityConnection;
                    state = VoiceTransportState.Connecting;
                }
            }
            if (!configuration.IsAuthority && peers.TryGetValue(configuration.AuthorityPeerId, out Peer authority) &&
                !authority.IsBound && Stopwatch.GetTimestamp() >= nextHelloAt) {
                nextHelloAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                Send(authority, VoiceConnectionAuthentication.HELLO, default);
            }
            for (int i = 0; i < 256 && outgoing.TryDequeue(out ulong peerId, out VoicePacket packet); i++) {
                if (peers.TryGetValue(peerId, out Peer peer) && peer.IsBound) { Send(peer, VoiceConnectionAuthentication.MEDIA, packet); }
            }
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            try {
                if (connectionCallback != null) { connectionCallback.Dispose(); }
                foreach (Peer peer in peers.Values) {
                    peer.Authentication.Dispose();
                    if (peer.Connection != HSteamNetConnection.Invalid) { SteamSockets.CloseConnection(peer.Connection, 0, "Voice session ended", false); }
                }
                if (authorityConnection != HSteamNetConnection.Invalid && !peers.ContainsKey(configuration.AuthorityPeerId)) {
                    SteamSockets.CloseConnection(authorityConnection, 0, "Voice session ended", false);
                }
                if (listenSocket != HSteamListenSocket.Invalid) { SteamSockets.CloseListenSocket(listenSocket); }
                if (pollGroup != HSteamNetPollGroup.Invalid) { SteamSockets.DestroyPollGroup(pollGroup); }
            } catch (Exception exception) {
                failureReason = exception.GetType().Name + ": " + exception.Message;
            } finally {
                if (pinnedSendBuffer.IsAllocated) { pinnedSendBuffer.Free(); }
                peers.Clear();
                steamPeers.Clear();
                lock (bindingSynchronization) {
                    while (bindings.Count > 0) {
                        byte[] secret = bindings.Dequeue().Secret;
                        if (secret != null) { CryptographicOperations.ZeroMemory(secret); }
                    }
                }
                lock (connectionSynchronization) { connectionChanges.Clear(); }
                incoming.Clear();
                outgoing.Clear();
                state = VoiceTransportState.Stopped;
            }
        }

        public bool TryReceive(out ulong peerId, out VoicePacket packet) => incoming.TryDequeue(out peerId, out packet);

        private void HandleConnectionChanged(SteamNetConnectionStatusChangedCallback_t e) {
            if (disposed) { return; }
            bool belongsToVoice = configuration.IsAuthority ? listenSocket != HSteamListenSocket.Invalid && e.m_info.m_hListenSocket == listenSocket : e.m_hConn == authorityConnection;
            if (!belongsToVoice) { return; }
            lock (connectionSynchronization) {
                if (connectionChanges.Count < configuration.Options.MaxPeers * 4) { connectionChanges.Enqueue(e); }
            }
        }

        private void ApplyBindings() {
            lock (bindingSynchronization) {
                while (bindings.Count > 0) {
                    BindingChange change = bindings.Dequeue();
                    if (peers.TryGetValue(change.PeerId, out Peer previous)) {
                        previous.Authentication.Dispose();
                        if (previous.Connection != HSteamNetConnection.Invalid) { SteamSockets.CloseConnection(previous.Connection, 0, "Game peer disconnected", false); }
                        peers.Remove(change.PeerId);
                        steamPeers.Remove(previous.SteamId);
                    }
                    if (change.Secret == null) { continue; }
                    if (peers.Count < configuration.Options.MaxPeers) {
                        Peer peer = new(change.PeerId, change.SteamId, new VoiceConnectionAuthentication(configuration.Epoch, configuration.LocalPeerId, change.PeerId, change.Secret));
                        if (!configuration.IsAuthority) { peer.Connection = authorityConnection; }
                        peers.Add(change.PeerId, peer);
                        steamPeers[change.SteamId] = peer;
                    }
                    CryptographicOperations.ZeroMemory(change.Secret);
                }
            }
        }

        private void ProcessConnections() {
            for (int i = 0; i < configuration.Options.MaxPeers * 4; i++) {
                SteamNetConnectionStatusChangedCallback_t change;
                lock (connectionSynchronization) {
                    if (connectionChanges.Count == 0) { return; }
                    change = connectionChanges.Dequeue();
                }
                ulong steamId = change.m_info.m_identityRemote.GetSteamID64();
                if (!steamPeers.TryGetValue(steamId, out Peer peer)) {
                    SteamSockets.CloseConnection(change.m_hConn, 0, "Game session binding required", false);
                    continue;
                }
                if (change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && configuration.IsAuthority) {
                    if (peer.Connection != HSteamNetConnection.Invalid && peer.Connection != change.m_hConn) {
                        SteamSockets.CloseConnection(change.m_hConn, 0, "Voice peer already connected", false);
                        continue;
                    }
                    if (SteamSockets.AcceptConnection(change.m_hConn) != EResult.k_EResultOK || !SteamSockets.SetConnectionPollGroup(change.m_hConn, pollGroup)) {
                        SteamSockets.CloseConnection(change.m_hConn, 0, "Voice channel acceptance failed", false);
                        continue;
                    }
                    peer.Connection = change.m_hConn;
                } else if (change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                           change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally) {
                    SteamSockets.CloseConnection(change.m_hConn, 0, "Voice channel closed", false);
                    peer.Connection = HSteamNetConnection.Invalid;
                    peer.IsBound = false;
                    if (!configuration.IsAuthority) { state = VoiceTransportState.ConnectionLost; }
                }
            }
        }

        private void Receive() {
            for (int batch = 0; batch < 8; batch++) {
                int count = SteamSockets.ReceiveMessagesOnPollGroup(pollGroup, receivedPointers, receivedPointers.Length);
                if (count <= 0) { return; }
                try {
                    for (int i = 0; i < count; i++) {
                        IntPtr pointer = receivedPointers[i];
                        SteamNetworkingMessage_t message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(pointer);
                        if (message.m_cbSize < VoiceConnectionAuthentication.HEADER_BYTES + VoiceConnectionAuthentication.AUTHENTICATION_BYTES || message.m_cbSize > receiveBuffer.Length ||
                            !steamPeers.TryGetValue(message.m_identityPeer.GetSteamID64(), out Peer peer) || peer.Connection != message.m_conn) { continue; }
                        Marshal.Copy(message.m_pData, receiveBuffer, 0, message.m_cbSize);
                        if (!peer.Authentication.TryRead(receiveBuffer.AsSpan(0, message.m_cbSize), out byte kind, out VoicePacket packet)) { continue; }
                        if (kind == VoiceConnectionAuthentication.HELLO && configuration.IsAuthority) {
                            peer.IsBound = true;
                            Send(peer, VoiceConnectionAuthentication.ACKNOWLEDGMENT, default);
                        } else if (kind == VoiceConnectionAuthentication.ACKNOWLEDGMENT && peer.PeerId == configuration.AuthorityPeerId) {
                            peer.IsBound = true;
                            state = VoiceTransportState.Ready;
                        } else if (kind == VoiceConnectionAuthentication.MEDIA && peer.IsBound) {
                            incoming.Enqueue(peer.PeerId, packet);
                        }
                    }
                } finally {
                    for (int i = 0; i < count; i++) {
                        SteamNetworkingMessage_t.Release(receivedPointers[i]);
                        receivedPointers[i] = IntPtr.Zero;
                    }
                }
            }
        }

        private void Send(Peer peer, byte kind, VoicePacket packet) {
            if (peer.Connection == HSteamNetConnection.Invalid || !peer.Authentication.TryWrite(kind, packet, sendBuffer, out int length)) { return; }
            if (!packet.CanTransmit) { return; }
            EResult result = SteamSockets.SendMessageToConnection(peer.Connection, pinnedSendBuffer.AddrOfPinnedObject(), (uint)length,
                Constants.k_nSteamNetworkingSend_UnreliableNoDelay, out long messageNumber);
            if (result == EResult.k_EResultNoConnection || result == EResult.k_EResultInvalidState) {
                peer.IsBound = false;
                if (!configuration.IsAuthority) { state = VoiceTransportState.ConnectionLost; }
            }
        }

    }

}
