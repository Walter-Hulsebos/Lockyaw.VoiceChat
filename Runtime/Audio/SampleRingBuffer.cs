using System;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class SampleRingBuffer {

        internal int Capacity => samples.Length;
        internal int PrebufferWaitCount => Volatile.Read(ref prebufferWaitCount);
        internal int RebufferCount => Volatile.Read(ref rebufferCount);
        internal int ConcurrentReadAbortCount => Volatile.Read(ref concurrentReadAbortCount);
        internal int PartialReadCount => Volatile.Read(ref partialReadCount);
        internal long OverflowedSampleCount => Interlocked.Read(ref overflowedSampleCount);

        private const int UNDERFLOW_FADE_SAMPLES = 64;

        private readonly float[] samples;
        private readonly int prebufferSamples;
        private long nextWritePosition;
        private long readPosition;
        private long publishedWritePosition;
        private long publishedReadPosition;
        private long discardBeforePosition;
        private int writeVersion;
        private int clearVersion;
        private int observedClearVersion;
        private int prebufferWaitCount;
        private int rebufferCount;
        private int concurrentReadAbortCount;
        private int partialReadCount;
        private long overflowedSampleCount;
        private long trailingSilenceSampleCount;
        private bool isPrimed;
        private bool shouldFadeIn = true;

        internal SampleRingBuffer(int capacity, int prebufferSamples) {
            if (capacity <= 0) {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            samples = new float[capacity];
            this.prebufferSamples = Math.Clamp(prebufferSamples, 0, capacity);
        }

        internal void Write(ReadOnlySpan<float> source) {
            int sourceStart = Math.Max(0, source.Length - samples.Length);
            ReadOnlySpan<float> retainedSamples = source.Slice(sourceStart);
            if (retainedSamples.Length == 0) { return; }

            long currentReadPosition = Volatile.Read(ref publishedReadPosition);
            long currentDiscardBeforePosition = Volatile.Read(ref discardBeforePosition);
            long effectiveReadPosition = Math.Max(currentReadPosition, currentDiscardBeforePosition);
            bool canOverwriteUnreadSamples = nextWritePosition + retainedSamples.Length - effectiveReadPosition > samples.Length;
            if (canOverwriteUnreadSamples) {
                BeginWrite();
            }
            try {
                int writeIndex = (int)(nextWritePosition % samples.Length);
                int firstCopyLength = Math.Min(retainedSamples.Length, samples.Length - writeIndex);
                retainedSamples.Slice(0, firstCopyLength).CopyTo(samples.AsSpan(writeIndex, firstCopyLength));

                int secondCopyLength = retainedSamples.Length - firstCopyLength;
                if (secondCopyLength > 0) {
                    retainedSamples.Slice(firstCopyLength, secondCopyLength).CopyTo(samples.AsSpan(0, secondCopyLength));
                }

                nextWritePosition += retainedSamples.Length;
                Volatile.Write(ref publishedWritePosition, nextWritePosition);
            }
            finally {
                if (canOverwriteUnreadSamples) {
                    EndWrite();
                }
            }
        }

        internal void WriteSilence(int sampleCount) {
            int retainedSampleCount = Math.Clamp(sampleCount, 0, samples.Length);
            if (retainedSampleCount == 0) { return; }

            long currentReadPosition = Volatile.Read(ref publishedReadPosition);
            long currentDiscardBeforePosition = Volatile.Read(ref discardBeforePosition);
            long effectiveReadPosition = Math.Max(currentReadPosition, currentDiscardBeforePosition);
            bool canOverwriteUnreadSamples = nextWritePosition + retainedSampleCount - effectiveReadPosition > samples.Length;
            if (canOverwriteUnreadSamples) {
                BeginWrite();
            }
            try {
                int writeIndex = (int)(nextWritePosition % samples.Length);
                int firstClearLength = Math.Min(retainedSampleCount, samples.Length - writeIndex);
                samples.AsSpan(writeIndex, firstClearLength).Clear();

                int secondClearLength = retainedSampleCount - firstClearLength;
                if (secondClearLength > 0) {
                    samples.AsSpan(0, secondClearLength).Clear();
                }

                nextWritePosition += retainedSampleCount;
                Volatile.Write(ref publishedWritePosition, nextWritePosition);
            }
            finally {
                if (canOverwriteUnreadSamples) {
                    EndWrite();
                }
            }
        }

        internal int Read(Span<float> destination) {
            destination.Clear();

            int initialWriteVersion = Volatile.Read(ref writeVersion);
            if ((initialWriteVersion & 1) != 0) {
                Interlocked.Increment(ref concurrentReadAbortCount);
                return 0;
            }

            int currentClearVersion = Volatile.Read(ref clearVersion);
            long currentWritePosition = Volatile.Read(ref publishedWritePosition);
            long currentDiscardBeforePosition = Volatile.Read(ref discardBeforePosition);
            long nextReadPosition = readPosition;
            bool nextIsPrimed = isPrimed;
            bool nextShouldFadeIn = shouldFadeIn;

            if (observedClearVersion != currentClearVersion) {
                nextReadPosition = currentDiscardBeforePosition;
                nextIsPrimed = false;
                nextShouldFadeIn = true;
            }
            bool wasPrimed = nextIsPrimed;

            long oldestAvailablePosition = Math.Max(currentDiscardBeforePosition, currentWritePosition - samples.Length);
            if (nextReadPosition < oldestAvailablePosition || nextReadPosition > currentWritePosition) {
                if (nextReadPosition < oldestAvailablePosition) {
                    Interlocked.Add(ref overflowedSampleCount, oldestAvailablePosition - nextReadPosition);
                }
                nextReadPosition = oldestAvailablePosition;
            }

            long availableSampleCount = currentWritePosition - nextReadPosition;
            int playbackReserveSamples = Math.Max(prebufferSamples, destination.Length);
            int requiredStartSamples = Math.Min(samples.Length, playbackReserveSamples + destination.Length);
            if (!nextIsPrimed && availableSampleCount < requiredStartSamples) {
                Interlocked.Increment(ref prebufferWaitCount);
                if (availableSampleCount == 0) {
                    Interlocked.Add(ref trailingSilenceSampleCount, destination.Length);
                }
                if (!IsWriteVersionStable(initialWriteVersion)) {
                    Interlocked.Increment(ref concurrentReadAbortCount);
                    return 0;
                }

                CommitRead(nextReadPosition, false, true, currentClearVersion);
                return 0;
            }

            if (wasPrimed && availableSampleCount < destination.Length) {
                Interlocked.Increment(ref rebufferCount);
            }

            nextIsPrimed = true;
            int samplesToRead = (int)Math.Min(destination.Length, availableSampleCount);
            if (samplesToRead < destination.Length) {
                Interlocked.Increment(ref partialReadCount);
                Interlocked.Add(ref trailingSilenceSampleCount, destination.Length - samplesToRead);
            }
            int readIndex = (int)(nextReadPosition % samples.Length);
            int firstCopyLength = Math.Min(samplesToRead, samples.Length - readIndex);
            samples.AsSpan(readIndex, firstCopyLength).CopyTo(destination.Slice(0, firstCopyLength));

            int secondCopyLength = samplesToRead - firstCopyLength;
            if (secondCopyLength > 0) {
                samples.AsSpan(0, secondCopyLength).CopyTo(destination.Slice(firstCopyLength, secondCopyLength));
            }

            if (nextShouldFadeIn && samplesToRead > 0) {
                ApplyFadeIn(destination.Slice(0, Math.Min(UNDERFLOW_FADE_SAMPLES, samplesToRead)));
                nextShouldFadeIn = false;
            }
            if (samplesToRead < destination.Length) {
                int fadeSampleCount = Math.Min(UNDERFLOW_FADE_SAMPLES, samplesToRead);
                ApplyFadeOut(destination.Slice(samplesToRead - fadeSampleCount, fadeSampleCount));
                nextShouldFadeIn = true;
            }

            nextReadPosition += samplesToRead;
            if (samplesToRead < destination.Length || nextReadPosition == currentWritePosition) {
                nextIsPrimed = false;
            }

            if (!IsWriteVersionStable(initialWriteVersion)) {
                Interlocked.Increment(ref concurrentReadAbortCount);
                destination.Clear();
                return 0;
            }

            CommitRead(nextReadPosition, nextIsPrimed, nextShouldFadeIn, currentClearVersion);
            return samplesToRead;
        }

        internal void Clear() {
            BeginWrite();
            try {
                Volatile.Write(ref discardBeforePosition, nextWritePosition);
                Interlocked.Increment(ref clearVersion);
                Interlocked.Exchange(ref trailingSilenceSampleCount, 0);
            }
            finally {
                EndWrite();
            }
        }

        internal int GetSampleCount() {
            long currentWritePosition = Volatile.Read(ref publishedWritePosition);
            long currentReadPosition = Volatile.Read(ref publishedReadPosition);
            long currentDiscardBeforePosition = Volatile.Read(ref discardBeforePosition);
            long effectiveReadPosition = Math.Max(currentReadPosition, currentDiscardBeforePosition);
            long availableSampleCount = currentWritePosition - effectiveReadPosition;
            return (int)Math.Clamp(availableSampleCount, 0L, samples.Length);
        }

        internal long TakeTrailingSilenceSampleCount() => Interlocked.Exchange(ref trailingSilenceSampleCount, 0);

        internal void ResetDiagnostics() {
            Interlocked.Exchange(ref prebufferWaitCount, 0);
            Interlocked.Exchange(ref rebufferCount, 0);
            Interlocked.Exchange(ref concurrentReadAbortCount, 0);
            Interlocked.Exchange(ref partialReadCount, 0);
            Interlocked.Exchange(ref overflowedSampleCount, 0);
        }

        private void BeginWrite() {
            Interlocked.Increment(ref writeVersion);
        }

        private void EndWrite() {
            Interlocked.Increment(ref writeVersion);
        }

        private void CommitRead(long nextReadPosition, bool nextIsPrimed, bool nextShouldFadeIn, int currentClearVersion) {
            readPosition = nextReadPosition;
            isPrimed = nextIsPrimed;
            shouldFadeIn = nextShouldFadeIn;
            observedClearVersion = currentClearVersion;
            Volatile.Write(ref publishedReadPosition, nextReadPosition);
        }

        private static void ApplyFadeIn(Span<float> outputSamples) {
            for (int i = 0; i < outputSamples.Length; i++) {
                outputSamples[i] *= (i + 1f) / outputSamples.Length;
            }
        }

        private static void ApplyFadeOut(Span<float> outputSamples) {
            for (int i = 0; i < outputSamples.Length; i++) {
                outputSamples[i] *= (outputSamples.Length - i - 1f) / outputSamples.Length;
            }
        }

        private bool IsWriteVersionStable(int initialWriteVersion) {
            Thread.MemoryBarrier();
            return Volatile.Read(ref writeVersion) == initialWriteVersion;
        }

    }

}
