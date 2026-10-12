namespace Lockyaw.VoiceChat {

    public sealed class VoiceCaptureDiagnostics {

        public VoiceDeviceId DeviceId { get; }
        public string DeviceName { get; }
        public VoiceCaptureState State { get; }
        public int SampleRate { get; }
        public int ChannelCount => 1;
        public float QueuedMilliseconds { get; }
        public float DroppedMilliseconds { get; }
        public long CapturedFrames { get; }
        public long EncodedFrames { get; }
        public long SuppressedFrames { get; }
        public long SubmittedFrames { get; }
        public int MaximumFramesPerPass { get; }
        public float FrameMilliseconds { get; }
        public float DevicePeriodMilliseconds { get; }
        public bool UsesDeviceSignal { get; }

        public VoiceCaptureDiagnostics(string deviceName = "", VoiceCaptureState state = VoiceCaptureState.Disabled, int sampleRate = 0, float queuedMilliseconds = 0f, float droppedMilliseconds = 0f, long capturedFrames = 0, long encodedFrames = 0, long suppressedFrames = 0, long submittedFrames = 0, int maximumFramesPerPass = 0, VoiceDeviceId deviceId = default, float frameMilliseconds = 0f, float devicePeriodMilliseconds = 0f, bool usesDeviceSignal = false) {
            DeviceId = deviceId;
            DeviceName = deviceName == null ? "" : deviceName;
            State = state;
            SampleRate = sampleRate;
            QueuedMilliseconds = queuedMilliseconds;
            DroppedMilliseconds = droppedMilliseconds;
            CapturedFrames = capturedFrames;
            EncodedFrames = encodedFrames;
            SuppressedFrames = suppressedFrames;
            SubmittedFrames = submittedFrames;
            MaximumFramesPerPass = maximumFramesPerPass;
            FrameMilliseconds = frameMilliseconds;
            DevicePeriodMilliseconds = devicePeriodMilliseconds;
            UsesDeviceSignal = usesDeviceSignal;
        }

    }

}
