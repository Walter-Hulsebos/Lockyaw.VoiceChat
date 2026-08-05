namespace Lockyaw.VoiceChat {

    internal struct FrameReplayWindow {

        private const int WINDOW_BITS = 64;

        private FrameStreamHistory streamHistory;
        private uint newestSequence;
        private ulong receivedSequences;
        private bool hasSequence;

        internal void Clear() {
            streamHistory.Clear();
            ResetSequence();
        }

        internal void ResetSequence() {
            newestSequence = 0;
            receivedSequences = 0;
            hasSequence = false;
        }

        internal bool TryAccept(uint sequence, uint maximumSequenceAdvance) {
            return TryAccept(VoiceFrame.UNASSIGNED_SOURCE_CLIENT_ID, 0, sequence, maximumSequenceAdvance, false);
        }

        internal bool TryAccept(uint streamId, uint sequence, uint maximumSequenceAdvance) {
            return TryAccept(VoiceFrame.UNASSIGNED_SOURCE_CLIENT_ID, streamId, sequence, maximumSequenceAdvance, false);
        }

        internal bool TryAccept(uint streamId, uint sequence, uint maximumSequenceAdvance, bool allowForwardRebase) {
            return TryAccept(VoiceFrame.UNASSIGNED_SOURCE_CLIENT_ID, streamId, sequence, maximumSequenceAdvance, allowForwardRebase);
        }

        internal bool TryAccept(
            ulong sourceClientId,
            uint streamId,
            uint sequence,
            uint maximumSequenceAdvance,
            bool allowForwardRebase) {
            if (!streamHistory.IsCurrent(sourceClientId, streamId)) {
                if (streamHistory.IsRetired(sourceClientId, streamId)) { return false; }
                if (hasSequence && sourceClientId == streamHistory.CurrentSourceClientId) {
                    int streamSequenceComparison = FrameSequence.Compare(sequence, newestSequence);
                    if (streamSequenceComparison <= 0) { return false; }
                    if (FrameSequence.GetDistance(sequence, newestSequence) > maximumSequenceAdvance && !allowForwardRebase) {
                        return false;
                    }
                }
                if (!streamHistory.TryBegin(sourceClientId, streamId)) { return false; }
                ResetSequence();
            }

            if (!hasSequence) {
                newestSequence = sequence;
                receivedSequences = 1;
                hasSequence = true;
                return true;
            }

            int sequenceComparison = FrameSequence.Compare(sequence, newestSequence);
            if (sequenceComparison > 0) {
                uint sequenceAdvance = FrameSequence.GetDistance(sequence, newestSequence);
                if (sequenceAdvance > maximumSequenceAdvance) {
                    if (!allowForwardRebase) { return false; }
                    ResetSequence();
                    newestSequence = sequence;
                    receivedSequences = 1;
                    hasSequence = true;
                    return true;
                }

                receivedSequences = sequenceAdvance >= WINDOW_BITS
                    ? 1
                    : receivedSequences << (int)sequenceAdvance | 1;
                newestSequence = sequence;
                return true;
            }
            if (sequenceComparison == 0) { return false; }

            uint sequenceAge = FrameSequence.GetDistance(newestSequence, sequence);
            if (sequenceAge >= WINDOW_BITS) { return false; }

            ulong sequenceBit = 1UL << (int)sequenceAge;
            if ((receivedSequences & sequenceBit) != 0) { return false; }
            receivedSequences |= sequenceBit;
            return true;
        }

    }

}
