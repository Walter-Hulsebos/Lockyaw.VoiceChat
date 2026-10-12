using System;

namespace Lockyaw.VoiceChat {

    internal readonly struct VoiceStreamKey : IEquatable<VoiceStreamKey> {

        internal ulong SourceClientId { get; }
        internal uint StreamId { get; }

        internal VoiceStreamKey(VoiceFrame frame) {
            SourceClientId = frame.SourceClientId;
            StreamId = frame.StreamId;
        }

        public bool Equals(VoiceStreamKey other) => SourceClientId == other.SourceClientId && StreamId == other.StreamId;

        public override bool Equals(object obj) => obj is VoiceStreamKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(SourceClientId, StreamId);

    }

}
