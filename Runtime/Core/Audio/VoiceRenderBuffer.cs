using System;
using System.Diagnostics;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceRenderBuffer {

        internal int BufferedSamples => buffer.GetSampleCount();
        internal int TotalBufferedSamples => buffer.GetSampleCount() + Volatile.Read(ref pendingSamples);
        // The configured target is a minimum. The working target covers one device callback, one packet, the measured
        // arrival jitter and a small scheduling reserve.
        internal int TargetSamples => Math.Min(buffer.Capacity, Math.Max(configuration.TargetSamples, CallbackReserveSamples + Volatile.Read(ref packetSamples) + Volatile.Read(ref jitterSamples) + Volatile.Read(ref underrunReserveSamples) + SCHEDULING_RESERVE_SAMPLES));
        internal int CorrectionTargetSamples => TargetSamples + Math.Max(0, CallbackReserveSamples - READ_QUANTUM - 2) / 2;
        internal int CallbackReserveSamples => Volatile.Read(ref callbackReserveSamples);
        internal int LastCallbackSamples => Volatile.Read(ref lastCallbackSamples);
        internal int JitterSamples => Volatile.Read(ref jitterSamples);
        internal long WritePosition => buffer.WritePosition;
        // Capture-to-render time of the most recent locally captured frame, in Stopwatch ticks.
        internal long LatencyTicks => Interlocked.Read(ref latencyTicks);
        internal long LatencyMeasuredAt => Interlocked.Read(ref latencyMeasuredAt);
        internal SampleRingBuffer Samples => buffer;

        private const int READ_QUANTUM = 64;
        // Covers waking the processing worker. Arrival timing variation is measured separately.
        private const int SCHEDULING_RESERVE_SAMPLES = 96;
        private const int LATENCY_MARKERS = 32;

        private readonly SampleRingBuffer buffer;
        private readonly VoicePlaybackConfiguration configuration;
        private readonly float[] readSamples = new float[READ_QUANTUM];
        private readonly long[] markerPositions = new long[LATENCY_MARKERS];
        private readonly long[] markerCaptureTicks = new long[LATENCY_MARKERS];
        private int readIndex;
        private int readCount;
        private int generation = -1;
        private int outputRate;
        private int lastCallbackSamples;
        private int callbackReserveSamples;
        private int pendingSamples;
        private int packetSamples;
        private int jitterSamples;
        private int underrunReserveSamples;
        private int markerHead;
        private int markerTail;
        private long latencyTicks;
        private long latencyMeasuredAt;
        private float previousSample;
        private float nextSample;
        private double sourcePosition;
        private bool hasSamples;

        internal VoiceRenderBuffer(VoicePlaybackConfiguration configuration) {
            this.configuration = configuration;
            buffer = new(configuration.MaximumSamples, configuration.TargetSamples);
        }

        internal void Write(ReadOnlySpan<float> samples) {
            buffer.Write(samples);
        }

        internal void SetPacketSamples(int sampleCount) {
            Volatile.Write(ref packetSamples, Math.Max(0, sampleCount));
        }

        internal void SetArrivalReserve(int measuredJitterSamples, int underrunSamples) {
            Volatile.Write(ref jitterSamples, Math.Max(0, measuredJitterSamples));
            Volatile.Write(ref underrunReserveSamples, Math.Max(0, underrunSamples));
        }

        internal void SetPendingSamples(int sampleCount) {
            Volatile.Write(ref pendingSamples, Math.Max(0, sampleCount));
        }

        // Writer thread: the sample at ringPosition was captured locally at captureTicks.
        internal void AddLatencyMarker(long ringPosition, long captureTicks) {
            int tail = markerTail;
            int next = (tail + 1) % LATENCY_MARKERS;
            if (next == Volatile.Read(ref markerHead)) { return; }
            markerPositions[tail] = ringPosition;
            markerCaptureTicks[tail] = captureTicks;
            Volatile.Write(ref markerTail, next);
        }

        internal void Clear() {
            buffer.Clear();
            Volatile.Write(ref pendingSamples, 0);
        }

        internal void Mix(float[] destination, int channels, int sampleRate, float gain, int currentGeneration, bool isReady) {
            if (channels <= 0 || sampleRate <= 0) { return; }
            int callbackSamples = destination.Length / channels;
            int sourceSamples = (int)Math.Ceiling(callbackSamples * (double)VoiceEncoderSession.SAMPLE_RATE / sampleRate * (1d + configuration.MaximumPitchCorrection));
            Volatile.Write(ref callbackReserveSamples, sourceSamples + READ_QUANTUM + 2);
            buffer.SetPrebufferSamples(TargetSamples);
            Volatile.Write(ref lastCallbackSamples, callbackSamples);
            if (!isReady || generation != currentGeneration || outputRate != sampleRate) {
                readCount = 0;
                readIndex = 0;
                previousSample = 0f;
                nextSample = 0f;
                sourcePosition = 0d;
                hasSamples = false;
                generation = currentGeneration;
                outputRate = sampleRate;
                if (!isReady) { return; }
            }
            long callbackTicks = Stopwatch.GetTimestamp();
            long startPosition = ConsumedPosition;
            double errorSeconds = (TotalBufferedSamples - CorrectionTargetSamples) / (double)VoiceEncoderSession.SAMPLE_RATE;
            double correction = Math.Clamp(errorSeconds * configuration.PitchCorrectionGain, -configuration.MaximumPitchCorrection, configuration.MaximumPitchCorrection);
            double ratio = VoiceEncoderSession.SAMPLE_RATE / (double)sampleRate * (1d + correction);
            if (!hasSamples) {
                previousSample = ReadSample();
                nextSample = ReadSample();
                hasSamples = true;
            }
            for (int i = 0; i < destination.Length / channels; i++) {
                float sample = (previousSample + (nextSample - previousSample) * (float)sourcePosition) * gain;
                for (int channel = 0; channel < channels; channel++) { destination[i * channels + channel] += sample; }
                sourcePosition += ratio;
                while (sourcePosition >= 1d) {
                    previousSample = nextSample;
                    nextSample = ReadSample();
                    sourcePosition -= 1d;
                }
            }
            ResolveLatencyMarkers(startPosition, ConsumedPosition, callbackTicks, ratio * sampleRate);
        }

        // Ring position of the next sample the interpolator will consume.
        private long ConsumedPosition => buffer.ReadPosition - (readCount - readIndex);

        private void ResolveLatencyMarkers(long startPosition, long endPosition, long callbackTicks, double sourceRate) {
            int head = markerHead;
            int tail = Volatile.Read(ref markerTail);
            while (head != tail && markerPositions[head] < endPosition) {
                long position = markerPositions[head];
                if (position >= startPosition && sourceRate > 0d) {
                    long renderedTicks = callbackTicks + (long)((position - startPosition) / sourceRate * Stopwatch.Frequency);
                    Interlocked.Exchange(ref latencyTicks, Math.Max(0L, renderedTicks - markerCaptureTicks[head]));
                    Interlocked.Exchange(ref latencyMeasuredAt, callbackTicks);
                }
                head = (head + 1) % LATENCY_MARKERS;
            }
            Volatile.Write(ref markerHead, head);
        }

        private float ReadSample() {
            if (readIndex >= readCount) {
                buffer.Read(readSamples);
                readCount = readSamples.Length;
                readIndex = 0;
            }
            return readSamples[readIndex++];
        }

    }

}
