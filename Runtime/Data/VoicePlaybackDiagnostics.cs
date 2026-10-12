namespace Lockyaw.VoiceChat {

    public readonly struct VoicePlaybackDiagnostics {

        public int AudioCallbackSamples { get; }
        public int OutputSampleRate { get; }
        public float BufferedMilliseconds { get; }
        public long ReceivedFrames { get; }
        public long QueueDrops { get; }
        public int PrebufferWaits { get; }
        public int RebufferEvents { get; }
        public int PartialReads { get; }
        public float OverflowedMilliseconds { get; }
        public long AudioCallbacks { get; }
        public long RenderedSignalSamples { get; }
        public float TargetMilliseconds { get; }
        public float JitterMilliseconds { get; }
        // Capture-to-render time of locally captured audio heard through loopback; zero when not measured.
        public float LoopbackLatencyMilliseconds { get; }
        public float OutputBufferMilliseconds { get; }

        public VoicePlaybackDiagnostics(int audioCallbackSamples, int outputSampleRate, float bufferedMilliseconds, long receivedFrames, long queueDrops, int prebufferWaits, int rebufferEvents, int partialReads, float overflowedMilliseconds, long audioCallbacks, long renderedSignalSamples, float targetMilliseconds = 0f, float jitterMilliseconds = 0f, float loopbackLatencyMilliseconds = 0f, float outputBufferMilliseconds = 0f) {
            AudioCallbackSamples = audioCallbackSamples;
            OutputSampleRate = outputSampleRate;
            BufferedMilliseconds = bufferedMilliseconds;
            ReceivedFrames = receivedFrames;
            QueueDrops = queueDrops;
            PrebufferWaits = prebufferWaits;
            RebufferEvents = rebufferEvents;
            PartialReads = partialReads;
            OverflowedMilliseconds = overflowedMilliseconds;
            AudioCallbacks = audioCallbacks;
            RenderedSignalSamples = renderedSignalSamples;
            TargetMilliseconds = targetMilliseconds;
            JitterMilliseconds = jitterMilliseconds;
            LoopbackLatencyMilliseconds = loopbackLatencyMilliseconds;
            OutputBufferMilliseconds = outputBufferMilliseconds;
        }

    }

}
