using System;
using System.Collections.Generic;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceWorldCaptureProcessor : IVoiceProcessor {

        internal string Fault => Volatile.Read(ref fault);

        private readonly VoiceNetworkSession network;
        private readonly VoiceRouteId routeId;
        private readonly VoiceCodecConfiguration configuration;
        private readonly float[] mixedSamples;
        private readonly float[] sourceSamples;
        private readonly Dictionary<WorldAudioHistory, long> cursors = new();
        private readonly HashSet<WorldAudioHistory> selectedHistories = new();
        private readonly List<WorldAudioHistory> removedHistories = new();
        private VoiceWorldCaptureControl control;
        private VoiceEncoderSession encoder;
        private string fault = "";
        private ulong sourceClientId;
        private double nextCaptureAt;
        private double retryAt;
        private uint sequence;
        private int stopped;

        internal VoiceWorldCaptureProcessor(VoiceNetworkSession network, VoiceRouteId routeId, VoiceCodecConfiguration configuration) {
            this.network = network;
            this.routeId = routeId;
            this.configuration = configuration;
            mixedSamples = new float[configuration.SamplesPerFrame];
            sourceSamples = new float[configuration.SamplesPerFrame];
        }

        public void Dispose() {
            Stop();
            DisposeEncoder();
        }

        public void Process(double currentTime) {
            VoiceWorldCaptureControl current = Volatile.Read(ref control);
            if (Volatile.Read(ref stopped) != 0 || current == null || !current.Enabled) {
                DisposeEncoder();
                return;
            }
            if (currentTime < retryAt) { return; }
            try {
                if (encoder == null || sourceClientId != current.SourceClientId) {
                    DisposeEncoder();
                    encoder = new(configuration);
                    Volatile.Write(ref fault, "");
                    sourceClientId = current.SourceClientId;
                    nextCaptureAt = currentTime;
                    sequence = 0;
                }
                SynchronizeSources(current.Sources);
                double duration = mixedSamples.Length / (double)VoiceEncoderSession.SAMPLE_RATE;
                if (currentTime - nextCaptureAt > duration * 8d) {
                    cursors.Clear();
                    nextCaptureAt = currentTime;
                    encoder.Dispose();
                    encoder = new(configuration);
                    sequence = 0;
                }
                for (int i = 0; i < 8 && currentTime >= nextCaptureAt; i++) {
                    nextCaptureAt += duration;
                    Capture(current);
                }
            }
            catch (Exception exception) {
                Volatile.Write(ref fault, exception.Message);
                DisposeEncoder();
                retryAt = currentTime + 1d;
            }
        }

        internal void Configure(VoiceWorldCaptureControl nextControl) {
            Volatile.Write(ref control, nextControl);
        }

        internal void Stop() {
            Interlocked.Exchange(ref stopped, 1);
        }

        private void Capture(VoiceWorldCaptureControl current) {
            Array.Clear(mixedSamples, 0, mixedSamples.Length);
            bool captured = false;
            for (int i = 0; i < current.Sources.Length; i++) {
                VoiceWorldSource source = current.Sources[i];
                if (source.History == null || source.Gain <= 0f) { continue; }
                if (!cursors.TryGetValue(source.History, out long cursor)) { cursor = source.InitialPosition; }
                bool available = source.History.Read(ref cursor, sourceSamples, 8);
                cursors[source.History] = cursor;
                if (!available) { continue; }
                captured = true;
                for (int sampleIndex = 0; sampleIndex < mixedSamples.Length; sampleIndex++) { mixedSamples[sampleIndex] += sourceSamples[sampleIndex] * source.Gain; }
            }
            if (!captured) { return; }
            for (int i = 0; i < mixedSamples.Length; i++) { mixedSamples[i] = Math.Clamp(mixedSamples[i], -1f, 1f); }
            byte level = AudioMath.GetNetworkLevel(AudioMath.GetDecibels(mixedSamples));
            if (!encoder.TryEncode(mixedSamples, sequence, level, out VoiceFrame frame)) { return; }
            sequence = unchecked(sequence + 1);
            frame.SourceClientId = sourceClientId;
            VoiceWorldCaptureControl latest = Volatile.Read(ref control);
            if (Volatile.Read(ref stopped) != 0 || latest == null || !latest.Enabled || latest.SourceClientId != sourceClientId) { return; }
            network.Submit(routeId, frame);
        }

        private void SynchronizeSources(VoiceWorldSource[] sources) {
            selectedHistories.Clear();
            for (int i = 0; i < sources.Length; i++) {
                if (sources[i].History != null) { selectedHistories.Add(sources[i].History); }
            }
            removedHistories.Clear();
            foreach (WorldAudioHistory history in cursors.Keys) {
                if (!selectedHistories.Contains(history)) { removedHistories.Add(history); }
            }
            for (int i = 0; i < removedHistories.Count; i++) { cursors.Remove(removedHistories[i]); }
        }

        private void DisposeEncoder() {
            if (encoder != null) {
                encoder.Dispose();
                encoder = null;
            }
            cursors.Clear();
        }

    }

}
