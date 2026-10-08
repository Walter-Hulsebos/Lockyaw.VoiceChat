using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal interface IMicrophoneApi {

        AudioClip Start(string deviceName, int lengthSeconds, int sampleRate);
        void End(string deviceName);
        string[] GetDevices();
        bool IsRecording(string deviceName);
        int GetPosition(string deviceName);

    }

}
