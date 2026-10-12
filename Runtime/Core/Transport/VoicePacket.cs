namespace Lockyaw.VoiceChat {

    public readonly struct VoicePacket {

        public readonly VoicePacketKind Kind;
        public readonly ulong Epoch;
        public readonly VoiceRouteId RouteId;
        public readonly VoiceFrame Frame;
        public readonly bool HasBroadcastRoute;
        public readonly BroadcastRoute BroadcastRoute;
        public bool CanTransmit => TransmissionGate == null || TransmissionGate.IsEnabled;
        internal readonly VoiceTransmissionGate TransmissionGate;

        public bool IsValid => Epoch != 0 && RouteId.IsValid && Frame.IsValid && (!HasBroadcastRoute || BroadcastRoute.IsValid) &&
                               (Kind == VoicePacketKind.Submission || Kind == VoicePacketKind.Delivery);

        public VoicePacket(VoicePacketKind kind, ulong epoch, VoiceRouteId routeId, VoiceFrame frame) {
            Kind = kind;
            Epoch = epoch;
            RouteId = routeId;
            Frame = frame;
            HasBroadcastRoute = false;
            BroadcastRoute = default;
            TransmissionGate = null;
        }

        public VoicePacket(VoicePacketKind kind, ulong epoch, VoiceRouteId routeId, VoiceFrame frame, BroadcastRoute broadcastRoute) {
            Kind = kind;
            Epoch = epoch;
            RouteId = routeId;
            Frame = frame;
            HasBroadcastRoute = true;
            BroadcastRoute = broadcastRoute;
            TransmissionGate = null;
        }

        private VoicePacket(VoicePacket packet, VoiceTransmissionGate transmissionGate) {
            Kind = packet.Kind;
            Epoch = packet.Epoch;
            RouteId = packet.RouteId;
            Frame = packet.Frame;
            HasBroadcastRoute = packet.HasBroadcastRoute;
            BroadcastRoute = packet.BroadcastRoute;
            TransmissionGate = transmissionGate;
        }

        internal VoicePacket WithTransmissionGate(VoiceTransmissionGate transmissionGate) => new(this, transmissionGate);

    }

}
