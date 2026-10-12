using System;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [Serializable]
    public struct VoiceDeviceId : IEquatable<VoiceDeviceId> {

        [SerializeField] private string value;

        public static VoiceDeviceId Default => default;
        public static VoiceDeviceId None => new("none");
        public string Value => value == null ? "" : value;
        public bool IsDefault => Value.Length == 0;
        public bool IsNone => Value == "none";

        private VoiceDeviceId(string value) {
            this.value = value;
        }

        public static VoiceDeviceId FromValue(string value) => new(string.IsNullOrWhiteSpace(value) ? "" : value);

        public bool Equals(VoiceDeviceId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is VoiceDeviceId other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

        public override string ToString() => Value;

        public static bool operator ==(VoiceDeviceId left, VoiceDeviceId right) => left.Equals(right);

        public static bool operator !=(VoiceDeviceId left, VoiceDeviceId right) => !left.Equals(right);

    }

}
