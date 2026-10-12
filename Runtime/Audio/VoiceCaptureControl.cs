using System;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceCaptureControl {

        internal readonly bool Enabled;
        internal readonly VoiceDeviceId DeviceId;
        internal readonly bool TransmissionHeld;
        internal readonly bool Muted;
        internal readonly ConversationMembership Membership;
        internal readonly ulong SourceClientId;
        internal readonly float MicrophoneVolume;

        internal VoiceCaptureControl(bool enabled, VoiceDeviceId deviceId, bool transmissionHeld, bool muted, ConversationMembership membership, ulong sourceClientId, float microphoneVolume = 1f) {
            Enabled = enabled;
            DeviceId = deviceId;
            TransmissionHeld = transmissionHeld;
            Muted = muted;
            Membership = membership;
            SourceClientId = sourceClientId;
            MicrophoneVolume = float.IsNaN(microphoneVolume) || float.IsInfinity(microphoneVolume) ? 0f : Math.Clamp(microphoneVolume, 0f, 4f);
        }

    }

}
