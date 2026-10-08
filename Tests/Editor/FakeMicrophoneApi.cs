using System;
using UnityEngine;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class FakeMicrophoneApi : IMicrophoneApi {

        public string[] Devices = Array.Empty<string>();
        public Exception EnumerationFailure;
        public Exception StartFailure;
        public Exception RecordingFailure;
        public Exception PositionFailure;
        public Exception EndFailure;
        public AudioClip CaptureClip;
        public bool Recording = true;
        public int StartCount { get; private set; }
        public int EndCount { get; private set; }
        public int RecordingQueryCount { get; private set; }
        public int PositionQueryCount { get; private set; }

        public AudioClip Start(string deviceName, int lengthSeconds, int sampleRate) {
            StartCount++;
            if (StartFailure != null) { throw StartFailure; }
            return CaptureClip;
        }

        public void End(string deviceName) {
            EndCount++;
            if (EndFailure != null) { throw EndFailure; }
        }

        public string[] GetDevices() {
            if (EnumerationFailure != null) { throw EnumerationFailure; }
            return Devices;
        }

        public bool IsRecording(string deviceName) {
            RecordingQueryCount++;
            if (RecordingFailure != null) { throw RecordingFailure; }
            return Recording;
        }

        public int GetPosition(string deviceName) {
            PositionQueryCount++;
            if (PositionFailure != null) { throw PositionFailure; }
            return 0;
        }

    }

}
