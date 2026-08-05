using System;

namespace Lockyaw.VoiceChat {

    internal sealed class BroadcastPlaybackStream : IDisposable {

        internal int BufferedSamples => sampleBuffer.GetSampleCount();
        internal double LastFrameReceivedAt => lastFrameReceivedAt;
        internal bool IsActive => reorderBuffer.HasStarted;

        private const int MAXIMUM_PENDING_ENCODED_FRAMES = 16;
        private const int MIX_BUFFER_SAMPLES = 4096;
        private const uint MAXIMUM_SEQUENCE_ADVANCE = 1024;

        private readonly VoiceDecoderSession decoder = new();
        private readonly EncodedFrameReorderBuffer reorderBuffer;
        private readonly SampleRingBuffer sampleBuffer;
        private readonly float[] mixSamples = new float[MIX_BUFFER_SAMPLES];
        private readonly int maxConcealedFrames;
        private double lastFrameReceivedAt;
        private byte pendingFrameLevel;
        private bool hasPendingFrameLevel;

        internal BroadcastPlaybackStream(
            int maxBufferSamples,
            int prebufferSamples,
            int maxConcealedFrames,
            int maximumReorderDelayMilliseconds) {
            sampleBuffer = new SampleRingBuffer(maxBufferSamples, prebufferSamples);
            reorderBuffer = new(MAXIMUM_PENDING_ENCODED_FRAMES, maximumReorderDelayMilliseconds, MAXIMUM_SEQUENCE_ADVANCE);
            this.maxConcealedFrames = maxConcealedFrames;
        }

        public void Dispose() {
            decoder.Dispose();
            reorderBuffer.Clear();
            sampleBuffer.Clear();
            hasPendingFrameLevel = false;
        }

        internal void Clear() {
            sampleBuffer.Clear();
            decoder.ResetState();
            reorderBuffer.Clear();
            hasPendingFrameLevel = false;
        }

        internal void ResetPlayback() {
            sampleBuffer.Clear();
            decoder.ResetState();
            reorderBuffer.ResetCurrentStream();
            hasPendingFrameLevel = false;
        }

        internal void Receive(VoiceFrame frame, double receivedAt, int audioCallbackSamples) {
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
                Process(receivedAt, audioCallbackSamples, true);
                arrival = reorderBuffer.Enqueue(frame, receivedAt);
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.CapacityReached) { return; }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Discontinuity) {
                RestartEncodedStream(frame, receivedAt);
                return;
            }

            lastFrameReceivedAt = receivedAt;
            Process(receivedAt, audioCallbackSamples, reorderBuffer.RequiresForcedDeadline);
        }

        internal void Process(double currentTime, int audioCallbackSamples, bool forceDeadline) {
            while (reorderBuffer.TryTake(
                currentTime,
                sampleBuffer.GetSampleCount(),
                audioCallbackSamples,
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

        internal void MixInto(Span<float> destination) {
            int destinationIndex = 0;
            while (destinationIndex < destination.Length) {
                int samplesToRead = Math.Min(mixSamples.Length, destination.Length - destinationIndex);
                Span<float> samples = mixSamples.AsSpan(0, samplesToRead);
                sampleBuffer.Read(samples);
                for (int i = 0; i < samplesToRead; i++) {
                    destination[destinationIndex + i] += samples[i];
                }
                destinationIndex += samplesToRead;
            }
        }

        internal bool TryConsumeFrameLevel(out byte frameLevel) {
            frameLevel = pendingFrameLevel;
            if (!hasPendingFrameLevel) { return false; }

            hasPendingFrameLevel = false;
            return true;
        }

        internal bool HasTimedOut(double currentTime, float timeout) {
            return reorderBuffer.HasStarted && currentTime - lastFrameReceivedAt > timeout;
        }

        private void DecodeFrame(VoiceFrame frame, uint missingFrameCount, uint intentionalSilenceSampleCount) {
            long trailingSilenceSampleCount = sampleBuffer.TakeTrailingSilenceSampleCount();
            if (intentionalSilenceSampleCount > 0) {
                if (missingFrameCount <= maxConcealedFrames) {
                    for (uint i = 0; i < missingFrameCount; i++) {
                        sampleBuffer.Write(decoder.Conceal(frame.SampleCount));
                    }
                }

                decoder.ResetState();
                long remainingSilenceSampleCount = Math.Max(0L, intentionalSilenceSampleCount - trailingSilenceSampleCount);
                sampleBuffer.WriteSilence((int)Math.Min(remainingSilenceSampleCount, sampleBuffer.Capacity));
                sampleBuffer.Write(decoder.Decode(frame, false));
                return;
            }

            if (missingFrameCount > maxConcealedFrames) {
                decoder.ResetState();
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
        }

    }

}
