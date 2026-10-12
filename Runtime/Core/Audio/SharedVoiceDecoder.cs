using System;

namespace Lockyaw.VoiceChat {

    internal sealed class SharedVoiceDecoder : IDisposable {

        internal DecodedVoiceHistory History { get; } = new();
        internal double LastReceivedAt { get; private set; }
        internal byte Level { get; private set; }
        internal int LatestFrameSamples { get; private set; }
        internal bool LatestFrameStartsTalkSpurt { get; private set; }
        internal long ReceivedFrames { get; private set; }
        internal long DuplicateFrames { get; private set; }
        internal long MissingFrames { get; private set; }
        internal long DecoderFailures { get; private set; }
        internal long DecodedFrames { get; private set; }
        // Spread of recent arrival delays, excluding the latest 5 percent, in 48 kHz samples.
        internal int JitterSamples { get; private set; }

        private const int ARRIVAL_HISTORY = 256;
        private const double ARRIVAL_QUANTILE = .95d;
        private const int ESTIMATE_INTERVAL = 4;
        // A percentile from fewer arrivals mostly reflects how the first frames were batched, so jitter is reported after this many.
        private const int MINIMUM_JITTER_ARRIVALS = 16;
        private const int MAXIMUM_ANCHOR_SAMPLES = 1 << 30;
        private const int CONCEALMENT_REFERENCE_SAMPLES = 480;
        private const int LATENCY_MARKERS = 32;

        private readonly VoiceDecoderSession decoder = new();
        private readonly EncodedFrameReorderBuffer reorder;
        private readonly int maxConcealedFrames;
        private readonly double[] arrivalDelays = new double[ARRIVAL_HISTORY];
        private readonly double[] sortedDelays = new double[ARRIVAL_HISTORY];
        private readonly long[] markerPositions = new long[LATENCY_MARKERS];
        private readonly long[] markerCaptureTicks = new long[LATENCY_MARKERS];
        private int bufferedSamples;
        private int callbackReserveSamples;
        private int arrivalCount;
        private int arrivalWrite;
        private int arrivalsSinceEstimate;
        private int markerWrite;
        private int markerCount;
        private double delayAnchor;
        private double typicalDelay;
        private uint anchorTimestamp;
        private bool hasDelayAnchor;

        internal SharedVoiceDecoder(int reorderMilliseconds, int maxConcealedFrames) {
            reorder = new(16, reorderMilliseconds, 1024);
            this.maxConcealedFrames = maxConcealedFrames;
        }

        public void Dispose() {
            decoder.Dispose();
            reorder.Clear();
        }

        internal void Receive(VoiceFrame frame, double currentTime) {
            EncodedFrameReorderBuffer.Arrival arrival = reorder.Enqueue(frame, currentTime);
            if (arrival == EncodedFrameReorderBuffer.Arrival.CapacityReached) {
                Process(currentTime, true);
                arrival = reorder.Enqueue(frame, currentTime);
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Duplicate) { DuplicateFrames++; return; }
            bool restarted = arrival == EncodedFrameReorderBuffer.Arrival.Discontinuity;
            if (restarted) {
                decoder.ResetState();
                arrival = reorder.BeginStream(frame, currentTime);
            }
            if (arrival != EncodedFrameReorderBuffer.Arrival.Accepted && arrival != EncodedFrameReorderBuffer.Arrival.Reordered) { return; }
            ObserveArrival(frame, currentTime, restarted);
            LatestFrameSamples = frame.SampleCount;
            LatestFrameStartsTalkSpurt = frame.StartsTalkSpurt || restarted;
            LastReceivedAt = currentTime;
            ReceivedFrames++;
        }

        internal void Process(double currentTime, bool forceDeadline = false) {
            while (reorder.TryTake(currentTime, bufferedSamples == int.MaxValue ? 0 : bufferedSamples, callbackReserveSamples, forceDeadline, out VoiceFrame frame, out uint missingFrames, out uint silenceSamples, out _)) {
                try {
                    Decode(frame, missingFrames, silenceSamples);
                    Level = frame.Level;
                    DecodedFrames++;
                }
                catch {
                    DecoderFailures++;
                    decoder.ResetState();
                    reorder.ResetCurrentStream();
                }
                forceDeadline = false;
            }
        }

        internal void BeginBufferEstimate() {
            bufferedSamples = int.MaxValue;
            callbackReserveSamples = 0;
        }

        internal void IncludeBufferedSamples(int sampleCount, int requiredCallbackSamples) {
            bufferedSamples = Math.Min(bufferedSamples, Math.Max(0, sampleCount));
            callbackReserveSamples = Math.Max(callbackReserveSamples, requiredCallbackSamples);
        }

        // Finds a locally captured frame that starts within [startPosition, endPosition) of the decoded history.
        internal bool TryGetLatencyMarker(long startPosition, long endPosition, out long position, out long captureTicks) {
            for (int i = 0; i < markerCount; i++) {
                int index = (markerWrite - 1 - i + LATENCY_MARKERS) % LATENCY_MARKERS;
                if (markerPositions[index] < startPosition || markerPositions[index] >= endPosition) { continue; }
                position = markerPositions[index];
                captureTicks = markerCaptureTicks[index];
                return true;
            }
            position = 0;
            captureTicks = 0;
            return false;
        }

        // Measures each frame's arrival against its sample timeline. A talk spurt or restart shifts that relation by an
        // unknown pause, so the new spurt continues on the existing scale from the typical recent delay.
        private void ObserveArrival(VoiceFrame frame, double currentTime, bool restarted) {
            int timelineSamples = unchecked((int)(frame.SampleTimestamp - anchorTimestamp));
            if (!hasDelayAnchor || restarted || frame.StartsTalkSpurt || Math.Abs(timelineSamples) > MAXIMUM_ANCHOR_SAMPLES) {
                delayAnchor = currentTime - typicalDelay;
                anchorTimestamp = frame.SampleTimestamp;
                hasDelayAnchor = true;
                timelineSamples = 0;
            }
            arrivalDelays[arrivalWrite] = currentTime - delayAnchor - timelineSamples / (double)VoiceEncoderSession.SAMPLE_RATE;
            arrivalWrite = (arrivalWrite + 1) % ARRIVAL_HISTORY;
            if (arrivalCount < ARRIVAL_HISTORY) { arrivalCount++; }
            if (++arrivalsSinceEstimate < ESTIMATE_INTERVAL && arrivalCount > MINIMUM_JITTER_ARRIVALS) { return; }
            arrivalsSinceEstimate = 0;
            Array.Copy(arrivalDelays, sortedDelays, arrivalCount);
            Array.Sort(sortedDelays, 0, arrivalCount);
            int upperIndex = Math.Clamp((int)Math.Ceiling(arrivalCount * ARRIVAL_QUANTILE) - 1, 0, arrivalCount - 1);
            typicalDelay = sortedDelays[arrivalCount / 2];
            JitterSamples = arrivalCount < MINIMUM_JITTER_ARRIVALS ? 0 : (int)Math.Round(Math.Clamp(sortedDelays[upperIndex] - sortedDelays[0], 0d, 1d) * VoiceEncoderSession.SAMPLE_RATE);
        }

        private void Decode(VoiceFrame frame, uint missingFrames, uint silenceSamples) {
            MissingFrames += missingFrames;
            // The concealment limit is defined for 10 ms frames, so shorter frames conceal the same duration.
            int concealmentLimit = Math.Max(maxConcealedFrames, maxConcealedFrames * CONCEALMENT_REFERENCE_SAMPLES / Math.Max(1, (int)frame.SampleCount));
            if (silenceSamples > 0 || missingFrames > concealmentLimit) {
                decoder.ResetState();
                long missingSamples = (long)missingFrames * frame.SampleCount + silenceSamples;
                History.WriteSilence((int)Math.Min(missingSamples, VoiceEncoderSession.SAMPLE_RATE));
            }
            else if (missingFrames > 0) {
                for (uint i = 1; i < missingFrames; i++) { History.Write(decoder.Conceal(frame.SampleCount)); }
                History.Write(decoder.Decode(frame, true));
            }
            long framePosition = History.WritePosition;
            History.Write(decoder.Decode(frame, false));
            if (VoiceLatencyProbe.TryGetCapture(frame.StreamId, frame.Sequence, out long captureTicks)) {
                markerPositions[markerWrite] = framePosition;
                markerCaptureTicks[markerWrite] = captureTicks;
                markerWrite = (markerWrite + 1) % LATENCY_MARKERS;
                markerCount = Math.Min(markerCount + 1, LATENCY_MARKERS);
            }
        }

    }

}
