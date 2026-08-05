using System;
using System.Collections.Generic;

namespace Lockyaw.VoiceChat {

    internal sealed class EncodedFrameReorderBuffer {

        internal enum Arrival {
            Invalid = 0,
            Accepted = 1,
            Reordered = 2,
            Duplicate = 3,
            Late = 4,
            Discontinuity = 5,
            SequenceAdvanceRejected = 6,
            StreamFormatRejected = 7,
            CapacityReached = 8,
            SampleTimelineRejected = 9
        }

        private readonly struct PendingFrame {

            internal VoiceFrame Frame { get; }
            internal double ReceivedAt { get; }

            internal PendingFrame(VoiceFrame frame, double receivedAt) {
                Frame = frame;
                ReceivedAt = receivedAt;
            }

        }

        internal int Count => pendingFrameCount;
        internal bool HasStarted => hasActiveOrdering;
        internal bool RequiresForcedDeadline => pendingFrameCount > maximumPendingFrames;

        private readonly PendingFrame[] pendingFrames;
        private readonly Dictionary<ulong, uint> sourceHighWaterSequences = new(2);
        private readonly int maximumPendingFrames;
        private readonly int minimumReorderReserveSamples;
        private readonly double maximumReorderDelaySeconds;
        private readonly uint maximumSequenceAdvance;
        private FrameStreamHistory streamHistory;
        private int pendingFrameCount;
        private uint expectedSequence;
        private uint expectedSampleTimestamp;
        private uint newestReceivedSequence;
        private ushort streamSampleCount;
        private double startupStartedAt;
        private double gapStartedAt;
        private bool hasExpectedSequence;
        private bool hasNewestReceivedSequence;
        private bool hasSequenceHistory;
        private bool hasTimelineHistory;
        private bool hasEmittedFrame;
        private bool hasGapDeadline;
        private bool hasActiveOrdering;
        private bool canRebaseForward;

        internal EncodedFrameReorderBuffer(int capacity, int maximumReorderDelayMilliseconds, uint maximumSequenceAdvance) {
            if (capacity <= 0) {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            pendingFrames = new PendingFrame[capacity + 1];
            maximumPendingFrames = capacity;
            maximumReorderDelaySeconds = Math.Max(0, maximumReorderDelayMilliseconds) / 1000d;
            minimumReorderReserveSamples = (int)Math.Ceiling(maximumReorderDelaySeconds * VoiceEncoderSession.SAMPLE_RATE);
            this.maximumSequenceAdvance = maximumSequenceAdvance;
        }

        internal void Clear() {
            streamHistory.Clear();
            sourceHighWaterSequences.Clear();
            ResetOrdering();
        }

        internal void ResetCurrentStream() {
            ClearPendingFrames();
            newestReceivedSequence = 0;
            startupStartedAt = 0d;
            gapStartedAt = 0d;
            hasNewestReceivedSequence = false;
            hasTimelineHistory = false;
            hasEmittedFrame = false;
            hasGapDeadline = false;
            hasActiveOrdering = false;
            canRebaseForward = hasExpectedSequence;
        }

        internal void RetireCurrentStream() {
            streamHistory.RetireCurrent();
            ResetOrdering();
        }

        internal void RetireCurrentStream(ulong sourceClientId) {
            if (!streamHistory.HasCurrentStream || streamHistory.CurrentSourceClientId != sourceClientId) { return; }

            streamHistory.RetireCurrent(sourceClientId);
            ResetOrdering();
        }

        internal Arrival BeginStream(VoiceFrame frame, double receivedAt) {
            if (!frame.IsValid) { return Arrival.Invalid; }
            if (!streamHistory.IsCurrent(frame.SourceClientId, frame.StreamId)) {
                Arrival transition = GetStreamTransition(frame);
                if (transition != Arrival.Discontinuity) { return transition; }
            }
            if (!streamHistory.TryBegin(frame.SourceClientId, frame.StreamId)) { return Arrival.Late; }

            ResetOrdering();
            return Enqueue(frame, receivedAt);
        }

        internal Arrival Enqueue(VoiceFrame frame, double receivedAt) {
            if (!frame.IsValid) { return Arrival.Invalid; }

            if (!hasExpectedSequence) {
                if (!streamHistory.TryBegin(frame.SourceClientId, frame.StreamId)) { return Arrival.Late; }
                BeginOrdering(frame, receivedAt);
            }
            else {
                if (!streamHistory.IsCurrent(frame.SourceClientId, frame.StreamId)) {
                    return GetStreamTransition(frame);
                }
                if (frame.SampleCount != streamSampleCount) { return Arrival.StreamFormatRejected; }

                int expectedComparison = FrameSequence.Compare(frame.Sequence, expectedSequence);
                if (expectedComparison < 0) {
                    if (hasSequenceHistory || FrameSequence.GetDistance(expectedSequence, frame.Sequence) >= maximumPendingFrames) {
                        return Arrival.Late;
                    }
                    expectedSequence = frame.Sequence;
                    expectedSampleTimestamp = frame.SampleTimestamp;
                }
                else if (FrameSequence.GetDistance(frame.Sequence, expectedSequence) > maximumSequenceAdvance) {
                    if (!canRebaseForward) { return Arrival.SequenceAdvanceRejected; }

                    ResetOrdering();
                    BeginOrdering(frame, receivedAt);
                }
            }

            if (!HasCompatibleSampleTimeline(frame)) { return Arrival.SampleTimelineRejected; }

            for (int i = 0; i < pendingFrameCount; i++) {
                if (pendingFrames[i].Frame.Sequence == frame.Sequence) { return Arrival.Duplicate; }
            }
            if (pendingFrameCount >= pendingFrames.Length) { return Arrival.CapacityReached; }

            bool wasReordered = hasNewestReceivedSequence && FrameSequence.Compare(frame.Sequence, newestReceivedSequence) < 0;
            if (!hasNewestReceivedSequence || FrameSequence.Compare(frame.Sequence, newestReceivedSequence) > 0) {
                newestReceivedSequence = frame.Sequence;
                hasNewestReceivedSequence = true;
            }

            pendingFrames[pendingFrameCount] = new PendingFrame(frame, receivedAt);
            pendingFrameCount++;
            RecordSourceSequence(frame);
            if (!hasActiveOrdering) {
                startupStartedAt = receivedAt;
                hasActiveOrdering = true;
            }
            canRebaseForward = false;
            return wasReordered ? Arrival.Reordered : Arrival.Accepted;
        }

        internal bool TryTake(
            double currentTime,
            int bufferedSamples,
            int audioCallbackSamples,
            bool forceDeadline,
            out VoiceFrame frame,
            out uint missingFrameCount,
            out uint intentionalSilenceSampleCount,
            out bool gapFinalized) {
            frame = default;
            missingFrameCount = 0;
            intentionalSilenceSampleCount = 0;
            gapFinalized = false;
            if (pendingFrameCount == 0) { return false; }

            int pendingFrameIndex = GetNextFrameIndex();
            PendingFrame pendingFrame = pendingFrames[pendingFrameIndex];
            missingFrameCount = FrameSequence.GetDistance(pendingFrame.Frame.Sequence, expectedSequence);
            if (hasEmittedFrame && missingFrameCount > 0 && !hasGapDeadline) {
                gapStartedAt = GetEarliestPendingReceivedAt();
                hasGapDeadline = true;
            }
            if (!CanRelease(currentTime, bufferedSamples, audioCallbackSamples, forceDeadline, missingFrameCount)) {
                return false;
            }

            frame = pendingFrame.Frame;
            if (hasTimelineHistory) {
                uint timelineSampleAdvance = FrameSequence.GetDistance(frame.SampleTimestamp, expectedSampleTimestamp);
                uint missingPacketSamples = missingFrameCount * frame.SampleCount;
                intentionalSilenceSampleCount = timelineSampleAdvance - missingPacketSamples;
            }
            gapFinalized = missingFrameCount > 0;
            RemoveFrame(pendingFrameIndex);
            expectedSequence = unchecked(frame.Sequence + 1);
            expectedSampleTimestamp = unchecked(frame.SampleTimestamp + frame.SampleCount);
            hasSequenceHistory = true;
            hasTimelineHistory = true;
            hasEmittedFrame = true;
            hasGapDeadline = false;
            return true;
        }

        internal bool HasCurrentSource(ulong sourceClientId) {
            return streamHistory.HasCurrentStream && streamHistory.CurrentSourceClientId == sourceClientId;
        }

        private void ResetOrdering() {
            ClearPendingFrames();
            expectedSequence = 0;
            expectedSampleTimestamp = 0;
            newestReceivedSequence = 0;
            streamSampleCount = 0;
            startupStartedAt = 0d;
            gapStartedAt = 0d;
            hasExpectedSequence = false;
            hasNewestReceivedSequence = false;
            hasSequenceHistory = false;
            hasTimelineHistory = false;
            hasEmittedFrame = false;
            hasGapDeadline = false;
            hasActiveOrdering = false;
            canRebaseForward = false;
        }

        private void ClearPendingFrames() {
            Array.Clear(pendingFrames, 0, pendingFrameCount);
            pendingFrameCount = 0;
        }

        private void BeginOrdering(VoiceFrame frame, double receivedAt) {
            expectedSequence = frame.Sequence;
            expectedSampleTimestamp = frame.SampleTimestamp;
            streamSampleCount = frame.SampleCount;
            startupStartedAt = receivedAt;
            hasExpectedSequence = true;
            hasActiveOrdering = true;
        }

        private Arrival GetStreamTransition(VoiceFrame frame) {
            if (streamHistory.IsRetired(frame.SourceClientId, frame.StreamId)) { return Arrival.Late; }
            if (sourceHighWaterSequences.TryGetValue(frame.SourceClientId, out uint sourceHighWaterSequence) &&
                FrameSequence.Compare(frame.Sequence, sourceHighWaterSequence) <= 0) {
                return Arrival.Late;
            }
            if (!hasExpectedSequence || frame.SourceClientId != streamHistory.CurrentSourceClientId) {
                return Arrival.Discontinuity;
            }

            int sequenceComparison = FrameSequence.Compare(frame.Sequence, expectedSequence);
            if (sequenceComparison < 0) { return Arrival.Late; }
            if (FrameSequence.GetDistance(frame.Sequence, expectedSequence) > maximumSequenceAdvance && !canRebaseForward) {
                return Arrival.SequenceAdvanceRejected;
            }
            return Arrival.Discontinuity;
        }

        private void RecordSourceSequence(VoiceFrame frame) {
            if (!sourceHighWaterSequences.TryGetValue(frame.SourceClientId, out uint sourceHighWaterSequence) ||
                FrameSequence.Compare(frame.Sequence, sourceHighWaterSequence) > 0) {
                sourceHighWaterSequences[frame.SourceClientId] = frame.Sequence;
            }
        }

        private bool HasCompatibleSampleTimeline(VoiceFrame frame) {
            if (hasTimelineHistory) {
                uint packetAdvance = FrameSequence.GetDistance(frame.Sequence, expectedSequence);
                uint sampleAdvance = FrameSequence.GetDistance(frame.SampleTimestamp, expectedSampleTimestamp);
                if (FrameSequence.Compare(frame.Sequence, expectedSequence) >= 0 &&
                    (FrameSequence.Compare(frame.SampleTimestamp, expectedSampleTimestamp) < 0 ||
                     sampleAdvance < packetAdvance * streamSampleCount)) {
                    return false;
                }
            }

            for (int i = 0; i < pendingFrameCount; i++) {
                VoiceFrame pendingFrame = pendingFrames[i].Frame;
                int sequenceComparison = FrameSequence.Compare(frame.Sequence, pendingFrame.Sequence);
                if (sequenceComparison == 0) { continue; }

                VoiceFrame earlierFrame = sequenceComparison < 0 ? frame : pendingFrame;
                VoiceFrame laterFrame = sequenceComparison < 0 ? pendingFrame : frame;
                uint packetAdvance = FrameSequence.GetDistance(laterFrame.Sequence, earlierFrame.Sequence);
                uint sampleAdvance = FrameSequence.GetDistance(laterFrame.SampleTimestamp, earlierFrame.SampleTimestamp);
                if (FrameSequence.Compare(laterFrame.SampleTimestamp, earlierFrame.SampleTimestamp) < 0 ||
                    sampleAdvance < packetAdvance * streamSampleCount) {
                    return false;
                }
            }
            return true;
        }

        private void RemoveFrame(int frameIndex) {
            pendingFrameCount--;
            if (frameIndex < pendingFrameCount) {
                pendingFrames[frameIndex] = pendingFrames[pendingFrameCount];
            }
            pendingFrames[pendingFrameCount] = default;
        }

        private int GetNextFrameIndex() {
            int nextFrameIndex = 0;
            uint nextFrameDistance = FrameSequence.GetDistance(pendingFrames[0].Frame.Sequence, expectedSequence);
            for (int i = 1; i < pendingFrameCount; i++) {
                uint frameDistance = FrameSequence.GetDistance(pendingFrames[i].Frame.Sequence, expectedSequence);
                if (frameDistance >= nextFrameDistance) { continue; }
                nextFrameIndex = i;
                nextFrameDistance = frameDistance;
            }
            return nextFrameIndex;
        }

        private double GetEarliestPendingReceivedAt() {
            double earliestReceivedAt = pendingFrames[0].ReceivedAt;
            for (int i = 1; i < pendingFrameCount; i++) {
                earliestReceivedAt = Math.Min(earliestReceivedAt, pendingFrames[i].ReceivedAt);
            }
            return earliestReceivedAt;
        }

        private bool CanRelease(
            double currentTime,
            int bufferedSamples,
            int audioCallbackSamples,
            bool forceDeadline,
            uint missingFrameCount) {
            if (forceDeadline || maximumReorderDelaySeconds <= 0d) { return true; }

            if (!hasEmittedFrame) {
                return currentTime - startupStartedAt >= maximumReorderDelaySeconds;
            }
            if (missingFrameCount == 0) { return true; }
            if (currentTime - gapStartedAt >= maximumReorderDelaySeconds) { return true; }
            int requiredReserveSamples = Math.Max(minimumReorderReserveSamples, Math.Max(0, audioCallbackSamples));
            return bufferedSamples < requiredReserveSamples;
        }

    }

}
