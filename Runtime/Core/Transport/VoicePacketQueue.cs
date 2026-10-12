using System.Diagnostics;

namespace Lockyaw.VoiceChat {

    internal sealed class VoicePacketQueue {

        private struct Entry {
            internal ulong PeerId;
            internal VoicePacket Packet;
            internal long EnqueuedAt;
        }

        private readonly object synchronization = new();
        private readonly Entry[] entries;
        private readonly long maximumAgeTicks;
        private int readPosition;
        private int count;

        internal VoicePacketQueue(int capacity = 256, int maximumAgeMilliseconds = 100) {
            entries = new Entry[capacity];
            maximumAgeTicks = Stopwatch.Frequency * maximumAgeMilliseconds / 1000;
        }

        internal void Enqueue(ulong peerId, VoicePacket packet) {
            lock (synchronization) {
                if (!packet.CanTransmit) { return; }
                if (count == entries.Length) {
                    readPosition = (readPosition + 1) % entries.Length;
                    count--;
                }
                int writePosition = (readPosition + count) % entries.Length;
                entries[writePosition] = new Entry { PeerId = peerId, Packet = packet, EnqueuedAt = Stopwatch.GetTimestamp() };
                count++;
            }
        }

        internal void Clear() {
            lock (synchronization) {
                readPosition = 0;
                count = 0;
            }
        }

        internal bool TryDequeue(out ulong peerId, out VoicePacket packet) {
            lock (synchronization) {
                while (count > 0) {
                    Entry entry = entries[readPosition];
                    readPosition = (readPosition + 1) % entries.Length;
                    count--;
                    if (!entry.Packet.CanTransmit || Stopwatch.GetTimestamp() - entry.EnqueuedAt > maximumAgeTicks) { continue; }
                    peerId = entry.PeerId;
                    packet = entry.Packet;
                    return true;
                }
            }
            peerId = 0;
            packet = default;
            return false;
        }

    }

}
