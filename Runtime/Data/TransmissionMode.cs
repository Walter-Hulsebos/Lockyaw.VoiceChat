using UnityEngine;

namespace Lockyaw.VoiceChat {

    public enum TransmissionMode {
        [InspectorName("Open Microphone")]
        Open = 0,
        [InspectorName("Push to Talk")]
        PushToTalk = 1,
        [InspectorName("Voice Activated")]
        VoiceActivated = 2
    }

}
