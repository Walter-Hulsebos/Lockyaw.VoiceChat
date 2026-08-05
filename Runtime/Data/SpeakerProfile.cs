using System;
using Unity.Collections;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct SpeakerProfile : INetworkSerializable, IEquatable<SpeakerProfile> {

        public FixedString128Bytes DisplayName;
        public ushort Revision;

        public bool IsValid => DisplayName.Length > 0;

        public SpeakerProfile(string displayName, ushort revision) {
            DisplayName = default;
            Revision = revision;

            string normalizedDisplayName = displayName == null ? string.Empty : displayName.Trim();
            DisplayName.CopyFromTruncated(normalizedDisplayName);
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            serializer.SerializeValue(ref DisplayName);
            serializer.SerializeValue(ref Revision);
        }

        public SpeakerProfile WithDisplayName(string displayName) {
            SpeakerProfile requestedProfile = new(displayName, unchecked((ushort)(Revision + 1)));
            if (DisplayName.Equals(requestedProfile.DisplayName)) { return this; }
            return requestedProfile;
        }

        public bool Equals(SpeakerProfile other) {
            return DisplayName.Equals(other.DisplayName) &&
                   Revision == other.Revision;
        }

        public override bool Equals(object obj) => obj is SpeakerProfile profile && Equals(profile);

        public override int GetHashCode() => HashCode.Combine(DisplayName, Revision);

        public override string ToString() => DisplayName.ToString();

        public static bool operator ==(SpeakerProfile left, SpeakerProfile right) => left.Equals(right);

        public static bool operator !=(SpeakerProfile left, SpeakerProfile right) => !left.Equals(right);

    }

}
