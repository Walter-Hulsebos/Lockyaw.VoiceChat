using Unity.Collections;

namespace Lockyaw.VoiceChat {

    internal struct FrameStreamHistory {

        private struct StreamIdentity {

            internal ulong SourceClientId;
            internal uint StreamId;

            internal StreamIdentity(ulong sourceClientId, uint streamId) {
                SourceClientId = sourceClientId;
                StreamId = streamId;
            }

            internal bool Matches(ulong sourceClientId, uint streamId) {
                return SourceClientId == sourceClientId && StreamId == streamId;
            }

        }

        internal ulong CurrentSourceClientId => currentStream.SourceClientId;
        internal bool HasCurrentStream => hasCurrentStream;

        private const int MAXIMUM_RETIRED_STREAMS = 4;

        private FixedList128Bytes<StreamIdentity> retiredStreams;
        private StreamIdentity currentStream;
        private bool hasCurrentStream;

        internal void Clear() {
            retiredStreams.Clear();
            currentStream = default;
            hasCurrentStream = false;
        }

        internal bool TryBegin(ulong sourceClientId, uint streamId) {
            if (IsCurrent(sourceClientId, streamId)) { return true; }
            if (IsRetired(sourceClientId, streamId)) { return false; }

            if (hasCurrentStream) {
                RetireCurrent();
            }
            currentStream = new(sourceClientId, streamId);
            hasCurrentStream = true;
            return true;
        }

        internal void RetireCurrent() {
            if (!hasCurrentStream) { return; }

            AddRetiredStream(currentStream);
            currentStream = default;
            hasCurrentStream = false;
        }

        internal void RetireCurrent(ulong sourceClientId) {
            if (!hasCurrentStream || currentStream.SourceClientId != sourceClientId) { return; }
            RetireCurrent();
        }

        internal bool IsCurrent(ulong sourceClientId, uint streamId) {
            return hasCurrentStream && currentStream.Matches(sourceClientId, streamId);
        }

        internal bool IsRetired(ulong sourceClientId, uint streamId) {
            for (int i = 0; i < retiredStreams.Length; i++) {
                if (retiredStreams[i].Matches(sourceClientId, streamId)) { return true; }
            }
            return false;
        }

        private void AddRetiredStream(StreamIdentity stream) {
            if (IsRetired(stream.SourceClientId, stream.StreamId)) { return; }
            if (retiredStreams.Length >= MAXIMUM_RETIRED_STREAMS) {
                retiredStreams.RemoveAt(0);
            }
            retiredStreams.Add(stream);
        }

    }

}
