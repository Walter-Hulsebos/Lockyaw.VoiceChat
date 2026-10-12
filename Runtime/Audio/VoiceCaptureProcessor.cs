using System;
using System.Diagnostics;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceCaptureProcessor : IVoiceProcessor, IVoiceWaitSource {

        internal VoiceCaptureState State => (VoiceCaptureState)Volatile.Read(ref state);
        internal bool IsSpeaking => Volatile.Read(ref speaking) != 0;
        internal byte Level => (byte)Volatile.Read(ref level);
        internal VoiceCaptureDiagnostics Diagnostics => Volatile.Read(ref diagnostics);
        internal string Fault => Volatile.Read(ref fault);

        // The first frames of each talk spurt carry the marker, so one lost packet does not hide the spurt boundary.
        private const int TALK_SPURT_MARKED_FRAMES = 3;
        // Frames shorter than the device period would leave in bursts without lowering delay, so capture falls back to this length.
        private const int DEVICE_FALLBACK_FRAME_SAMPLES = (int)VoiceFrameLength.TenMilliseconds;
        // Queued capture beyond this is stale after a stall; it is discarded instead of being sent late.
        private const int STALE_BACKLOG_SAMPLES = 960;

        private readonly IVoiceCaptureSource capture;
        private readonly IVoiceCaptureSignal signal;
        private readonly VoiceNetworkSession network;
        private readonly VoiceRouteId routeId;
        private readonly VoiceCodecConfiguration configuration;
        private readonly TransmissionMode transmissionMode;
        private readonly float activationThreshold;
        private readonly float activationRelease;
        private readonly float reconnectInterval;
        private float[] frameSamples;
        private float[] readSamples;
        private VoiceCaptureControl control;
        private VoiceEncoderSession encoder;
        private VoiceDeviceId activeDevice;
        private VoiceCaptureDiagnostics diagnostics = new();
        private string fault = "";
        private double retryAt;
        private double releaseAt;
        private double nextDiagnosticsAt;
        private long observedDiscontinuities;
        private long capturedFrames;
        private long encodedFrames;
        private long suppressedFrames;
        private long submittedFrames;
        private long staleSamples;
        private uint sequence;
        private int frameLength;
        private int sampleCount;
        private int maximumFramesPerPass;
        private int talkSpurtFramesRemaining;
        private int state;
        private int speaking;
        private int level;
        private int stopped;
        private bool captureStarted;
        private bool wasTransmitting;

        internal VoiceCaptureProcessor(VoiceNetworkSession network, VoiceRouteId routeId, VoiceCodecConfiguration configuration, TransmissionMode transmissionMode, float activationThreshold, float activationRelease, float reconnectInterval, IVoiceCaptureSource capture = null) {
            this.capture = capture == null ? new NativeVoiceCapture() : capture;
            signal = this.capture as IVoiceCaptureSignal;
            this.network = network;
            this.routeId = routeId;
            this.configuration = configuration;
            this.transmissionMode = transmissionMode;
            this.activationThreshold = activationThreshold;
            this.activationRelease = activationRelease;
            this.reconnectInterval = reconnectInterval;
            frameLength = configuration.SamplesPerFrame;
            frameSamples = new float[frameLength];
            readSamples = new float[frameLength];
        }

        public void Dispose() {
            Interlocked.Exchange(ref stopped, 1);
            try { StopResources(); }
            finally { capture.Dispose(); }
        }

        public bool WaitForWork(int timeoutMilliseconds) {
            return signal != null && encoder != null && Volatile.Read(ref stopped) == 0 && signal.WaitForSamples(timeoutMilliseconds);
        }

        public void InterruptWait() {
            if (signal != null) { signal.InterruptWait(); }
        }

        public void Process(double currentTime) {
            VoiceCaptureControl current = Volatile.Read(ref control);
            if (Volatile.Read(ref stopped) != 0 || current == null || !current.Enabled || current.DeviceId.IsNone) {
                StopResources();
                retryAt = 0d;
                PublishDiagnostics(currentTime);
                return;
            }
            try {
                if (activeDevice != current.DeviceId) {
                    StopResources();
                    activeDevice = current.DeviceId;
                    retryAt = 0d;
                    nextDiagnosticsAt = 0d;
                }
                if (encoder == null) {
                    if (currentTime < retryAt) { return; }
                    activeDevice = current.DeviceId;
                    if (signal != null) { signal.SetPreferredPeriod(Math.Min(configuration.SamplesPerFrame, DEVICE_FALLBACK_FRAME_SAMPLES)); }
                    VoiceCaptureState captureState = capture.Start(activeDevice);
                    Volatile.Write(ref state, (int)captureState);
                    if (captureState != VoiceCaptureState.Running) {
                        retryAt = currentTime + reconnectInterval;
                        PublishDiagnostics(currentTime);
                        return;
                    }
                    captureStarted = true;
                    StartEncoder();
                    Volatile.Write(ref fault, "");
                    observedDiscontinuities = capture.DiscontinuityCount;
                }
                if (capture.State != VoiceCaptureState.Running) {
                    VoiceCaptureState captureState = capture.State;
                    StopResources();
                    Volatile.Write(ref state, (int)captureState);
                    retryAt = currentTime + reconnectInterval;
                    PublishDiagnostics(currentTime);
                    return;
                }
                DiscardStaleSamples();
                int processed = 0;
                for (int pass = 0; pass < 16; pass++) {
                    int received = capture.Read(readSamples, frameLength - sampleCount);
                    if (capture.DiscontinuityCount != observedDiscontinuities) {
                        observedDiscontinuities = capture.DiscontinuityCount;
                        encoder.Dispose();
                        StartEncoder();
                    }
                    if (received == 0) { break; }
                    Array.Copy(readSamples, 0, frameSamples, sampleCount, received);
                    sampleCount += received;
                    if (sampleCount != frameLength) { continue; }
                    sampleCount = 0;
                    ProcessFrame(current, currentTime, GetFirstSampleTicks());
                    processed++;
                }
                maximumFramesPerPass = Math.Max(maximumFramesPerPass, processed);
                PublishDiagnostics(currentTime);
            }
            catch (Exception exception) {
                Volatile.Write(ref fault, exception.Message);
                StopResources();
                Volatile.Write(ref state, (int)VoiceCaptureState.BackendUnavailable);
                retryAt = currentTime + reconnectInterval;
                PublishDiagnostics(currentTime);
            }
        }

        internal void Configure(VoiceCaptureControl nextControl) {
            Volatile.Write(ref control, nextControl);
        }

        internal void Stop() {
            Interlocked.Exchange(ref stopped, 1);
            InterruptWait();
        }

        internal void ResetDiagnostics() {
            capturedFrames = 0;
            encodedFrames = 0;
            suppressedFrames = 0;
            submittedFrames = 0;
            staleSamples = 0;
            maximumFramesPerPass = 0;
            nextDiagnosticsAt = 0d;
        }

        // Starts a new stream with the frame length that matches how often the device delivers audio.
        private void StartEncoder() {
            frameLength = SelectFrameLength();
            if (frameSamples.Length != frameLength) {
                frameSamples = new float[frameLength];
                readSamples = new float[frameLength];
            }
            encoder = new(frameLength == configuration.SamplesPerFrame ? configuration : new VoiceCodecConfiguration((VoiceFrameLength)frameLength, configuration.BitRate, configuration.Complexity, configuration.ExpectedPacketLossPercent, configuration.UseVariableBitRate, configuration.UseForwardErrorCorrection, configuration.UseDiscontinuousTransmission, configuration.Application));
            sampleCount = 0;
            sequence = 0;
            wasTransmitting = false;
        }

        private int SelectFrameLength() {
            int configured = configuration.SamplesPerFrame;
            int devicePeriod = signal == null ? 0 : signal.DevicePeriodSamples;
            return configured < DEVICE_FALLBACK_FRAME_SAMPLES && devicePeriod * 2 > configured * 3 ? DEVICE_FALLBACK_FRAME_SAMPLES : configured;
        }

        private void DiscardStaleSamples() {
            int queued = capture.QueuedSamples;
            if (queued <= Math.Max(STALE_BACKLOG_SAMPLES, frameLength * 2)) { return; }
            int remaining = queued - frameLength;
            while (remaining > 0) {
                int discarded = capture.Read(readSamples, Math.Min(readSamples.Length, remaining));
                if (discarded == 0) { break; }
                remaining -= discarded;
                staleSamples += discarded;
            }
            sampleCount = 0;
        }

        // Estimated capture time of the frame's first sample: the frame and everything still queued behind it arrived before now.
        private long GetFirstSampleTicks() {
            long samplesBehind = (long)capture.QueuedSamples + frameLength;
            return Stopwatch.GetTimestamp() - samplesBehind * Stopwatch.Frequency / VoiceEncoderSession.SAMPLE_RATE;
        }

        private void ProcessFrame(VoiceCaptureControl current, double currentTime, long firstSampleTicks) {
            capturedFrames++;
            float[] samples = frameSamples;
            if (current.MicrophoneVolume != 1f) { AudioMath.ApplyGain(samples, samples, current.MicrophoneVolume); }
            float decibels = AudioMath.GetDecibels(samples);
            byte networkLevel = AudioMath.GetNetworkLevel(decibels);
            if (decibels >= activationThreshold) { releaseAt = currentTime + activationRelease; }
            bool active = decibels >= activationThreshold || currentTime < releaseAt;
            bool transmit = !current.Muted && (transmissionMode == TransmissionMode.Open || transmissionMode == TransmissionMode.PushToTalk && current.TransmissionHeld || transmissionMode == TransmissionMode.VoiceActivated && active);
            Volatile.Write(ref speaking, transmit && active ? 1 : 0);
            Volatile.Write(ref level, transmit ? networkLevel : 0);
            if (!transmit) {
                wasTransmitting = false;
                return;
            }
            if (!wasTransmitting) {
                wasTransmitting = true;
                talkSpurtFramesRemaining = TALK_SPURT_MARKED_FRAMES;
            }
            encodedFrames++;
            if (!encoder.TryEncode(samples, sequence, networkLevel, out VoiceFrame frame)) {
                suppressedFrames++;
                return;
            }
            sequence = unchecked(sequence + 1);
            frame.SourceClientId = current.SourceClientId;
            frame = frame.WithMembership(current.Membership);
            if (talkSpurtFramesRemaining > 0) {
                frame.StartsTalkSpurt = true;
                talkSpurtFramesRemaining--;
            }
            VoiceCaptureControl latest = Volatile.Read(ref control);
            if (Volatile.Read(ref stopped) != 0 || latest == null || !latest.Enabled || latest.Muted || latest.DeviceId != current.DeviceId || latest.SourceClientId != current.SourceClientId || latest.Membership != current.Membership || transmissionMode == TransmissionMode.PushToTalk && !latest.TransmissionHeld) { return; }
            VoiceLatencyProbe.RecordCapture(frame.StreamId, frame.Sequence, firstSampleTicks);
            if (network.Submit(routeId, frame)) { submittedFrames++; }
        }

        private void StopResources() {
            VoiceEncoderSession previousEncoder = encoder;
            encoder = null;
            try {
                if (previousEncoder != null) { previousEncoder.Dispose(); }
            }
            finally {
                if (captureStarted) {
                    captureStarted = false;
                    capture.Stop();
                }
                sampleCount = 0;
                releaseAt = 0d;
                wasTransmitting = false;
                Volatile.Write(ref speaking, 0);
                Volatile.Write(ref level, 0);
                Volatile.Write(ref state, (int)VoiceCaptureState.Disabled);
            }
        }

        private void PublishDiagnostics(double currentTime) {
            if (currentTime < nextDiagnosticsAt) { return; }
            nextDiagnosticsAt = currentTime + .05d;
            bool running = encoder != null;
            VoiceCaptureDiagnostics snapshot = new(capture.Device.DisplayName, State, capture.SampleRate, capture.QueuedSamples / 48f, (capture.DroppedSamples + staleSamples) / 48f, capturedFrames, encodedFrames, suppressedFrames, submittedFrames, maximumFramesPerPass, capture.Device.Id,
                running ? frameLength / 48f : 0f, running && signal != null ? signal.DevicePeriodSamples / 48f : 0f, running && signal != null && signal.CanSignal);
            Volatile.Write(ref diagnostics, snapshot);
        }

    }

}
