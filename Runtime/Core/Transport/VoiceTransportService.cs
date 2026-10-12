using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Lockyaw.VoiceChat {

    public sealed class VoiceTransportService : IDisposable {

        private sealed class ValidationState {
            internal FrameReplayWindow ReplayWindow;
            internal ulong SourcePeerId;
            internal ConversationMembership Membership;
            internal long WindowStartedAt;
            internal long LastFrameAt;
            internal int FramesInWindow;
            internal long SamplesInWindow;
        }

        // Route limits are expressed in 10 ms frames so one setting admits the same audio duration at every frame length.
        internal const int RATE_LIMIT_SAMPLES_PER_FRAME = 480;
        // Carriers that report arrivals only need periodic housekeeping; other carriers are polled.
        private const int HOUSEKEEPING_MILLISECONDS = 20;
        private const int POLL_MILLISECONDS = 1;

        public ulong LocalPeerId { get; }
        public ulong AuthorityPeerId { get; }
        public ulong Epoch { get; }
        public VoiceTransportCapability Capability => carrier.Capability;
        public VoiceTransportState State => disposed ? VoiceTransportState.Stopped : carrier.State;
        public string FailureReason => failureReason.Length == 0 ? carrier.FailureReason : failureReason;
        public long RejectedPackets => Interlocked.Read(ref rejectedPackets);

        private readonly IVoicePacketCarrier carrier;
        private readonly VoicePacketQueue submissions = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly Thread worker;
        private readonly object receiverSynchronization = new();
        private readonly object transmissionSynchronization = new();
        private readonly object disposalSynchronization = new();
        private VoiceTransmissionGate transmissionGate = new();
        private readonly Dictionary<VoiceRouteId, ValidationState> validations = new();
        private readonly int idleWaitMilliseconds;
        private Dictionary<VoiceRouteId, VoiceRouteSnapshot> routes = new();
        private Dictionary<VoiceRouteId, IVoicePacketReceiver> receivers = new();
        private volatile bool disposed;
        private bool disposeOnWorker;
        private bool resourcesDisposed;
        private bool hasUnsentPackets;
        private volatile string failureReason = string.Empty;
        private long rejectedPackets;

        public VoiceTransportService(ulong localPeerId, ulong authorityPeerId, ulong epoch, IVoicePacketCarrier carrier) {
            if (carrier == null) { throw new ArgumentNullException(nameof(carrier)); }
            if (epoch == 0) { throw new ArgumentOutOfRangeException(nameof(epoch)); }
            LocalPeerId = localPeerId;
            AuthorityPeerId = authorityPeerId;
            Epoch = epoch;
            this.carrier = carrier;
            if (carrier is IVoicePacketArrivalNotifier notifier) {
                notifier.SetArrivalCallback(Wake);
                idleWaitMilliseconds = HOUSEKEEPING_MILLISECONDS;
            } else {
                idleWaitMilliseconds = POLL_MILLISECONDS;
            }
            worker = new Thread(Service) { IsBackground = true, Name = "Lockyaw voice delivery", Priority = ThreadPriority.Highest };
            worker.Start();
        }

        public void SetSendingEnabled(bool enabled) {
            lock (transmissionSynchronization) {
                if (enabled) {
                    if (!transmissionGate.IsEnabled) { Volatile.Write(ref transmissionGate, new VoiceTransmissionGate()); }
                } else {
                    transmissionGate.Cancel();
                    submissions.Clear();
                }
            }
            Wake();
        }

        public void PublishRoutes(VoiceRouteSnapshot[] snapshots) {
            Dictionary<VoiceRouteId, VoiceRouteSnapshot> next = new();
            if (snapshots != null) {
                for (int i = 0; i < snapshots.Length && i < 4096; i++) {
                    VoiceRouteSnapshot snapshot = snapshots[i];
                    if (snapshot != null) { next[snapshot.RouteId] = snapshot; }
                }
            }
            Volatile.Write(ref routes, next);
            Wake();
        }

        public void RegisterReceiver(VoiceRouteId routeId, IVoicePacketReceiver receiver) {
            if (receiver == null) { throw new ArgumentNullException(nameof(receiver)); }
            lock (receiverSynchronization) {
                Dictionary<VoiceRouteId, IVoicePacketReceiver> next = new(receivers) { [routeId] = receiver };
                Volatile.Write(ref receivers, next);
            }
        }

        public void UnregisterReceiver(VoiceRouteId routeId) {
            lock (receiverSynchronization) {
                Dictionary<VoiceRouteId, IVoicePacketReceiver> next = new(receivers);
                next.Remove(routeId);
                Volatile.Write(ref receivers, next);
            }
        }

        public bool Submit(VoiceRouteId routeId, VoiceFrame frame) {
            VoiceTransmissionGate currentGate = Volatile.Read(ref transmissionGate);
            if (disposed || !frame.IsValid || !currentGate.IsEnabled) { return false; }
            submissions.Enqueue(LocalPeerId, new VoicePacket(VoicePacketKind.Submission, Epoch, routeId, frame).WithTransmissionGate(currentGate));
            Wake();
            return true;
        }

        public void Dispose() {
            disposed = true;
            SetSendingEnabled(false);
            Wake();
            if (Thread.CurrentThread == worker) { disposeOnWorker = true; return; }
            worker.Join();
            DisposeResources();
        }

        private void Wake() {
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
        }

        private void Service() {
            try {
                while (!disposed) {
                    try {
                        carrier.Service();
                        hasUnsentPackets = false;
                        for (int i = 0; !disposed && i < 128 && carrier.TryReceive(out ulong peerId, out VoicePacket packet); i++) {
                            Process(peerId, packet);
                        }
                        for (int i = 0; !disposed && i < 128 && submissions.TryDequeue(out ulong peerId, out VoicePacket packet); i++) {
                            if (LocalPeerId == AuthorityPeerId) { Process(peerId, packet); }
                            else { Send(AuthorityPeerId, packet); }
                        }
                        // Carriers transmit while servicing, so flush packets queued in this pass instead of waiting for the next wake.
                        if (!disposed && hasUnsentPackets) { carrier.Service(); }
                    } catch (Exception exception) {
                        failureReason = exception.GetType().Name + ": " + exception.Message;
                    }
                    if (!disposed) { wake.WaitOne(idleWaitMilliseconds); }
                }
            } finally {
                if (disposeOnWorker) { DisposeResources(); }
            }
        }

        private void Send(ulong peerId, VoicePacket packet) {
            if (carrier.TrySend(peerId, packet)) { hasUnsentPackets = true; }
        }

        private void DisposeResources() {
            lock (disposalSynchronization) {
                if (resourcesDisposed) { return; }
                try { carrier.Dispose(); }
                catch (Exception exception) { failureReason = exception.GetType().Name + ": " + exception.Message; }
                finally {
                    submissions.Clear();
                    wake.Dispose();
                    resourcesDisposed = true;
                }
            }
        }

        private void Process(ulong peerId, VoicePacket packet) {
            if (!packet.CanTransmit) { return; }
            if (!packet.IsValid || packet.Epoch != Epoch) { Reject(); return; }
            if (packet.Kind == VoicePacketKind.Delivery) {
                if (peerId != AuthorityPeerId) { Reject(); return; }
                Deliver(packet);
                return;
            }
            if (LocalPeerId != AuthorityPeerId || packet.HasBroadcastRoute) { Reject(); return; }
            Dictionary<VoiceRouteId, VoiceRouteSnapshot> currentRoutes = Volatile.Read(ref routes);
            if (!currentRoutes.TryGetValue(packet.RouteId, out VoiceRouteSnapshot route) || !route.IsEnabled ||
                route.SourcePeerId != peerId || packet.Frame.SourceClientId != peerId || route.Membership != packet.Frame.Membership ||
                !Accept(route, packet.Frame)) {
                Reject();
                return;
            }
            for (int i = 0; !disposed && i < route.Recipients.Count; i++) {
                VoiceRouteRecipient recipient = route.Recipients[i];
                VoicePacket delivery = recipient.HasBroadcastRoute
                    ? new VoicePacket(VoicePacketKind.Delivery, Epoch, recipient.DestinationRouteId, packet.Frame, recipient.BroadcastRoute)
                    : new VoicePacket(VoicePacketKind.Delivery, Epoch, recipient.DestinationRouteId, packet.Frame);
                delivery = delivery.WithTransmissionGate(packet.TransmissionGate);
                if (recipient.PeerId == LocalPeerId) { Deliver(delivery); }
                else { Send(recipient.PeerId, delivery); }
            }
        }

        private void Deliver(VoicePacket packet) {
            if (!packet.CanTransmit) { return; }
            Dictionary<VoiceRouteId, IVoicePacketReceiver> currentReceivers = Volatile.Read(ref receivers);
            if (currentReceivers.TryGetValue(packet.RouteId, out IVoicePacketReceiver receiver)) {
                receiver.ReceivePacket(packet);
            }
        }

        private void Reject() {
            Interlocked.Increment(ref rejectedPackets);
        }

        private bool Accept(VoiceRouteSnapshot route, VoiceFrame frame) {
            if (!validations.TryGetValue(route.RouteId, out ValidationState validation)) {
                if (validations.Count >= 4096) { validations.Clear(); }
                validation = new ValidationState();
                validations.Add(route.RouteId, validation);
            }
            if (validation.SourcePeerId != route.SourcePeerId || validation.Membership != route.Membership) {
                validation.ReplayWindow.Clear();
                validation.SourcePeerId = route.SourcePeerId;
                validation.Membership = route.Membership;
                validation.LastFrameAt = 0;
            }
            long currentTime = Stopwatch.GetTimestamp();
            if (currentTime - validation.WindowStartedAt >= Stopwatch.Frequency) {
                validation.WindowStartedAt = currentTime;
                validation.FramesInWindow = 0;
                validation.SamplesInWindow = 0;
            }
            validation.FramesInWindow++;
            validation.SamplesInWindow += frame.SampleCount;
            if (validation.SamplesInWindow > (long)route.MaxFramesPerSecond * RATE_LIMIT_SAMPLES_PER_FRAME ||
                validation.FramesInWindow > route.MaxFramesPerSecond * 3 ||
                !validation.ReplayWindow.TryAccept(frame.SourceClientId, frame.StreamId, frame.Sequence, 1024, currentTime - validation.LastFrameAt >= Stopwatch.Frequency * 2)) {
                return false;
            }
            validation.LastFrameAt = currentTime;
            return true;
        }

    }

}
