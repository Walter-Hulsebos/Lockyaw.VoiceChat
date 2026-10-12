using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoicePlaybackEngine : IVoiceProcessor {

        private readonly struct PendingFrame {

            internal readonly VoicePlaybackOutput Output;
            internal readonly VoiceFrame Frame;
            internal readonly ulong OriginClientId;
            internal readonly int ResetVersion;
            internal readonly int Generation;
            internal readonly long EnqueuedAt;

            internal PendingFrame(VoicePlaybackOutput output, VoiceFrame frame, ulong originClientId, int resetVersion, int generation) {
                Output = output;
                Frame = frame;
                OriginClientId = originClientId;
                ResetVersion = resetVersion;
                Generation = generation;
                EnqueuedAt = Stopwatch.GetTimestamp();
            }

        }

        private readonly struct ParticipantGain {

            internal readonly ulong ClientId;
            internal readonly float Gain;

            internal ParticipantGain(ulong clientId, float gain) {
                ClientId = clientId;
                Gain = gain;
            }

        }

        internal bool IsReceivingEnabled => Volatile.Read(ref receivingEnabled) != 0;
        internal int Generation => Volatile.Read(ref generation);
        internal long DecodedStreamCount => Interlocked.Read(ref decodedStreamCount);
        internal long DecodedFrames => Interlocked.Read(ref decodedFrames);

        private const int MAXIMUM_FRAME_AGE_MILLISECONDS = 100;
        private const int MAXIMUM_FRAMES = 512;
        private const int MAXIMUM_STREAMS = 128;
        private const int MAXIMUM_OUTPUTS = 64;

        private readonly object ingressLock = new();
        private readonly object controlLock = new();
        private readonly Queue<PendingFrame> frames = new(MAXIMUM_FRAMES);
        private readonly Queue<VoicePlaybackOutput> pendingOutputs = new(MAXIMUM_OUTPUTS);
        private readonly List<VoicePlaybackOutput> outputs = new(MAXIMUM_OUTPUTS);
        private readonly Dictionary<VoiceStreamKey, SharedVoiceDecoder> streams = new(MAXIMUM_STREAMS);
        private readonly List<VoiceStreamKey> expiredStreams = new(MAXIMUM_STREAMS);
        private readonly int maximumReorderDelayMilliseconds;
        private readonly int maxConcealedFrames;
        private ParticipantGain[] gains = Array.Empty<ParticipantGain>();
        private Action wakeHandler;
        private int receivingEnabled = 1;
        private int generation;
        private int appliedGeneration;
        private int outputCount;
        private int disposed;
        private long decodedStreamCount;
        private long decodedFrames;

        internal VoicePlaybackEngine(int maximumReorderDelayMilliseconds = 10, int maxConcealedFrames = 3) {
            this.maximumReorderDelayMilliseconds = Math.Clamp(maximumReorderDelayMilliseconds, 0, 100);
            this.maxConcealedFrames = Math.Clamp(maxConcealedFrames, 0, 8);
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
            SetReceivingEnabled(false);
            lock (controlLock) {
                while (pendingOutputs.Count > 0) { pendingOutputs.Dequeue().Deactivate(); }
            }
            for (int i = 0; i < outputs.Count; i++) { outputs[i].Deactivate(); }
            outputs.Clear();
            ClearStreams();
            lock (ingressLock) { frames.Clear(); }
        }

        public void Process(double currentTime) {
            if (Volatile.Read(ref disposed) != 0) { return; }
            lock (controlLock) {
                while (pendingOutputs.Count > 0) { outputs.Add(pendingOutputs.Dequeue()); }
            }
            if (appliedGeneration != Generation) {
                ClearStreams();
                appliedGeneration = Generation;
            }
            for (int i = outputs.Count - 1; i >= 0; i--) {
                if (!outputs[i].IsActive) {
                    outputs.RemoveAt(i);
                    Interlocked.Decrement(ref outputCount);
                }
                else { outputs[i].Prepare(); }
            }
            long oldestEnqueuedAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * MAXIMUM_FRAME_AGE_MILLISECONDS / 1000;
            for (int i = 0; i < MAXIMUM_FRAMES; i++) {
                PendingFrame pending;
                lock (ingressLock) {
                    if (frames.Count == 0) { break; }
                    pending = frames.Dequeue();
                }
                if (!IsReceivingEnabled || pending.Generation != Generation || !pending.Output.IsActive) { continue; }
                if (pending.EnqueuedAt < oldestEnqueuedAt) {
                    pending.Output.RecordQueueDrop();
                    continue;
                }
                VoiceStreamKey key = new(pending.Frame);
                if (!streams.TryGetValue(key, out SharedVoiceDecoder decoder)) {
                    if (streams.Count >= MAXIMUM_STREAMS) { continue; }
                    decoder = new(maximumReorderDelayMilliseconds, maxConcealedFrames);
                    streams.Add(key, decoder);
                    Interlocked.Increment(ref decodedStreamCount);
                }
                if (pending.Output.Attach(key, decoder, pending.OriginClientId, pending.ResetVersion, currentTime)) {
                    long previousDecodedFrames = decoder.DecodedFrames;
                    decoder.Receive(pending.Frame, currentTime);
                    Interlocked.Add(ref decodedFrames, decoder.DecodedFrames - previousDecodedFrames);
                }
            }
            foreach (SharedVoiceDecoder decoder in streams.Values) { decoder.BeginBufferEstimate(); }
            for (int i = 0; i < outputs.Count; i++) { outputs[i].ReportBufferedSamples(); }
            expiredStreams.Clear();
            foreach (KeyValuePair<VoiceStreamKey, SharedVoiceDecoder> stream in streams) {
                long previousDecodedFrames = stream.Value.DecodedFrames;
                stream.Value.Process(currentTime);
                Interlocked.Add(ref decodedFrames, stream.Value.DecodedFrames - previousDecodedFrames);
                if (currentTime - stream.Value.LastReceivedAt > 5d) { expiredStreams.Add(stream.Key); }
            }
            for (int i = 0; i < outputs.Count; i++) { outputs[i].Process(currentTime); }
            for (int i = 0; i < expiredStreams.Count; i++) {
                SharedVoiceDecoder decoder = streams[expiredStreams[i]];
                streams.Remove(expiredStreams[i]);
                decoder.Dispose();
            }
        }

        internal VoicePlaybackOutput CreateOutput(VoicePlaybackConfiguration configuration) {
            lock (controlLock) {
                if (disposed != 0 || outputCount >= MAXIMUM_OUTPUTS) { return null; }
                VoicePlaybackOutput output = new(this, configuration);
                Interlocked.Increment(ref outputCount);
                pendingOutputs.Enqueue(output);
                return output;
            }
        }

        internal void ReleaseOutput(VoicePlaybackOutput output) {
            if (output != null) { output.Deactivate(); }
        }

        internal void Reset() {
            Interlocked.Increment(ref generation);
        }

        internal void SetReceivingEnabled(bool value) {
            int state = value ? 1 : 0;
            if (Interlocked.Exchange(ref receivingEnabled, state) != state) { Interlocked.Increment(ref generation); }
        }

        internal void SetParticipantGain(ulong clientId, float gain) {
            if (!float.IsFinite(gain)) { return; }
            gain = Math.Clamp(gain, 0f, 1f);
            lock (controlLock) {
                ParticipantGain[] previous = gains;
                int position = -1;
                for (int i = 0; i < previous.Length; i++) {
                    if (previous[i].ClientId == clientId) { position = i; break; }
                }
                if (gain == 1f) {
                    if (position < 0) { return; }
                    ParticipantGain[] removed = new ParticipantGain[previous.Length - 1];
                    Array.Copy(previous, 0, removed, 0, position);
                    Array.Copy(previous, position + 1, removed, position, previous.Length - position - 1);
                    Volatile.Write(ref gains, removed);
                    return;
                }
                if (position >= 0 && previous[position].Gain == gain) { return; }
                if (position < 0 && previous.Length >= MAXIMUM_STREAMS) { return; }
                ParticipantGain[] replacement = new ParticipantGain[position < 0 ? previous.Length + 1 : previous.Length];
                Array.Copy(previous, replacement, previous.Length);
                replacement[position < 0 ? previous.Length : position] = new(clientId, gain);
                Volatile.Write(ref gains, replacement);
            }
        }

        internal void RetainParticipantGains(ISet<ulong> activeClientIds) {
            lock (controlLock) {
                ParticipantGain[] previous = gains;
                int retainedCount = 0;
                for (int i = 0; i < previous.Length; i++) {
                    if (activeClientIds.Contains(previous[i].ClientId)) { retainedCount++; }
                }
                if (retainedCount == previous.Length) { return; }
                ParticipantGain[] retained = retainedCount == 0 ? Array.Empty<ParticipantGain>() : new ParticipantGain[retainedCount];
                int position = 0;
                for (int i = 0; i < previous.Length; i++) {
                    if (activeClientIds.Contains(previous[i].ClientId)) { retained[position++] = previous[i]; }
                }
                Volatile.Write(ref gains, retained);
            }
        }

        // Decoding starts when a frame arrives rather than on the worker's next poll.
        internal void SetWakeHandler(Action handler) {
            Volatile.Write(ref wakeHandler, handler);
        }

        internal bool Enqueue(VoicePlaybackOutput output, VoiceFrame frame, ulong originClientId, int resetVersion) {
            lock (ingressLock) {
                if (disposed != 0) { return false; }
                if (frames.Count >= MAXIMUM_FRAMES) { frames.Dequeue().Output.RecordQueueDrop(); }
                frames.Enqueue(new(output, frame, originClientId, resetVersion, Generation));
            }
            Action handler = Volatile.Read(ref wakeHandler);
            if (handler != null) { handler(); }
            return true;
        }

        internal float GetParticipantGain(ulong clientId) {
            ParticipantGain[] snapshot = Volatile.Read(ref gains);
            for (int i = 0; i < snapshot.Length; i++) {
                if (snapshot[i].ClientId == clientId) { return snapshot[i].Gain; }
            }
            return 1f;
        }

        private void ClearStreams() {
            foreach (SharedVoiceDecoder decoder in streams.Values) { decoder.Dispose(); }
            streams.Clear();
        }

    }

}
