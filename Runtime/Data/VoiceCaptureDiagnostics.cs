namespace Lockyaw.VoiceChat {

    public readonly struct VoiceCaptureDiagnostics {

        public string DeviceName { get; }
        public int SampleRate { get; }
        public int ChannelCount { get; }
        public float QueuedMilliseconds { get; }
        public float DroppedMilliseconds { get; }
        public long CapturedFrames { get; }
        public long EncodedFrames { get; }
        public long SuppressedFrames { get; }
        public long SubmittedFrames { get; }
        public int MaximumFramesPerUpdate { get; }

        public VoiceCaptureDiagnostics(
            string deviceName,
            int sampleRate,
            int channelCount,
            float queuedMilliseconds,
            float droppedMilliseconds,
            long capturedFrames,
            long encodedFrames,
            long suppressedFrames,
            long submittedFrames,
            int maximumFramesPerUpdate) {
            DeviceName = deviceName;
            SampleRate = sampleRate;
            ChannelCount = channelCount;
            QueuedMilliseconds = queuedMilliseconds;
            DroppedMilliseconds = droppedMilliseconds;
            CapturedFrames = capturedFrames;
            EncodedFrames = encodedFrames;
            SuppressedFrames = suppressedFrames;
            SubmittedFrames = submittedFrames;
            MaximumFramesPerUpdate = maximumFramesPerUpdate;
        }

    }

}
