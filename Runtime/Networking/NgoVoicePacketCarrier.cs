using System;
using System.Threading;
using Unity.Collections;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    internal sealed class NgoVoicePacketCarrier : IVoicePacketCarrier, IVoicePacketArrivalNotifier {

        public VoiceTransportCapability Capability => VoiceTransportCapability.FrameDriven;
        public VoiceTransportState State => disposed ? VoiceTransportState.Stopped : VoiceTransportState.Ready;
        public string FailureReason => string.Empty;

        private const string MESSAGE_NAME = "Lockyaw.Voice.Media.1";
        private readonly NetworkManager networkManager;
        private readonly CustomMessagingManager messaging;
        private readonly SynchronizationContext mainThreadContext;
        private readonly int mainThreadId;
        private readonly VoicePacketQueue incoming = new(1024);
        private readonly VoicePacketQueue outgoing = new(1024);
        private readonly byte[] sendBuffer = new byte[VoicePacketCodec.MAXIMUM_PACKET_BYTES];
        private byte[] receiveBuffer = new byte[VoicePacketCodec.MAXIMUM_PACKET_BYTES];
        private Action arrivalCallback;
        private volatile bool disposed;

        internal NgoVoicePacketCarrier(NetworkManager networkManager) {
            this.networkManager = networkManager;
            messaging = networkManager.CustomMessagingManager;
            mainThreadContext = SynchronizationContext.Current;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            messaging.RegisterNamedMessageHandler(MESSAGE_NAME, HandleMessageReceived);
        }

        internal void Pump() {
            if (disposed || networkManager == null || !networkManager.IsListening) { return; }
            for (int i = 0; i < 1024 && outgoing.TryDequeue(out ulong peerId, out VoicePacket packet); i++) {
                if (!VoicePacketCodec.TryWrite(packet, sendBuffer, out int writtenBytes)) { continue; }
                using (FastBufferWriter writer = new(writtenBytes, Allocator.Temp)) {
                    writer.WriteBytesSafe(sendBuffer, writtenBytes);
                    if (!packet.CanTransmit) { continue; }
                    messaging.SendNamedMessage(MESSAGE_NAME, peerId, writer, NetworkDelivery.Unreliable);
                }
            }
        }

        public bool TrySend(ulong peerId, VoicePacket packet) {
            if (disposed || !packet.IsValid || !packet.CanTransmit) { return false; }
            outgoing.Enqueue(peerId, packet);
            return true;
        }

        public void Service() { }

        public void SetArrivalCallback(Action callback) {
            Volatile.Write(ref arrivalCallback, callback);
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId || mainThreadContext == null) { messaging.UnregisterNamedMessageHandler(MESSAGE_NAME); }
            else { mainThreadContext.Post(HandleDisposalRequested, null); }
            incoming.Clear();
            outgoing.Clear();
        }

        public bool TryReceive(out ulong peerId, out VoicePacket packet) => incoming.TryDequeue(out peerId, out packet);

        private void HandleDisposalRequested(object state) {
            messaging.UnregisterNamedMessageHandler(MESSAGE_NAME);
        }

        private void HandleMessageReceived(ulong senderClientId, FastBufferReader reader) {
            int length = reader.Length - reader.Position;
            if (disposed || length < VoicePacketCodec.MINIMUM_HEADER_BYTES || length > receiveBuffer.Length || !reader.TryBeginRead(length)) { return; }
            reader.ReadBytesSafe(ref receiveBuffer, length);
            if (!VoicePacketCodec.TryRead(receiveBuffer.AsSpan(0, length), out VoicePacket packet)) { return; }
            incoming.Enqueue(senderClientId, packet);
            Action callback = Volatile.Read(ref arrivalCallback);
            if (callback != null) { callback(); }
        }

    }

}
