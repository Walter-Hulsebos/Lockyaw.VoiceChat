using UnityEngine;

namespace Lockyaw.VoiceChat {

    public enum VoiceFrameLength : ushort {
        [InspectorName("10 ms")]
        TenMilliseconds = 480,
        [InspectorName("20 ms")]
        TwentyMilliseconds = 960,
        [InspectorName("40 ms")]
        FortyMilliseconds = 1920
    }

}
