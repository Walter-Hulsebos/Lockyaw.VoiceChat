using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal sealed class UnityMicrophoneApi : IMicrophoneApi {

        internal static readonly UnityMicrophoneApi Instance = new();

        public AudioClip Start(string deviceName, int lengthSeconds, int sampleRate) => Microphone.Start(deviceName, true, lengthSeconds, sampleRate);

        public void End(string deviceName) {
            Microphone.End(deviceName);
        }

        public string[] GetDevices() {
#if UNITY_SERVER || (UNITY_WEBGL && !UNITY_EDITOR)
            return System.Array.Empty<string>();
#else
            return Microphone.devices;
#endif
        }

        public bool IsRecording(string deviceName) => Microphone.IsRecording(deviceName);

        public int GetPosition(string deviceName) => Microphone.GetPosition(deviceName);

    }

}
