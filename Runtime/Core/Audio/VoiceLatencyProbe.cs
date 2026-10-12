#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

namespace Lockyaw.VoiceChat {

    // Capture times of recently sent local frames. A renderer in the same process, such as local loopback, uses them to
    // measure capture-to-playback latency without a shared network clock.
    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    internal static class VoiceLatencyProbe {

        private struct Entry {
            internal uint StreamId;
            internal uint Sequence;
            internal long CaptureTicks;
            internal bool IsSet;
        }

        private const int CAPACITY = 256;
        private static readonly Entry[] entries = new Entry[CAPACITY];
        private static readonly object synchronization = new();

        internal static void RecordCapture(uint streamId, uint sequence, long captureTicks) {
            lock (synchronization) {
                entries[sequence % CAPACITY] = new Entry { StreamId = streamId, Sequence = sequence, CaptureTicks = captureTicks, IsSet = true };
            }
        }

        internal static bool TryGetCapture(uint streamId, uint sequence, out long captureTicks) {
            lock (synchronization) {
                Entry entry = entries[sequence % CAPACITY];
                captureTicks = entry.CaptureTicks;
                return entry.IsSet && entry.StreamId == streamId && entry.Sequence == sequence;
            }
        }

    }

}
