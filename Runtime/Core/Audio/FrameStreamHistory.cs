namespace Lockyaw.VoiceChat {

    internal struct FrameStreamHistory {

        private struct StreamIdentity {

            internal ulong SourceClientId;
            internal uint StreamId;

            internal StreamIdentity(ulong sourceClientId, uint streamId) {
                SourceClientId = sourceClientId;
                StreamId = streamId;
            }

            internal bool Matches(ulong sourceClientId, uint streamId) => SourceClientId == sourceClientId && StreamId == streamId;

        }

        internal ulong CurrentSourceClientId => currentStream.SourceClientId;
        internal bool HasCurrentStream => hasCurrentStream;

        private StreamIdentity firstRetired;
        private StreamIdentity secondRetired;
        private StreamIdentity thirdRetired;
        private StreamIdentity fourthRetired;
        private StreamIdentity currentStream;
        private int retiredCount;
        private bool hasCurrentStream;

        internal void Clear() {
            this = default;
        }

        internal bool TryBegin(ulong sourceClientId, uint streamId) {
            if (IsCurrent(sourceClientId, streamId)) { return true; }
            if (IsRetired(sourceClientId, streamId)) { return false; }
            RetireCurrent();
            currentStream = new(sourceClientId, streamId);
            hasCurrentStream = true;
            return true;
        }

        internal void RetireCurrent() {
            if (!hasCurrentStream) { return; }
            fourthRetired = thirdRetired;
            thirdRetired = secondRetired;
            secondRetired = firstRetired;
            firstRetired = currentStream;
            if (retiredCount < 4) { retiredCount++; }
            currentStream = default;
            hasCurrentStream = false;
        }

        internal void RetireCurrent(ulong sourceClientId) {
            if (hasCurrentStream && currentStream.SourceClientId == sourceClientId) { RetireCurrent(); }
        }

        internal bool IsCurrent(ulong sourceClientId, uint streamId) => hasCurrentStream && currentStream.Matches(sourceClientId, streamId);

        internal bool IsRetired(ulong sourceClientId, uint streamId) {
            return retiredCount > 0 && firstRetired.Matches(sourceClientId, streamId) ||
                   retiredCount > 1 && secondRetired.Matches(sourceClientId, streamId) ||
                   retiredCount > 2 && thirdRetired.Matches(sourceClientId, streamId) ||
                   retiredCount > 3 && fourthRetired.Matches(sourceClientId, streamId);
        }

    }

}
