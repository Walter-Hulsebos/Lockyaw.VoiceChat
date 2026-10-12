using System;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class WorldAudioHistory {

        internal const long UNASSIGNED_SAMPLE_POSITION = -1;
        internal long PublishedSamplePosition => Volatile.Read(ref publishedWritePosition);

        private const int HISTORY_SAMPLE_COUNT = 19200;

        private readonly float[] samples = new float[HISTORY_SAMPLE_COUNT];
        private long nextWritePosition;
        private long publishedWritePosition;
        private long sourceFramePosition;
        private double nextOutputSourcePosition;
        private float previousSourceSample;
        private int sourceSampleRate;
        private int writeVersion;
        private int requestedResetVersion;
        private int appliedResetVersion;
        private bool hasPreviousSourceSample;

        internal void RequestReset() {
            Interlocked.Increment(ref requestedResetVersion);
        }

        internal void WriteInterleaved(float[] interleavedSamples, int channelCount, int sampleRate) {
            if (interleavedSamples == null || channelCount <= 0 || sampleRate <= 0) { return; }
            int resetVersion = Volatile.Read(ref requestedResetVersion);
            if (Volatile.Read(ref appliedResetVersion) != resetVersion || sourceSampleRate != sampleRate) {
                ResetCapture(sampleRate);
            }
            int sourceFrameCount = interleavedSamples.Length / channelCount;
            if (sourceFrameCount > 0) { WriteResampled(interleavedSamples, channelCount, sourceFrameCount); }
            Volatile.Write(ref appliedResetVersion, resetVersion);
        }

        internal bool Read(ref long nextSamplePosition, Span<float> destination, int maximumBufferedFrameCount) {
            destination.Clear();
            if (destination.Length == 0 ||
                Volatile.Read(ref appliedResetVersion) != Volatile.Read(ref requestedResetVersion)) {
                return false;
            }

            int initialWriteVersion = Volatile.Read(ref writeVersion);
            if ((initialWriteVersion & 1) != 0) { return false; }

            long currentWritePosition = Volatile.Read(ref publishedWritePosition);
            if (nextSamplePosition == UNASSIGNED_SAMPLE_POSITION || nextSamplePosition > currentWritePosition) {
                nextSamplePosition = currentWritePosition;
                return false;
            }

            long oldestAvailablePosition = Math.Max(0L, currentWritePosition - samples.Length);
            if (nextSamplePosition < oldestAvailablePosition) {
                nextSamplePosition = oldestAvailablePosition;
            }

            long maximumBufferedSampleCount = (long)destination.Length * Math.Max(1, maximumBufferedFrameCount);
            if (currentWritePosition - nextSamplePosition > maximumBufferedSampleCount) {
                nextSamplePosition = currentWritePosition - maximumBufferedSampleCount;
            }
            if (currentWritePosition - nextSamplePosition < destination.Length) { return false; }

            int readIndex = (int)(nextSamplePosition % samples.Length);
            int firstCopyLength = Math.Min(destination.Length, samples.Length - readIndex);
            samples.AsSpan(readIndex, firstCopyLength).CopyTo(destination.Slice(0, firstCopyLength));

            int secondCopyLength = destination.Length - firstCopyLength;
            if (secondCopyLength > 0) {
                samples.AsSpan(0, secondCopyLength).CopyTo(destination.Slice(firstCopyLength, secondCopyLength));
            }

            Thread.MemoryBarrier();
            if (Volatile.Read(ref writeVersion) != initialWriteVersion) {
                destination.Clear();
                return false;
            }

            nextSamplePosition += destination.Length;
            return true;
        }

        private void WriteResampled(float[] interleavedSamples, int channelCount, int sourceFrameCount) {
            double sourceFramesPerOutputSample = sourceSampleRate / (double)VoiceEncoderSession.SAMPLE_RATE;
            int sourceFrameIndex = 0;

            BeginWrite();
            try {
                long refreshedWritePosition = nextWritePosition;
                if (!hasPreviousSourceSample) {
                    previousSourceSample = GetMonoSample(interleavedSamples, 0, channelCount);
                    sourceFramePosition = 0;
                    nextOutputSourcePosition = 0d;
                    hasPreviousSourceSample = true;
                    WriteSample(previousSourceSample, ref refreshedWritePosition);
                    nextOutputSourcePosition += sourceFramesPerOutputSample;
                    sourceFrameIndex = 1;
                }

                for (; sourceFrameIndex < sourceFrameCount; sourceFrameIndex++) {
                    float currentSourceSample = GetMonoSample(interleavedSamples, sourceFrameIndex, channelCount);
                    long currentSourceFramePosition = sourceFramePosition + 1;
                    while (nextOutputSourcePosition <= currentSourceFramePosition) {
                        float interpolation = (float)(nextOutputSourcePosition - sourceFramePosition);
                        float outputSample = previousSourceSample + (currentSourceSample - previousSourceSample) * interpolation;
                        WriteSample(outputSample, ref refreshedWritePosition);
                        nextOutputSourcePosition += sourceFramesPerOutputSample;
                    }

                    previousSourceSample = currentSourceSample;
                    sourceFramePosition = currentSourceFramePosition;
                }

                nextWritePosition = refreshedWritePosition;
                Volatile.Write(ref publishedWritePosition, refreshedWritePosition);
            }
            finally {
                EndWrite();
            }
        }

        private void ResetCapture(int currentSourceSampleRate) {
            BeginWrite();
            try {
                nextWritePosition = 0;
                Volatile.Write(ref publishedWritePosition, 0);
                sourceFramePosition = 0;
                nextOutputSourcePosition = 0d;
                previousSourceSample = 0f;
                sourceSampleRate = currentSourceSampleRate;
                hasPreviousSourceSample = false;
            }
            finally {
                EndWrite();
            }
        }

        private void WriteSample(float sample, ref long writePosition) {
            samples[(int)(writePosition % samples.Length)] = float.IsFinite(sample) ? sample : 0f;
            writePosition++;
        }

        private void BeginWrite() {
            Interlocked.Increment(ref writeVersion);
        }

        private void EndWrite() {
            Interlocked.Increment(ref writeVersion);
        }

        private static float GetMonoSample(float[] interleavedSamples, int sourceFrameIndex, int channelCount) {
            int firstSampleIndex = sourceFrameIndex * channelCount;
            float mixedSample = 0f;
            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++) {
                mixedSample += interleavedSamples[firstSampleIndex + channelIndex];
            }
            return mixedSample / channelCount;
        }

    }

}
