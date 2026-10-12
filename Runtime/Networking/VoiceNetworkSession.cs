using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace Lockyaw.VoiceChat {

    public sealed class VoiceNetworkSession : IDisposable {

        public ulong Epoch => service == null ? 0 : service.Epoch;
        public ulong AuthorityPeerId => authorityPeerId;
        public bool IsAuthority => networkManager != null && (networkManager.DistributedAuthorityMode ? networkManager.IsConnectedClient && networkManager.LocalClientId == authorityPeerId : networkManager.IsServer);
        public bool IsReady => service != null && service.State == VoiceTransportState.Ready;
        public VoiceTransportState State => service == null ? state : service.State;
        public VoiceTransportCapability Capability => service == null ? VoiceTransportCapability.FrameDriven : service.Capability;
        public string FailureReason => service == null ? failureReason : service.FailureReason;
        public VoiceDeliveryMode DeliveryMode => deliveryMode;
        public long RejectedPackets => service == null ? 0 : service.RejectedPackets;

        private const string CONTROL_MESSAGE = "Lockyaw.Voice.Control.1";
        private const byte REQUEST_BINDING = 1;
        private const byte PROVIDE_BINDING = 2;
        private const int CONTROL_HEADER_BYTES = 32;
        private readonly NetworkManager networkManager;
        private readonly VoiceNetworkOptions options;
        private readonly Dictionary<VoiceRouteId, IVoicePacketReceiver> receivers = new();
        private readonly Dictionary<ulong, byte[]> bindingSecrets = new();
        private readonly byte[] controlBuffer = new byte[CONTROL_HEADER_BYTES + 255 + UdpVoicePacketCarrier.SECRET_BYTES];
        private byte[] receiveBuffer = new byte[CONTROL_HEADER_BYTES + 255 + UdpVoicePacketCarrier.SECRET_BYTES];
        private VoiceRouteSnapshot[] routes = Array.Empty<VoiceRouteSnapshot>();
        private CustomMessagingManager messaging;
        private VoiceTransportService service;
        private UdpVoicePacketCarrier datagramCarrier;
        private NgoVoicePacketCarrier gameCarrier;
        private IVoiceIndependentCarrier independentCarrier;
        private VoiceTransportState state = VoiceTransportState.Stopped;
        private VoiceDeliveryMode deliveryMode;
        private ulong authorityPeerId;
        private long nextBindingRequestAt;
        private string failureReason = string.Empty;
        private bool sendingEnabled = true;
        private bool started;
        private bool disposed;

        public VoiceNetworkSession(NetworkManager networkManager, VoiceNetworkOptions options) {
            if (networkManager == null) { throw new ArgumentNullException(nameof(networkManager)); }
            this.networkManager = networkManager;
            VoiceNetworkOptions selectedOptions = options == null ? new VoiceNetworkOptions() : options;
            this.options = new VoiceNetworkOptions {
                DeliveryMode = selectedOptions.DeliveryMode,
                ListenAddress = selectedOptions.ListenAddress,
                AdvertisedAddress = selectedOptions.AdvertisedAddress,
                Port = selectedOptions.Port,
                SteamVirtualPort = selectedOptions.SteamVirtualPort,
                MaxPeers = Math.Max(1, Math.Min(selectedOptions.MaxPeers, 4096))
            };
            VoiceNetworkSerialization.Initialize();
        }

        public void Start() {
            if (started || disposed) { return; }
#if UNITY_WEBGL && !UNITY_EDITOR
            SetUnavailable("Voice requires a browser audio and delivery backend on this platform.");
#else
            started = true;
            state = VoiceTransportState.Connecting;
            networkManager.OnConnectionEvent += HandleConnectionChanged;
            networkManager.OnSessionOwnerPromoted += HandleSessionOwnerChanged;
            Pump();
#endif
        }

        public void Pump() {
            if (!started || disposed || networkManager == null || !networkManager.IsListening || networkManager.CustomMessagingManager == null) { return; }
            if (!networkManager.IsServer && !networkManager.IsConnectedClient) { return; }
            if (messaging == null) {
                messaging = networkManager.CustomMessagingManager;
                messaging.RegisterNamedMessageHandler(CONTROL_MESSAGE, HandleControlReceived);
                authorityPeerId = networkManager.DistributedAuthorityMode ? networkManager.CurrentSessionOwner : NetworkManager.ServerClientId;
                if (IsAuthority) { StartAuthority(); }
            }
            if (gameCarrier != null) { gameCarrier.Pump(); }
            if (independentCarrier != null) { independentCarrier.PumpMainThread(); }
            if (service == null && !IsAuthority && state != VoiceTransportState.Unavailable && Stopwatch.GetTimestamp() >= nextBindingRequestAt) {
                nextBindingRequestAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                controlBuffer[0] = REQUEST_BINDING;
                BinaryPrimitives.WriteUInt16LittleEndian(controlBuffer.AsSpan(1), VoicePacketCodec.PROTOCOL_VERSION);
                SendControl(authorityPeerId, 3);
            }
        }

        public void SetSendingEnabled(bool enabled) {
            Volatile.Write(ref sendingEnabled, enabled);
            VoiceTransportService currentService = Volatile.Read(ref service);
            if (currentService != null) { currentService.SetSendingEnabled(enabled); }
        }

        public void PublishRoutes(VoiceRouteSnapshot[] snapshots) {
            routes = snapshots == null ? Array.Empty<VoiceRouteSnapshot>() : (VoiceRouteSnapshot[])snapshots.Clone();
            if (service != null) { service.PublishRoutes(routes); }
        }

        public void RegisterReceiver(VoiceRouteId routeId, IVoicePacketReceiver receiver) {
            if (receiver == null) { throw new ArgumentNullException(nameof(receiver)); }
            receivers[routeId] = receiver;
            if (service != null) { service.RegisterReceiver(routeId, receiver); }
        }

        public void UnregisterReceiver(VoiceRouteId routeId) {
            receivers.Remove(routeId);
            if (service != null) { service.UnregisterReceiver(routeId); }
        }

        public bool Submit(VoiceRouteId routeId, VoiceFrame frame) {
            VoiceTransportService currentService = Volatile.Read(ref service);
            return currentService != null && currentService.Submit(routeId, frame);
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            if (networkManager != null) {
                networkManager.OnConnectionEvent -= HandleConnectionChanged;
                networkManager.OnSessionOwnerPromoted -= HandleSessionOwnerChanged;
            }
            if (messaging != null) { messaging.UnregisterNamedMessageHandler(CONTROL_MESSAGE); }
            StopService();
            receivers.Clear();
            routes = Array.Empty<VoiceRouteSnapshot>();
            state = VoiceTransportState.Stopped;
        }

        private void HandleConnectionChanged(NetworkManager sender, ConnectionEventData e) {
            if (e.EventType != ConnectionEvent.ClientDisconnected && e.EventType != ConnectionEvent.PeerDisconnected) { return; }
            if (datagramCarrier != null) { datagramCarrier.RemovePeer(e.ClientId); }
            if (independentCarrier != null) { independentCarrier.RemovePeer(e.ClientId); }
            if (bindingSecrets.TryGetValue(e.ClientId, out byte[] secret)) {
                CryptographicOperations.ZeroMemory(secret);
                bindingSecrets.Remove(e.ClientId);
            }
            if (e.ClientId == networkManager.LocalClientId || e.ClientId == authorityPeerId && !IsAuthority) {
                StopService();
                state = VoiceTransportState.ConnectionLost;
            }
        }

        private void HandleSessionOwnerChanged(ulong sessionOwner) {
            StopService();
            authorityPeerId = sessionOwner;
            state = VoiceTransportState.Connecting;
            nextBindingRequestAt = 0;
            if (IsAuthority) { StartAuthority(); }
        }

        private void HandleControlReceived(ulong senderClientId, FastBufferReader reader) {
            int length = reader.Length - reader.Position;
            if (disposed || length < 3 || length > receiveBuffer.Length || !reader.TryBeginRead(length)) { return; }
            reader.ReadBytesSafe(ref receiveBuffer, length);
            ReadOnlySpan<byte> data = receiveBuffer.AsSpan(0, length);
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1)) != VoicePacketCodec.PROTOCOL_VERSION) {
                if (senderClientId == authorityPeerId) { SetUnavailable("The voice protocol version does not match the host."); }
                return;
            }
            if (data[0] == REQUEST_BINDING && IsAuthority && service != null && length == 3) {
                try { ProvideBinding(senderClientId); }
                catch (Exception exception) { SetUnavailable(exception.GetType().Name + ": " + exception.Message); }
                return;
            }
            if (data[0] != PROVIDE_BINDING || senderClientId != authorityPeerId || IsAuthority || length < CONTROL_HEADER_BYTES) { return; }
            ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(3));
            ulong authority = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(11));
            ulong assignedPeer = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(19));
            VoiceDeliveryMode mode = (VoiceDeliveryMode)data[27];
            ushort port = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28));
            int addressLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(30));
            int secretLength = mode != VoiceDeliveryMode.GameTransport ? UdpVoicePacketCarrier.SECRET_BYTES : 0;
            if (epoch == 0 || authority != authorityPeerId || assignedPeer != networkManager.LocalClientId || addressLength > 255 ||
                length != CONTROL_HEADER_BYTES + addressLength + secretLength) { return; }
            if (service != null && service.Epoch == epoch) { return; }
            if (options.DeliveryMode != VoiceDeliveryMode.Automatic && options.DeliveryMode != mode) {
                SetUnavailable("The host selected a different voice delivery mode.");
                return;
            }
            StopService();
            deliveryMode = mode;
            string address = Encoding.UTF8.GetString(receiveBuffer, CONTROL_HEADER_BYTES, addressLength);
            try {
                if (mode == VoiceDeliveryMode.DirectDatagram) {
                    if (!CanUseDirectDatagrams()) { SetUnavailable("Independent UDP requires a direct, unencrypted UnityTransport game connection."); return; }
                    if (address.Length == 0) { address = ((UnityTransport)networkManager.NetworkConfig.NetworkTransport).ConnectionData.Address; }
                    IPAddress remoteAddress = ResolveAddress(address);
                    string listenAddress = remoteAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "::" : "0.0.0.0";
                    datagramCarrier = new UdpVoicePacketCarrier(assignedPeer, authority, epoch, listenAddress, 0, options.MaxPeers);
                    byte[] secret = data.Slice(CONTROL_HEADER_BYTES + addressLength, secretLength).ToArray();
                    datagramCarrier.SetPeerBinding(authority, secret, new IPEndPoint(remoteAddress, port));
                    CryptographicOperations.ZeroMemory(secret);
                    StartService(epoch, datagramCarrier);
                } else if (mode == VoiceDeliveryMode.GameTransport) {
                    gameCarrier = new NgoVoicePacketCarrier(networkManager);
                    StartService(epoch, gameCarrier);
                } else if (VoiceCarrierProviders.TryGet(mode, networkManager, out IVoiceCarrierProvider provider)) {
                    independentCarrier = provider.Create(new VoiceCarrierConfiguration(networkManager, options, authority, epoch, address, port));
                    byte[] secret = data.Slice(CONTROL_HEADER_BYTES + addressLength, secretLength).ToArray();
                    independentCarrier.BindPeer(authority, secret);
                    CryptographicOperations.ZeroMemory(secret);
                    StartService(epoch, independentCarrier);
                } else {
                    SetUnavailable("The independent voice delivery provider is unavailable.");
                }
            } catch (Exception exception) {
                StopService();
                SetUnavailable(exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void StartAuthority() {
            deliveryMode = options.DeliveryMode;
            if (deliveryMode == VoiceDeliveryMode.Automatic) {
                deliveryMode = CanUseDirectDatagrams() ? VoiceDeliveryMode.DirectDatagram : VoiceDeliveryMode.GameTransport;
                if (deliveryMode == VoiceDeliveryMode.GameTransport && VoiceCarrierProviders.TryGet(VoiceDeliveryMode.Automatic, networkManager, out IVoiceCarrierProvider selectedProvider)) {
                    deliveryMode = selectedProvider.Mode;
                }
            }
            try {
                ulong epoch = CreateEpoch();
                if (deliveryMode == VoiceDeliveryMode.DirectDatagram) {
                    if (!CanUseDirectDatagrams()) { SetUnavailable("Independent UDP requires a direct, unencrypted UnityTransport game connection."); return; }
                    datagramCarrier = new UdpVoicePacketCarrier(networkManager.LocalClientId, authorityPeerId, epoch, options.ListenAddress, options.Port, options.MaxPeers);
                    StartService(epoch, datagramCarrier);
                } else if (deliveryMode == VoiceDeliveryMode.GameTransport) {
                    gameCarrier = new NgoVoicePacketCarrier(networkManager);
                    StartService(epoch, gameCarrier);
                } else if (VoiceCarrierProviders.TryGet(deliveryMode, networkManager, out IVoiceCarrierProvider provider)) {
                    independentCarrier = provider.Create(new VoiceCarrierConfiguration(networkManager, options, authorityPeerId, epoch, string.Empty, 0));
                    StartService(epoch, independentCarrier);
                } else {
                    SetUnavailable("The independent voice delivery provider is unavailable. GameTransport supports the configured game connection.");
                }
            } catch (Exception exception) {
                StopService();
                SetUnavailable(exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void StartService(ulong epoch, IVoicePacketCarrier carrier) {
            VoiceTransportService nextService = new(networkManager.LocalClientId, authorityPeerId, epoch, carrier);
            nextService.SetSendingEnabled(Volatile.Read(ref sendingEnabled));
            nextService.PublishRoutes(routes);
            foreach (KeyValuePair<VoiceRouteId, IVoicePacketReceiver> receiver in receivers) { nextService.RegisterReceiver(receiver.Key, receiver.Value); }
            Volatile.Write(ref service, nextService);
            failureReason = string.Empty;
            state = VoiceTransportState.Connecting;
        }

        private void StopService() {
            VoiceTransportService previousService = Interlocked.Exchange(ref service, null);
            if (previousService != null) { previousService.Dispose(); }
            else if (datagramCarrier != null) { datagramCarrier.Dispose(); }
            else if (gameCarrier != null) { gameCarrier.Dispose(); }
            else if (independentCarrier != null) { independentCarrier.Dispose(); }
            datagramCarrier = null;
            gameCarrier = null;
            independentCarrier = null;
            foreach (byte[] secret in bindingSecrets.Values) { CryptographicOperations.ZeroMemory(secret); }
            bindingSecrets.Clear();
        }

        private void ProvideBinding(ulong peerId) {
            if (peerId == networkManager.LocalClientId || !IsConnectedPeer(peerId)) { return; }
            byte[] secret = null;
            if (deliveryMode != VoiceDeliveryMode.GameTransport && !bindingSecrets.TryGetValue(peerId, out secret)) {
                if (bindingSecrets.Count >= options.MaxPeers) { return; }
                secret = new byte[UdpVoicePacketCarrier.SECRET_BYTES];
                RandomNumberGenerator.Fill(secret);
                bindingSecrets.Add(peerId, secret);
                if (datagramCarrier != null) { datagramCarrier.SetPeerBinding(peerId, secret); }
                if (independentCarrier != null) { independentCarrier.BindPeer(peerId, secret); }
            }
            string address = independentCarrier != null ? independentCarrier.AdvertisedAddress : options.AdvertisedAddress;
            if (address == null || deliveryMode == VoiceDeliveryMode.GameTransport) { address = string.Empty; }
            byte[] encodedAddress = Encoding.UTF8.GetBytes(address);
            if (encodedAddress.Length > 255) { SetUnavailable("The advertised voice address is too long."); return; }
            controlBuffer[0] = PROVIDE_BINDING;
            BinaryPrimitives.WriteUInt16LittleEndian(controlBuffer.AsSpan(1), VoicePacketCodec.PROTOCOL_VERSION);
            BinaryPrimitives.WriteUInt64LittleEndian(controlBuffer.AsSpan(3), service.Epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(controlBuffer.AsSpan(11), authorityPeerId);
            BinaryPrimitives.WriteUInt64LittleEndian(controlBuffer.AsSpan(19), peerId);
            controlBuffer[27] = (byte)deliveryMode;
            BinaryPrimitives.WriteUInt16LittleEndian(controlBuffer.AsSpan(28), datagramCarrier != null ? datagramCarrier.BoundPort : independentCarrier != null ? independentCarrier.BoundPort : (ushort)0);
            BinaryPrimitives.WriteUInt16LittleEndian(controlBuffer.AsSpan(30), (ushort)encodedAddress.Length);
            encodedAddress.CopyTo(controlBuffer, CONTROL_HEADER_BYTES);
            int length = CONTROL_HEADER_BYTES + encodedAddress.Length;
            if (secret != null) { secret.CopyTo(controlBuffer, length); length += secret.Length; }
            SendControl(peerId, length);
            if (secret != null) { Array.Clear(controlBuffer, CONTROL_HEADER_BYTES + encodedAddress.Length, secret.Length); }
        }

        private void SendControl(ulong peerId, int length) {
            using (FastBufferWriter writer = new(length, Allocator.Temp)) {
                writer.WriteBytesSafe(controlBuffer, length);
                messaging.SendNamedMessage(CONTROL_MESSAGE, peerId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private void SetUnavailable(string reason) {
            StopService();
            state = VoiceTransportState.Unavailable;
            failureReason = reason;
        }

        private bool CanUseDirectDatagrams() {
            return networkManager.NetworkConfig.NetworkTransport is UnityTransport transport &&
                   transport.Protocol == UnityTransport.ProtocolType.UnityTransport && !transport.UseWebSockets && !transport.UseEncryption &&
                   !networkManager.CMBServiceConnection;
        }

        private bool IsConnectedPeer(ulong peerId) {
            IReadOnlyList<ulong> connectedPeers = networkManager.ConnectedClientsIds;
            for (int i = 0; i < connectedPeers.Count; i++) {
                if (connectedPeers[i] == peerId) { return true; }
            }
            return false;
        }

        private static ulong CreateEpoch() {
            Span<byte> bytes = stackalloc byte[8];
            ulong epoch;
            do {
                RandomNumberGenerator.Fill(bytes);
                epoch = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            } while (epoch == 0);
            return epoch;
        }

        private static IPAddress ResolveAddress(string address) {
            if (IPAddress.TryParse(address, out IPAddress parsed)) { return parsed; }
            IPAddress[] addresses = Dns.GetHostAddresses(address);
            if (addresses.Length == 0) { throw new ArgumentException("The voice host address could not be resolved.", nameof(address)); }
            for (int i = 0; i < addresses.Length; i++) {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork) { return addresses[i]; }
            }
            return addresses[0];
        }

    }

}
