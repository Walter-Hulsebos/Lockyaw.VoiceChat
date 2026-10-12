using System;

namespace Lockyaw.VoiceChat {

    public readonly struct VoiceRouteId : IEquatable<VoiceRouteId> {

        public readonly ulong ObjectId;
        public readonly VoiceRouteRole Role;
        public bool IsValid => Role >= VoiceRouteRole.Participant && Role <= VoiceRouteRole.Broadcast;

        public VoiceRouteId(ulong objectId, VoiceRouteRole role) {
            ObjectId = objectId;
            Role = role;
        }

        public bool Equals(VoiceRouteId other) => ObjectId == other.ObjectId && Role == other.Role;

        public override bool Equals(object obj) => obj is VoiceRouteId other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ObjectId, Role);

        public static VoiceRouteId Participant(ulong objectId) => new(objectId, VoiceRouteRole.Participant);

        public static VoiceRouteId World(ulong objectId) => new(objectId, VoiceRouteRole.World);

        public static VoiceRouteId Broadcast(ulong objectId) => new(objectId, VoiceRouteRole.Broadcast);

        public static bool operator ==(VoiceRouteId left, VoiceRouteId right) => left.Equals(right);

        public static bool operator !=(VoiceRouteId left, VoiceRouteId right) => !left.Equals(right);

    }

}
