using System;

namespace Lockyaw.VoiceChat {

    internal sealed class BroadcastPlaybackStream : IDisposable {

        internal int BufferedSamples => sampleBuffer.GetSampleCount();
        internal double LastFrameReceivedAt => lastFrameReceivedAt;
        internal bool CanStart => BufferedSamples >= prebufferSamples;
        internal bool IsActive => reorderBuffer.HasStarted && !isTimedOut;
        internal ulong OriginClientId { get; set; } = BroadcastRoute.UNASSIGNED_CLIENT_ID;
        internal float PlaybackVolume { get; set; } = 1f;

        private const int MAXIMUM_PENDING_ENCODED_FRAMES = 16;
        private const int MIX_BUFFER_SAMPLES = 4096;
        private const uint MAXIMUM_SEQUENCE_ADVANCE = 1024;

        private readonly VoiceDecoderSession decoder = new();
        private readonly EncodedFrameReorderBuffer reorderBuffer;
        private readonly SampleRingBuffer sampleBuffer;
        private readonly float[] mixSamples = new float[MIX_BUFFER_SAMPLES];
        private readonly int maxConcealedFrames;
        private readonly int prebufferSamples;
        private double lastFrameReceivedAt;
        private byte pendingFrameLevel;
        private bool hasPendingFrameLevel;
        private bool isTimedOut;

        internal BroadcastPlaybackStream(
            int maxBufferSamples,
            int prebufferSamples,
            int maxConcealedFrames,
            int maximumReorderDelayMilliseconds) {
            sampleBuffer = new SampleRingBuffer(maxBufferSamples, prebufferSamples);
            reorderBuffer = new(MAXIMUM_PENDING_ENCODED_FRAMES, maximumReorderDelayMilliseconds, MAXIMUM_SEQUENCE_ADVANCE);
            this.maxConcealedFrames = maxConcealedFrames;
            this.prebufferSamples = prebufferSamples;
        }

        public void Dispose() {
            decoder.Dispose();
            reorderBuffer.Clear();
            sampleBuffer.Clear();
            hasPendingFrameLevel = false;
            isTimedOut = false;
        }

        internal void Clear() {
            sampleBuffer.Clear();
            decoder.ResetState();
            reorderBuffer.Clear();
            hasPendingFrameLevel = false;
            isTimedOut = false;
        }

        internal void ResetPlayback() {
            sampleBuffer.Clear();
            decoder.ResetState();
            reorderBuffer.ResetCurrentStream();
            hasPendingFrameLevel = false;
            isTimedOut = false;
        }

        internal void Receive(VoiceFrame frame, double receivedAt, int renderQuantumSamples) {
            if (!frame.IsValid) { return; }

            EncodedFrameReorderBuffer.Arrival arrival = reorderBuffer.Enqueue(frame, receivedAt);
            if (arrival == EncodedFrameReorderBuffer.Arrival.Invalid ||
                arrival == EncodedFrameReorderBuffer.Arrival.Duplicate ||
                arrival == EncodedFrameReorderBuffer.Arrival.Late ||
                arrival == EncodedFrameReorderBuffer.Arrival.SequenceAdvanceRejected ||
                arrival == EncodedFrameReorderBuffer.Arrival.StreamFormatRejected ||
                arrival == EncodedFrameReorderBuffer.Arrival.SampleTimelineRejected) {
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.CapacityReached) {
                Process(receivedAt, renderQuantumSamples, true);
                arrival = reorderBuffer.Enqueue(frame, receivedAt);
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.CapacityReached) { return; }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Discontinuity) {
                RestartEncodedStream(frame, receivedAt);
                return;
            }

            lastFrameReceivedAt = receivedAt;
            isTimedOut = false;
            Process(receivedAt, renderQuantumSamples, reorderBuffer.RequiresForcedDeadline);
        }

        internal void Process(double currentTime, int renderQuantumSamples, bool forceDeadline) {
            while (reorderBuffer.TryTake(
                currentTime,
                sampleBuffer.GetSampleCount(),
                renderQuantumSamples,
                forceDeadline,
                out VoiceFrame frame,
                out uint missingFrameCount,
                out uint intentionalSilenceSampleCount,
                out _)) {
                try {
                    DecodeFrame(frame, missingFrameCount, intentionalSilenceSampleCount);
                }
                catch {
                    decoder.ResetState();
                    reorderBuffer.ResetCurrentStream();
                    throw;
                }
                pendingFrameLevel = frame.Level;
                hasPendingFrameLevel = true;
                forceDeadline = false;
            }
        }

        internal int MixInto(Span<float> destination) {
            int destinationIndex = 0;
            int mixedSampleCount = 0;
            while (destinationIndex < destination.Length) {
                int samplesToRead = Math.Min(mixSamples.Length, destination.Length - destinationIndex);
                Span<float> samples = mixSamples.AsSpan(0, samplesToRead);
                int readSampleCount = sampleBuffer.Read(samples);
                for (int i = 0; i < samplesToRead; i++) {
                    destination[destinationIndex + i] += samples[i] * PlaybackVolume;
                }
                mixedSampleCount += readSampleCount;
                destinationIndex += samplesToRead;
            }
            return mixedSampleCount;
        }

        internal bool TryConsumeFrameLevel(out byte frameLevel) {
            frameLevel = pendingFrameLevel;
            if (!hasPendingFrameLevel) { return false; }

            hasPendingFrameLevel = false;
            return true;
        }

        internal bool HasTimedOut(double currentTime, float timeout) {
            return reorderBuffer.HasStarted && !isTimedOut && currentTime - lastFrameReceivedAt > timeout;
        }

        internal void MarkTimedOut() {
            isTimedOut = true;
        }

        private void DecodeFrame(VoiceFrame frame, uint missingFrameCount, uint intentionalSilenceSampleCount) {
            long trailingSilenceSampleCount = sampleBuffer.TakeTrailingSilenceSampleCount();
            long missingSampleCount = (long)missingFrameCount * frame.SampleCount;
            long totalGapSampleCount = missingSampleCount + intentionalSilenceSampleCount;
            long remainingGapSampleCount = Math.Max(0L, totalGapSampleCount - trailingSilenceSampleCount);
            if (intentionalSilenceSampleCount > 0) {
                if (trailingSilenceSampleCount == 0L && missingFrameCount <= maxConcealedFrames) {
                    for (uint i = 0; i < missingFrameCount; i++) {
                        sampleBuffer.Write(decoder.Conceal(frame.SampleCount));
                    }
                    remainingGapSampleCount -= missingSampleCount;
                }

                decoder.ResetState();
                sampleBuffer.WriteSilence((int)Math.Min(remainingGapSampleCount, sampleBuffer.Capacity));
                sampleBuffer.Write(decoder.Decode(frame, false));
                return;
            }

            if (missingFrameCount > maxConcealedFrames || trailingSilenceSampleCount > 0L) {
                decoder.ResetState();
                sampleBuffer.WriteSilence((int)Math.Min(remainingGapSampleCount, sampleBuffer.Capacity));
            }
            else if (missingFrameCount > 0) {
                for (uint i = 1; i < missingFrameCount; i++) {
                    sampleBuffer.Write(decoder.Conceal(frame.SampleCount));
                }
                sampleBuffer.Write(decoder.Decode(frame, true));
            }

            sampleBuffer.Write(decoder.Decode(frame, false));
        }

        private void RestartEncodedStream(VoiceFrame frame, double receivedAt) {
            decoder.ResetState();
            if (reorderBuffer.BeginStream(frame, receivedAt) != EncodedFrameReorderBuffer.Arrival.Accepted) { return; }
            lastFrameReceivedAt = receivedAt;
            isTimedOut = false;
        }

    }

}
