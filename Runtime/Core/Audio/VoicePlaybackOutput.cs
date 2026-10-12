using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoicePlaybackOutput : IVoicePacketReceiver {

        private sealed class PreparedStream {

            internal readonly VoiceStreamKey Key;
            internal readonly ulong OriginClientId;
            internal readonly VoiceRenderBuffer Buffer;
            internal SharedVoiceDecoder Decoder;
            internal long Position;
            internal long SkippedSamples;
            internal int IsAudible;
            internal float LastPreparedSample;
            internal float BlendFromSample;
            internal int BlendRemainingSamples;
            internal int ObservedRebuffers;
            internal int UnderrunMarginSamples;
            internal double UnderrunPendingSince;
            internal double MarginDecayAt;

            internal PreparedStream(VoiceStreamKey key, SharedVoiceDecoder decoder, ulong originClientId, VoicePlaybackConfiguration configuration) {
                Key = key;
                Decoder = decoder;
                OriginClientId = originClientId;
                Position = decoder.History.WritePosition;
                Buffer = new(configuration);
            }

        }

        private sealed class RouteFilter {

            internal int Channel;
            internal ulong OutputObjectId;
            internal ulong LocalClientId;
            internal bool SuppressOrigin;

        }

        internal bool IsActive => Volatile.Read(ref active) != 0;
        internal bool IsMuted => Volatile.Read(ref muted) != 0;
        internal bool IsReady => IsActive && !IsMuted && engine.IsReceivingEnabled && Volatile.Read(ref appliedResetVersion) == Volatile.Read(ref resetVersion) && Volatile.Read(ref appliedEngineVersion) == engine.Generation;
        internal float Gain => Volatile.Read(ref gain);
        internal int Generation => unchecked(Volatile.Read(ref resetVersion) + engine.Generation);
        internal int ActiveStreams => Volatile.Read(ref activeStreams);
        internal int LastCallbackSamples => Volatile.Read(ref lastCallbackSamples);
        internal byte Level => (byte)Volatile.Read(ref level);
        internal long ReceivedFrames => Interlocked.Read(ref receivedFrames);
        internal long QueueDrops => Interlocked.Read(ref queueDrops);
        internal long LatencyTrimmedSamples => Interlocked.Read(ref latencyTrimmedSamples);
        internal long AudioCallbacks => Interlocked.Read(ref audioCallbacks);
        internal long RenderedSignalSamples => Interlocked.Read(ref renderedSignalSamples);
        internal int BufferedSamples => GetBufferedSamples();
        internal int TargetSamples => GetLargestStreamValue(true);
        internal int JitterSamples => GetLargestStreamValue(false);
        internal double LatencyMilliseconds => GetLatestLatencyMilliseconds();

        private const int PREPARE_QUANTUM = 480;
        private const int CATCHUP_BLEND_SAMPLES = 64;
        private const int CATCHUP_TOLERANCE_SAMPLES = 64;
        // A mid-speech underrun adds reserve beyond the measured jitter; it is held, then released gradually.
        private const int UNDERRUN_MARGIN_STEP_SAMPLES = 96;
        private const int MAXIMUM_UNDERRUN_MARGIN_SAMPLES = 960;
        private const int UNDERRUN_MARGIN_DECAY_SAMPLES = 48;
        private const double UNDERRUN_RESUME_SECONDS = .1d;
        private const double UNDERRUN_MARGIN_HOLD_SECONDS = 5d;
        private const double LATENCY_REPORT_SECONDS = 2d;

        private readonly VoicePlaybackEngine engine;
        private readonly VoicePlaybackConfiguration configuration;
        private readonly List<PreparedStream> streams = new(16);
        private readonly float[] preparedSamples = new float[PREPARE_QUANTUM];
        private PreparedStream[] renderStreams = Array.Empty<PreparedStream>();
        private RouteFilter route;
        private float gain = 1f;
        private int active = 1;
        private int muted;
        private int resetVersion;
        private int appliedResetVersion;
        private int appliedEngineVersion;
        private int activeStreams;
        private int lastCallbackSamples;
        private int level;
        private long receivedFrames;
        private long queueDrops;
        private long latencyTrimmedSamples;
        private long audioCallbacks;
        private long renderedSignalSamples;

        internal VoicePlaybackOutput(VoicePlaybackEngine engine, VoicePlaybackConfiguration configuration) {
            this.engine = engine;
            this.configuration = configuration;
            appliedEngineVersion = engine.Generation;
        }

        public void ReceivePacket(VoicePacket packet) {
            if (packet.HasBroadcastRoute) { Enqueue(new BroadcastFrame(packet.BroadcastRoute, packet.Frame)); }
            else { Enqueue(packet.Frame, packet.Frame.SourceClientId); }
        }

        internal bool Enqueue(VoiceFrame frame, ulong originClientId) {
            if (!IsActive || IsMuted || !engine.IsReceivingEnabled || !frame.IsValid) { return false; }
            if (engine.Enqueue(this, frame, originClientId, Volatile.Read(ref resetVersion))) { return true; }
            Interlocked.Increment(ref queueDrops);
            return false;
        }

        internal bool Enqueue(BroadcastFrame frame) {
            RouteFilter filter = Volatile.Read(ref route);
            if (filter == null || !frame.CanPlayOn(filter.Channel, filter.OutputObjectId)) { return false; }
            if (filter.SuppressOrigin && frame.Route.HasOriginClient && frame.Route.OriginClientId == filter.LocalClientId) { return false; }
            return Enqueue(frame.Frame, frame.Route.OriginClientId);
        }

        internal void ConfigureRoute(int channel, ulong outputObjectId, ulong localClientId, bool suppressOrigin) {
            RouteFilter previous = Volatile.Read(ref route);
            if (previous != null && previous.Channel == channel && previous.OutputObjectId == outputObjectId && previous.LocalClientId == localClientId && previous.SuppressOrigin == suppressOrigin) { return; }
            Reset();
            Volatile.Write(ref route, new RouteFilter { Channel = channel, OutputObjectId = outputObjectId, LocalClientId = localClientId, SuppressOrigin = suppressOrigin });
        }

        internal void SetGain(float value) {
            if (float.IsFinite(value)) { Volatile.Write(ref gain, Math.Clamp(value, 0f, 2f)); }
        }

        internal void SetMuted(bool value) {
            int state = value ? 1 : 0;
            if (Interlocked.Exchange(ref muted, state) != state) { Reset(); }
        }

        internal void Reset() {
            Interlocked.Increment(ref resetVersion);
        }

        internal void RetireSource(ulong sourceClientId) {
            Reset();
        }

        internal void Deactivate() {
            Volatile.Write(ref active, 0);
            Reset();
        }

        internal void RecordQueueDrop() {
            Interlocked.Increment(ref queueDrops);
        }

        internal void ResetDiagnostics() {
            Interlocked.Exchange(ref receivedFrames, 0);
            Interlocked.Exchange(ref queueDrops, 0);
            Interlocked.Exchange(ref latencyTrimmedSamples, 0);
            Interlocked.Exchange(ref audioCallbacks, 0);
            Interlocked.Exchange(ref renderedSignalSamples, 0);
            PreparedStream[] snapshot = Volatile.Read(ref renderStreams);
            for (int i = 0; i < snapshot.Length; i++) { snapshot[i].Buffer.Samples.ResetDiagnostics(); }
        }

        internal void Prepare() {
            int currentResetVersion = Volatile.Read(ref resetVersion);
            int currentEngineVersion = engine.Generation;
            if (appliedResetVersion == currentResetVersion && appliedEngineVersion == currentEngineVersion) { return; }
            streams.Clear();
            Volatile.Write(ref renderStreams, Array.Empty<PreparedStream>());
            Volatile.Write(ref activeStreams, 0);
            Volatile.Write(ref level, 0);
            Volatile.Write(ref appliedEngineVersion, currentEngineVersion);
            Volatile.Write(ref appliedResetVersion, currentResetVersion);
        }

        internal bool Attach(VoiceStreamKey key, SharedVoiceDecoder decoder, ulong originClientId, int expectedVersion, double currentTime) {
            if (expectedVersion != Volatile.Read(ref resetVersion) || !IsReady) { return false; }
            Interlocked.Increment(ref receivedFrames);
            for (int i = 0; i < streams.Count; i++) {
                PreparedStream stream = streams[i];
                if (!stream.Key.Equals(key) || stream.OriginClientId != originClientId) { continue; }
                if (stream.Decoder != decoder) {
                    streams[i] = new(key, decoder, originClientId, configuration);
                    PublishStreams();
                }
                return true;
            }
            for (int i = streams.Count - 1; i >= 0; i--) {
                PreparedStream stream = streams[i];
                bool replacesOrigin = stream.Key.SourceClientId == key.SourceClientId && stream.OriginClientId == originClientId &&
                                      (originClientId != BroadcastRoute.UNASSIGNED_CLIENT_ID || configuration.MaximumStreams == 1);
                if (replacesOrigin || currentTime - stream.Decoder.LastReceivedAt > 2d) { streams.RemoveAt(i); }
            }
            if (streams.Count >= configuration.MaximumStreams) { return false; }
            streams.Add(new(key, decoder, originClientId, configuration));
            PublishStreams();
            return true;
        }

        internal void ReportBufferedSamples() {
            if (!IsReady) { return; }
            for (int i = 0; i < streams.Count; i++) {
                PreparedStream stream = streams[i];
                long sampleCount = stream.Buffer.BufferedSamples + Math.Max(0L, stream.Decoder.History.WritePosition - stream.Position - stream.SkippedSamples);
                stream.Decoder.IncludeBufferedSamples((int)Math.Min(int.MaxValue, sampleCount), stream.Buffer.CallbackReserveSamples);
            }
        }

        internal void Process(double currentTime) {
            Prepare();
            if (!IsReady) { return; }
            int count = 0;
            int currentLevel = 0;
            for (int i = 0; i < streams.Count; i++) {
                PreparedStream stream = streams[i];
                DecodedVoiceHistory history = stream.Decoder.History;
                long silenceSamples = stream.Buffer.Samples.TakeTrailingSilenceSampleCount();
                if (currentTime - stream.Decoder.LastReceivedAt <= configuration.ReceiveTimeout) {
                    count++;
                    currentLevel = Math.Max(currentLevel, stream.Decoder.Level);
                    stream.SkippedSamples = Math.Min(VoiceEncoderSession.SAMPLE_RATE, stream.SkippedSamples + silenceSamples);
                }
                stream.Buffer.SetPacketSamples(stream.Decoder.LatestFrameSamples);
                UpdateUnderrunMargin(stream, currentTime);
                stream.Buffer.SetArrivalReserve(stream.Decoder.JitterSamples, stream.UnderrunMarginSamples);
                int targetSamples = stream.Buffer.TargetSamples;
                long skip = Math.Min(stream.SkippedSamples, Math.Max(0L, history.WritePosition - stream.Position));
                stream.Position += skip;
                stream.SkippedSamples -= skip;
                long pendingSamples = Math.Max(0L, history.WritePosition - stream.Position);
                long totalSamples = stream.Buffer.BufferedSamples + pendingSamples;
                int catchupAllowance = Math.Max(PREPARE_QUANTUM, stream.Decoder.LatestFrameSamples) + stream.Buffer.CallbackReserveSamples + CATCHUP_TOLERANCE_SAMPLES;
                int catchupThreshold = Math.Max(targetSamples, Math.Min(configuration.MaximumSamples, stream.Buffer.CorrectionTargetSamples + catchupAllowance));
                if (totalSamples > catchupThreshold) {
                    long trimmedSamples = Math.Min(pendingSamples, totalSamples - targetSamples);
                    stream.Position += trimmedSamples;
                    Interlocked.Add(ref latencyTrimmedSamples, trimmedSamples);
                    if (trimmedSamples > 0) {
                        stream.BlendFromSample = stream.LastPreparedSample;
                        stream.BlendRemainingSamples = CATCHUP_BLEND_SAMPLES;
                    }
                }
                int preparationTargetSamples = Math.Min(configuration.MaximumSamples, stream.Buffer.CorrectionTargetSamples);
                int iterations = preparationTargetSamples / PREPARE_QUANTUM + 1;
                while (stream.Buffer.BufferedSamples < preparationTargetSamples && iterations-- > 0) {
                    Array.Clear(preparedSamples, 0, preparedSamples.Length);
                    int requestedSamples = Math.Min(PREPARE_QUANTUM, preparationTargetSamples - stream.Buffer.BufferedSamples);
                    int sampleCount = history.Mix(ref stream.Position, preparedSamples.AsSpan(0, requestedSamples), 1f);
                    if (sampleCount == 0) { break; }
                    long historyStart = stream.Position - sampleCount;
                    if (stream.Decoder.TryGetLatencyMarker(historyStart, stream.Position, out long markerPosition, out long captureTicks)) {
                        stream.Buffer.AddLatencyMarker(stream.Buffer.WritePosition + markerPosition - historyStart, captureTicks);
                    }
                    int blendSamples = Math.Min(sampleCount, stream.BlendRemainingSamples);
                    for (int sampleIndex = 0; sampleIndex < blendSamples; sampleIndex++) {
                        float blend = (CATCHUP_BLEND_SAMPLES - stream.BlendRemainingSamples + 1f) / CATCHUP_BLEND_SAMPLES;
                        preparedSamples[sampleIndex] = stream.BlendFromSample + (preparedSamples[sampleIndex] - stream.BlendFromSample) * blend;
                        stream.BlendRemainingSamples--;
                    }
                    stream.LastPreparedSample = preparedSamples[sampleCount - 1];
                    stream.Buffer.Write(preparedSamples.AsSpan(0, sampleCount));
                }
                stream.Buffer.SetPendingSamples((int)Math.Min(int.MaxValue, Math.Max(0L, history.WritePosition - stream.Position - stream.SkippedSamples)));
                Volatile.Write(ref stream.IsAudible, currentTime - stream.Decoder.LastReceivedAt <= configuration.ReceiveTimeout || stream.Buffer.BufferedSamples > 0 ? 1 : 0);
            }
            Volatile.Write(ref activeStreams, count);
            Volatile.Write(ref level, currentLevel);
        }

        internal void Render(float[] samples, int channels, int sampleRate) {
            Array.Clear(samples, 0, samples.Length);
            if (channels <= 0 || sampleRate <= 0) { return; }
            Interlocked.Increment(ref audioCallbacks);
            Volatile.Write(ref lastCallbackSamples, samples.Length / channels);
            bool ready = IsReady;
            int currentGeneration = Generation;
            float outputGain = Gain;
            PreparedStream[] snapshot = Volatile.Read(ref renderStreams);
            for (int i = 0; i < snapshot.Length; i++) {
                PreparedStream stream = snapshot[i];
                float participantGain = stream.OriginClientId == BroadcastRoute.UNASSIGNED_CLIENT_ID ? 1f : engine.GetParticipantGain(stream.OriginClientId);
                stream.Buffer.Mix(samples, channels, sampleRate, outputGain * participantGain, currentGeneration, ready && Volatile.Read(ref stream.IsAudible) != 0);
            }
            long signalSamples = 0;
            for (int i = 0; i < samples.Length; i++) {
                float sample = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
                samples[i] = sample;
                if (i % channels == 0 && Math.Abs(sample) > .00001f) { signalSamples++; }
            }
            Interlocked.Add(ref renderedSignalSamples, signalSamples);
        }

        internal void GetBufferDiagnostics(out int prebufferWaits, out int underruns, out int partialReads, out long overflowedSamples) {
            prebufferWaits = 0;
            underruns = 0;
            partialReads = 0;
            overflowedSamples = 0;
            PreparedStream[] snapshot = Volatile.Read(ref renderStreams);
            for (int i = 0; i < snapshot.Length; i++) {
                SampleRingBuffer buffer = snapshot[i].Buffer.Samples;
                prebufferWaits += buffer.PrebufferWaitCount;
                underruns += buffer.RebufferCount;
                partialReads += buffer.PartialReadCount;
                overflowedSamples += buffer.OverflowedSampleCount;
            }
        }

        private void PublishStreams() {
            Volatile.Write(ref renderStreams, streams.ToArray());
        }

        // An underrun followed shortly by more speech means the target was too small. The end of a talk spurt also
        // drains the buffer, so an underrun only counts when the next frame continues the same spurt.
        private static void UpdateUnderrunMargin(PreparedStream stream, double currentTime) {
            int rebuffers = stream.Buffer.Samples.RebufferCount;
            if (rebuffers > stream.ObservedRebuffers) { stream.UnderrunPendingSince = currentTime; }
            stream.ObservedRebuffers = rebuffers;
            SharedVoiceDecoder decoder = stream.Decoder;
            if (stream.UnderrunPendingSince > 0d && decoder.LastReceivedAt > stream.UnderrunPendingSince) {
                if (!decoder.LatestFrameStartsTalkSpurt && decoder.LastReceivedAt - stream.UnderrunPendingSince <= UNDERRUN_RESUME_SECONDS) {
                    stream.UnderrunMarginSamples = Math.Min(MAXIMUM_UNDERRUN_MARGIN_SAMPLES, stream.UnderrunMarginSamples + UNDERRUN_MARGIN_STEP_SAMPLES);
                    stream.MarginDecayAt = currentTime + UNDERRUN_MARGIN_HOLD_SECONDS;
                }
                stream.UnderrunPendingSince = 0d;
            }
            if (stream.UnderrunMarginSamples > 0 && currentTime >= stream.MarginDecayAt) {
                stream.UnderrunMarginSamples = Math.Max(0, stream.UnderrunMarginSamples - UNDERRUN_MARGIN_DECAY_SAMPLES);
                stream.MarginDecayAt = currentTime + 1d;
            }
        }

        private int GetLargestStreamValue(bool target) {
            int maximum = 0;
            PreparedStream[] snapshot = Volatile.Read(ref renderStreams);
            for (int i = 0; i < snapshot.Length; i++) { maximum = Math.Max(maximum, target ? snapshot[i].Buffer.TargetSamples : snapshot[i].Buffer.JitterSamples); }
            return maximum;
        }

        private double GetLatestLatencyMilliseconds() {
            long latestMeasurement = 0;
            long latency = 0;
            PreparedStream[] snapshot = Volatile.Read(ref renderStreams);
            for (int i = 0; i < snapshot.Length; i++) {
                long measuredAt = snapshot[i].Buffer.LatencyMeasuredAt;
                if (measuredAt <= latestMeasurement) { continue; }
                latestMeasurement = measuredAt;
                latency = snapshot[i].Buffer.LatencyTicks;
            }
            if (latestMeasurement == 0 || Stopwatch.GetTimestamp() - latestMeasurement > Stopwatch.Frequency * LATENCY_REPORT_SECONDS) { return 0d; }
            return latency * 1000d / Stopwatch.Frequency;
        }

        private int GetBufferedSamples() {
            int maximum = 0;
            PreparedStream[] snapshot = Volatile.Read(ref renderStreams);
            for (int i = 0; i < snapshot.Length; i++) { maximum = Math.Max(maximum, snapshot[i].Buffer.TotalBufferedSamples); }
            return maximum;
        }

    }

}
