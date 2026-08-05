using System;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct BroadcastControlState : INetworkSerializable, IEquatable<BroadcastControlState> {

        public const ulong NO_OPERATOR_CLIENT_ID = ulong.MaxValue;

        public bool IsBroadcasting;
        public ulong OperatorClientId;
        public ushort Revision;

        public bool IsValid => IsBroadcasting
            ? OperatorClientId != NO_OPERATOR_CLIENT_ID
            : OperatorClientId == NO_OPERATOR_CLIENT_ID;
        public static BroadcastControlState Idle => new(false, NO_OPERATOR_CLIENT_ID, 0);

        public BroadcastControlState(bool isBroadcasting, ulong operatorClientId, ushort revision) {
            IsBroadcasting = isBroadcasting;
            OperatorClientId = operatorClientId;
            Revision = revision;
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            serializer.SerializeValue(ref IsBroadcasting);
            serializer.SerializeValue(ref OperatorClientId);
            serializer.SerializeValue(ref Revision);
        }

        public BroadcastControlState WithBroadcastStarted(ulong operatorClientId) {
            if (!IsValid || IsBroadcasting || operatorClientId == NO_OPERATOR_CLIENT_ID) { return this; }
            return new BroadcastControlState(true, operatorClientId, GetNextRevision());
        }

        public BroadcastControlState WithBroadcastEnded(ulong operatorClientId) {
            if (!IsOperatedBy(operatorClientId)) { return this; }
            return new BroadcastControlState(false, NO_OPERATOR_CLIENT_ID, GetNextRevision());
        }

        public bool IsOperatedBy(ulong clientId) {
            return IsValid &&
                   IsBroadcasting &&
                   clientId != NO_OPERATOR_CLIENT_ID &&
                   OperatorClientId == clientId;
        }

        public bool Equals(BroadcastControlState other) {
            return IsBroadcasting == other.IsBroadcasting &&
                   OperatorClientId == other.OperatorClientId &&
                   Revision == other.Revision;
        }

        public override bool Equals(object obj) => obj is BroadcastControlState state && Equals(state);

        public override int GetHashCode() => HashCode.Combine(IsBroadcasting, OperatorClientId, Revision);

        public static bool operator ==(BroadcastControlState left, BroadcastControlState right) => left.Equals(right);

        public static bool operator !=(BroadcastControlState left, BroadcastControlState right) => !left.Equals(right);

        private ushort GetNextRevision() => unchecked((ushort)(Revision + 1));

    }

}
