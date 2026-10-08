using System;

namespace Lockyaw.VoiceChat {

    internal readonly struct CircularAudioClipSchedule {

        internal long ClearStartSample { get; }
        internal int ClearSampleCount { get; }
        internal long SkippedSampleCount { get; }
        internal long WriteStartSample { get; }
        internal int WriteSampleCount { get; }

        internal CircularAudioClipSchedule(
            long clearStartSample,
            int clearSampleCount,
            long skippedSampleCount,
            long writeStartSample,
            int writeSampleCount) {
            ClearStartSample = clearStartSample;
            ClearSampleCount = clearSampleCount;
            SkippedSampleCount = skippedSampleCount;
            WriteStartSample = writeStartSample;
            WriteSampleCount = writeSampleCount;
        }

    }

    internal sealed class CircularAudioClipScheduler {

        internal int ScheduledLeadSamples => hasStarted
            ? (int)Math.Clamp(scheduledPosition - readPosition, 0L, clipSampleCount)
            : 0;
        internal bool HasStarted => hasStarted;

        private readonly int clipSampleCount;
        private readonly int targetLeadSamples;
        private long readPosition;
        private long scheduledPosition;
        private long clearedPosition;
        private bool hasStarted;

        internal CircularAudioClipScheduler(int clipSampleCount, int targetLeadSamples) {
            if (clipSampleCount <= 0) {
                throw new ArgumentOutOfRangeException(nameof(clipSampleCount));
            }
            if (targetLeadSamples <= 0 || targetLeadSamples >= clipSampleCount) {
                throw new ArgumentOutOfRangeException(nameof(targetLeadSamples));
            }

            this.clipSampleCount = clipSampleCount;
            this.targetLeadSamples = targetLeadSamples;
        }

        internal CircularAudioClipSchedule Start(long absolutePlaybackSample) {
            if (absolutePlaybackSample < 0L) {
                throw new ArgumentOutOfRangeException(nameof(absolutePlaybackSample));
            }

            readPosition = absolutePlaybackSample;
            scheduledPosition = absolutePlaybackSample + targetLeadSamples;
            clearedPosition = absolutePlaybackSample;
            hasStarted = true;
            return new CircularAudioClipSchedule(absolutePlaybackSample, 0, 0L, absolutePlaybackSample, targetLeadSamples);
        }

        internal CircularAudioClipSchedule Advance(long absolutePlaybackSample) {
            if (!hasStarted) {
                throw new InvalidOperationException("The circular audio clip scheduler has not started.");
            }
            if (absolutePlaybackSample < readPosition) {
                throw new ArgumentOutOfRangeException(nameof(absolutePlaybackSample));
            }

            long clearStartSample = clearedPosition;
            long clearSampleCount = absolutePlaybackSample - clearedPosition;
            if (clearSampleCount > clipSampleCount) {
                clearStartSample = absolutePlaybackSample - clipSampleCount;
                clearSampleCount = clipSampleCount;
            }

            long skippedSampleCount = Math.Max(0L, absolutePlaybackSample - scheduledPosition);
            long writeStartSample;
            int writeSampleCount;
            if (skippedSampleCount > 0L) {
                writeStartSample = absolutePlaybackSample;
                writeSampleCount = targetLeadSamples;
                scheduledPosition = absolutePlaybackSample + targetLeadSamples;
            }
            else {
                writeStartSample = scheduledPosition;
                long desiredScheduledPosition = absolutePlaybackSample + targetLeadSamples;
                writeSampleCount = (int)Math.Max(0L, desiredScheduledPosition - scheduledPosition);
                scheduledPosition += writeSampleCount;
            }

            readPosition = absolutePlaybackSample;
            clearedPosition = absolutePlaybackSample;
            return new CircularAudioClipSchedule(
                clearStartSample,
                (int)clearSampleCount,
                skippedSampleCount,
                writeStartSample,
                writeSampleCount);
        }

        internal void Reset() {
            readPosition = 0L;
            scheduledPosition = 0L;
            clearedPosition = 0L;
            hasStarted = false;
        }

    }

}
