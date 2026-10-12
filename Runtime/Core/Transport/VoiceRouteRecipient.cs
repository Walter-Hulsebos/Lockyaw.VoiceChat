namespace Lockyaw.VoiceChat {

    public readonly struct VoiceRouteRecipient {

        public readonly ulong PeerId;
        public readonly VoiceRouteId DestinationRouteId;
        public readonly bool HasBroadcastRoute;
        public readonly BroadcastRoute BroadcastRoute;

        public VoiceRouteRecipient(ulong peerId, VoiceRouteId destinationRouteId) {
            PeerId = peerId;
            DestinationRouteId = destinationRouteId;
            HasBroadcastRoute = false;
            BroadcastRoute = default;
        }

        public VoiceRouteRecipient(ulong peerId, VoiceRouteId destinationRouteId, BroadcastRoute broadcastRoute) {
            PeerId = peerId;
            DestinationRouteId = destinationRouteId;
            HasBroadcastRoute = true;
            BroadcastRoute = broadcastRoute;
        }

    }

}
