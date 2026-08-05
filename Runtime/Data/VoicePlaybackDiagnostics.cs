namespace Lockyaw.VoiceChat {

    public readonly struct VoicePlaybackDiagnostics {

        public int AudioCallbackSamples { get; }
        public int OutputSampleRate { get; }
        public int DspBufferSamples { get; }
        public int DspBufferCount { get; }
        public float BufferedMilliseconds { get; }
        public long ReceivedFrames { get; }
        public long MissingFrames { get; }
        public float IntentionalSilenceMilliseconds { get; }
        public long LateFrames { get; }
        public long ReorderedFrames { get; }
        public long DuplicateFrames { get; }
        public long FinalizedGaps { get; }
        public long QueuePressureEvents { get; }
        public long RejectedSequenceAdvances { get; }
        public long ForwardErrorCorrectionAttempts { get; }
        public int PendingEncodedFrames { get; }
        public int MaximumPendingEncodedFrames { get; }
        public long ConcealedFrames { get; }
        public long DecoderResets { get; }
        public int PrebufferWaits { get; }
        public int RebufferEvents { get; }
        public int ConcurrentReadAborts { get; }
        public int PartialReads { get; }
        public float OverflowedMilliseconds { get; }

        public VoicePlaybackDiagnostics(
            int audioCallbackSamples,
            int outputSampleRate,
            int dspBufferSamples,
            int dspBufferCount,
            float bufferedMilliseconds,
            long receivedFrames,
            long missingFrames,
            float intentionalSilenceMilliseconds,
            long lateFrames,
            long reorderedFrames,
            long duplicateFrames,
            long finalizedGaps,
            long queuePressureEvents,
            long rejectedSequenceAdvances,
            long forwardErrorCorrectionAttempts,
            int pendingEncodedFrames,
            int maximumPendingEncodedFrames,
            long concealedFrames,
            long decoderResets,
            int prebufferWaits,
            int rebufferEvents,
            int concurrentReadAborts,
            int partialReads,
            float overflowedMilliseconds) {
            AudioCallbackSamples = audioCallbackSamples;
            OutputSampleRate = outputSampleRate;
            DspBufferSamples = dspBufferSamples;
            DspBufferCount = dspBufferCount;
            BufferedMilliseconds = bufferedMilliseconds;
            ReceivedFrames = receivedFrames;
            MissingFrames = missingFrames;
            IntentionalSilenceMilliseconds = intentionalSilenceMilliseconds;
            LateFrames = lateFrames;
            ReorderedFrames = reorderedFrames;
            DuplicateFrames = duplicateFrames;
            FinalizedGaps = finalizedGaps;
            QueuePressureEvents = queuePressureEvents;
            RejectedSequenceAdvances = rejectedSequenceAdvances;
            ForwardErrorCorrectionAttempts = forwardErrorCorrectionAttempts;
            PendingEncodedFrames = pendingEncodedFrames;
            MaximumPendingEncodedFrames = maximumPendingEncodedFrames;
            ConcealedFrames = concealedFrames;
            DecoderResets = decoderResets;
            PrebufferWaits = prebufferWaits;
            RebufferEvents = rebufferEvents;
            ConcurrentReadAborts = concurrentReadAborts;
            PartialReads = partialReads;
            OverflowedMilliseconds = overflowedMilliseconds;
        }

    }

}
