using System;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct BroadcastFrame : INetworkSerializable, IEquatable<BroadcastFrame> {

        public BroadcastRoute Route;
        public VoiceFrame Frame;

        public bool IsValid => Route.IsValid && Frame.IsValid;

        public BroadcastFrame(BroadcastRoute route, VoiceFrame frame) {
            Route = route;
            Frame = frame;
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            serializer.SerializeValue(ref Route);
            serializer.SerializeValue(ref Frame);
        }

        public BroadcastFrame WithRelay(ulong relayNetworkObjectId) {
            if (!CanRelayThrough(relayNetworkObjectId)) { return this; }
            return new BroadcastFrame(Route.WithRelay(relayNetworkObjectId), Frame);
        }

        public bool CanPlayOn(int channel, ulong outputNetworkObjectId) {
            return IsValid &&
                   outputNetworkObjectId != BroadcastRoute.UNASSIGNED_NETWORK_OBJECT_ID &&
                   Route.MatchesChannel(channel) &&
                   !Route.ContainsEndpoint(outputNetworkObjectId);
        }

        public bool CanRelayThrough(ulong relayNetworkObjectId) => IsValid && Route.CanRelayThrough(relayNetworkObjectId);

        public bool Equals(BroadcastFrame other) => Route == other.Route && Frame == other.Frame;

        public override bool Equals(object obj) => obj is BroadcastFrame frame && Equals(frame);

        public override int GetHashCode() => HashCode.Combine(Route, Frame);

        public static bool operator ==(BroadcastFrame left, BroadcastFrame right) => left.Equals(right);

        public static bool operator !=(BroadcastFrame left, BroadcastFrame right) => !left.Equals(right);

    }

}
