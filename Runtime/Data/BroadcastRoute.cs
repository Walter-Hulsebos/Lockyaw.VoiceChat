using System;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct BroadcastRoute : INetworkSerializable, IEquatable<BroadcastRoute> {

        public const int NO_CHANNEL = -1;
        public const ulong UNASSIGNED_NETWORK_OBJECT_ID = 0;
        public const ulong UNASSIGNED_CLIENT_ID = ulong.MaxValue;
        public const byte MAXIMUM_HOP_COUNT = 1;

        public int Channel;
        public ulong SourceNetworkObjectId;
        public ulong OriginNetworkObjectId;
        public ulong OriginClientId;
        public byte HopCount;

        public bool IsValid => Channel >= 0 &&
                               SourceNetworkObjectId != UNASSIGNED_NETWORK_OBJECT_ID &&
                               OriginNetworkObjectId != UNASSIGNED_NETWORK_OBJECT_ID &&
                               HopCount <= MAXIMUM_HOP_COUNT;
        public bool IsDirect => HopCount == 0;
        public bool HasReachedHopLimit => HopCount >= MAXIMUM_HOP_COUNT;
        public bool HasOriginClient => OriginClientId != UNASSIGNED_CLIENT_ID;

        public BroadcastRoute(
            int channel,
            ulong sourceNetworkObjectId,
            ulong originNetworkObjectId,
            byte hopCount = 0,
            ulong originClientId = UNASSIGNED_CLIENT_ID) {
            Channel = channel;
            SourceNetworkObjectId = sourceNetworkObjectId;
            OriginNetworkObjectId = originNetworkObjectId;
            OriginClientId = originClientId;
            HopCount = hopCount;
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            serializer.SerializeValue(ref Channel);
            serializer.SerializeValue(ref SourceNetworkObjectId);
            serializer.SerializeValue(ref OriginNetworkObjectId);
            serializer.SerializeValue(ref OriginClientId);
            serializer.SerializeValue(ref HopCount);
        }

        public BroadcastRoute WithRelay(ulong relayNetworkObjectId) {
            if (!CanRelayThrough(relayNetworkObjectId)) { return this; }
            return new BroadcastRoute(Channel, relayNetworkObjectId, OriginNetworkObjectId, (byte)(HopCount + 1), OriginClientId);
        }

        public bool MatchesChannel(int channel) => IsValid && channel >= 0 && Channel == channel;

        public bool ContainsEndpoint(ulong networkObjectId) {
            return networkObjectId != UNASSIGNED_NETWORK_OBJECT_ID &&
                   (SourceNetworkObjectId == networkObjectId || OriginNetworkObjectId == networkObjectId);
        }

        public bool CanRelayThrough(ulong relayNetworkObjectId) {
            return IsValid &&
                   !HasReachedHopLimit &&
                   relayNetworkObjectId != UNASSIGNED_NETWORK_OBJECT_ID &&
                   !ContainsEndpoint(relayNetworkObjectId);
        }

        public bool Equals(BroadcastRoute other) {
            return Channel == other.Channel &&
                   SourceNetworkObjectId == other.SourceNetworkObjectId &&
                   OriginNetworkObjectId == other.OriginNetworkObjectId &&
                   OriginClientId == other.OriginClientId &&
                   HopCount == other.HopCount;
        }

        public override bool Equals(object obj) => obj is BroadcastRoute route && Equals(route);

        public override int GetHashCode() => HashCode.Combine(Channel, SourceNetworkObjectId, OriginNetworkObjectId, OriginClientId, HopCount);

        public static bool operator ==(BroadcastRoute left, BroadcastRoute right) => left.Equals(right);

        public static bool operator !=(BroadcastRoute left, BroadcastRoute right) => !left.Equals(right);

    }

}
