using System;
using System.Threading;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("")]
    internal sealed class WorldAudioSourceTap : MonoBehaviour {

        internal const long UNASSIGNED_SAMPLE_POSITION = -1;

        private const int HISTORY_FRAME_COUNT = 4;
        private const int HISTORY_SAMPLE_COUNT = (int)VoiceFrameLength.FortyMilliseconds * HISTORY_FRAME_COUNT;

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
        private int readerCount;
        private bool hasPreviousSourceSample;

        private void Awake() {
            hideFlags = HideFlags.HideInInspector;
            enabled = false;
        }

        private void OnAudioFilterRead(float[] interleavedSamples, int channelCount) {
            if (interleavedSamples == null || interleavedSamples.Length == 0 || channelCount <= 0) { return; }

            int currentSourceSampleRate = AudioSettings.outputSampleRate;
            if (currentSourceSampleRate <= 0) { return; }

            int currentResetVersion = Volatile.Read(ref requestedResetVersion);
            bool shouldApplyReset = Volatile.Read(ref appliedResetVersion) != currentResetVersion;
            if (shouldApplyReset || sourceSampleRate != currentSourceSampleRate) {
                ResetCapture(currentSourceSampleRate);
            }

            int sourceFrameCount = interleavedSamples.Length / channelCount;
            if (sourceFrameCount > 0) {
                WriteResampled(interleavedSamples, channelCount, sourceFrameCount);
            }

            if (shouldApplyReset) {
                Volatile.Write(ref appliedResetVersion, currentResetVersion);
            }
        }

        internal static WorldAudioSourceTap Acquire(AudioSource audioSource, out long initialSamplePosition) {
            if (!audioSource.TryGetComponent(out WorldAudioSourceTap sourceTap)) {
                sourceTap = audioSource.gameObject.AddComponent<WorldAudioSourceTap>();
            }

            bool shouldReset = sourceTap.readerCount == 0;
            sourceTap.readerCount++;
            if (shouldReset) {
                initialSamplePosition = 0;
                Interlocked.Increment(ref sourceTap.requestedResetVersion);
                sourceTap.enabled = true;
            }
            else if (Volatile.Read(ref sourceTap.appliedResetVersion) != Volatile.Read(ref sourceTap.requestedResetVersion)) {
                initialSamplePosition = 0;
            }
            else {
                initialSamplePosition = Volatile.Read(ref sourceTap.publishedWritePosition);
            }
            return sourceTap;
        }

        internal void Release() {
            if (readerCount <= 0) { return; }

            readerCount--;
            if (readerCount == 0) {
                enabled = false;
            }
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
