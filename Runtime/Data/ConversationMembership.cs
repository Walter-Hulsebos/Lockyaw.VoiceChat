using System;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct ConversationMembership : INetworkSerializable, IEquatable<ConversationMembership> {

        public const int EVERYONE = 0;

        public int Group;
        public ushort Revision;

        public bool IsEveryone => Group == EVERYONE;
        public bool IsValid => Group >= EVERYONE;

        public ConversationMembership(int group, ushort revision) {
            Group = group;
            Revision = revision;
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            serializer.SerializeValue(ref Group);
            serializer.SerializeValue(ref Revision);
        }

        public ConversationMembership WithGroup(int group) {
            if (Group == group) { return this; }
            return new ConversationMembership(group, unchecked((ushort)(Revision + 1)));
        }

        public bool Reaches(ConversationMembership listener) {
            return IsValid &&
                   listener.IsValid &&
                   (IsEveryone || Group == listener.Group);
        }

        public bool Equals(ConversationMembership other) {
            return Group == other.Group &&
                   Revision == other.Revision;
        }

        public override bool Equals(object obj) => obj is ConversationMembership membership && Equals(membership);

        public override int GetHashCode() => HashCode.Combine(Group, Revision);

        public static bool operator ==(ConversationMembership left, ConversationMembership right) => left.Equals(right);

        public static bool operator !=(ConversationMembership left, ConversationMembership right) => !left.Equals(right);

    }

}
