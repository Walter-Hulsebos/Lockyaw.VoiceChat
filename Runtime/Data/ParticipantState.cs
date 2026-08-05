using System;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct ParticipantState : INetworkSerializable, IEquatable<ParticipantState> {

        public VoiceStateFlags Flags;
        public byte Level;
        public ushort Revision;

        public bool IsInputMuted => (Flags & VoiceStateFlags.InputMuted) != 0;
        public bool IsDeafened => (Flags & VoiceStateFlags.Deafened) != 0;
        public bool IsSpeaking => (Flags & VoiceStateFlags.Speaking) != 0;

        public ParticipantState(VoiceStateFlags flags, byte level, ushort revision) {
            Flags = flags;
            Level = level;
            Revision = revision;
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            serializer.SerializeValue(ref Flags);
            serializer.SerializeValue(ref Level);
            serializer.SerializeValue(ref Revision);
        }

        public ParticipantState WithInputMuted(bool isInputMuted) => WithFlag(VoiceStateFlags.InputMuted, isInputMuted);

        public ParticipantState WithDeafened(bool isDeafened) => WithFlag(VoiceStateFlags.Deafened, isDeafened);

        public ParticipantState WithActivity(bool isSpeaking, byte level) {
            VoiceStateFlags flags = isSpeaking ? Flags | VoiceStateFlags.Speaking : Flags & ~VoiceStateFlags.Speaking;
            byte normalizedLevel = isSpeaking ? level : (byte)0;
            if (flags == Flags && normalizedLevel == Level) { return this; }
            return new ParticipantState(flags, normalizedLevel, GetNextRevision());
        }

        public bool Equals(ParticipantState other) {
            return Flags == other.Flags &&
                   Level == other.Level &&
                   Revision == other.Revision;
        }

        public override bool Equals(object obj) => obj is ParticipantState state && Equals(state);

        public override int GetHashCode() => HashCode.Combine((byte)Flags, Level, Revision);

        public static bool operator ==(ParticipantState left, ParticipantState right) => left.Equals(right);

        public static bool operator !=(ParticipantState left, ParticipantState right) => !left.Equals(right);

        private ParticipantState WithFlag(VoiceStateFlags flag, bool isEnabled) {
            VoiceStateFlags flags = isEnabled ? Flags | flag : Flags & ~flag;
            if (flags == Flags) { return this; }
            return new ParticipantState(flags, Level, GetNextRevision());
        }

        private ushort GetNextRevision() => unchecked((ushort)(Revision + 1));

    }

}
