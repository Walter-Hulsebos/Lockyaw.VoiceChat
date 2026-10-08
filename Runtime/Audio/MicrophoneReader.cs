using System;
using UnityEngine;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat {

    internal sealed class MicrophoneReader : IDisposable {

        internal string DeviceName => deviceName;
        internal bool IsRecording => GetIsRecording();
        internal int SampleRate => sampleRate;
        internal int ChannelCount => channelCount;
        internal float QueuedMilliseconds => sampleRate <= 0 ? 0f : queuedInputFrames * 1000f / sampleRate;
        internal float DroppedMilliseconds => sampleRate <= 0 ? 0f : droppedInputFrames * 1000f / sampleRate;

        private const int CAPTURE_SECONDS = 1;
        private const int MAXIMUM_QUEUED_FRAMES = 3;

        private readonly string deviceName;
        private readonly float[] frameSamples;
        private readonly IMicrophoneApi microphoneApi;
        private float[] captureSamples;
        private AudioClip microphoneClip;
        private int readPosition;
        private int inputFramesPerOutputFrame;
        private int sampleRate;
        private int channelCount;
        private int queuedInputFrames;
        private long droppedInputFrames;

        internal MicrophoneReader(string deviceName, int samplesPerFrame)
            : this(deviceName, samplesPerFrame, UnityMicrophoneApi.Instance) { }

        internal MicrophoneReader(string deviceName, int samplesPerFrame, IMicrophoneApi microphoneApi) {
            this.deviceName = deviceName;
            frameSamples = new float[samplesPerFrame];
            this.microphoneApi = microphoneApi != null ? microphoneApi : throw new ArgumentNullException(nameof(microphoneApi));
        }

        public void Dispose() {
            Stop();
        }

        internal bool Start() {
            Stop();
            if (!MicrophoneDevices.ContainsDevice(microphoneApi, deviceName)) { return false; }

            try {
                microphoneClip = microphoneApi.Start(deviceName, CAPTURE_SECONDS, VoiceEncoderSession.SAMPLE_RATE);
                droppedInputFrames = 0;
                if (microphoneClip == null) { return false; }

                sampleRate = microphoneClip.frequency;
                channelCount = microphoneClip.channels;
                if (sampleRate <= 0 || channelCount <= 0) {
                    Stop();
                    return false;
                }

                inputFramesPerOutputFrame = Math.Max(1, (int)Math.Round(frameSamples.Length * sampleRate / (double)VoiceEncoderSession.SAMPLE_RATE));
                int lookaheadFrames = sampleRate == VoiceEncoderSession.SAMPLE_RATE ? 0 : 1;
                captureSamples = channelCount == 1 && lookaheadFrames == 0
                    ? null
                    : new float[(inputFramesPerOutputFrame + lookaheadFrames) * channelCount];
                return true;
            }
            catch (Exception) {
                Stop();
                return false;
            }
        }

        internal void Stop() {
            if (microphoneClip != null) {
                try {
                    if (IsRecording) {
                        microphoneApi.End(deviceName);
                    }
                }
                catch (Exception) { }
                finally {
                    Object.Destroy(microphoneClip);
                    microphoneClip = null;
                }
            }
            readPosition = 0;
            queuedInputFrames = 0;
            inputFramesPerOutputFrame = 0;
            sampleRate = 0;
            channelCount = 0;
            captureSamples = null;
        }

        internal bool TryReadFrame(out ReadOnlySpan<float> samples) {
            samples = ReadOnlySpan<float>.Empty;
            if (!IsRecording) { return false; }

            int microphonePosition;
            try {
                microphonePosition = microphoneApi.GetPosition(deviceName);
            }
            catch (Exception) {
                return false;
            }
            if (microphonePosition < 0) { return false; }

            int availableInputFrames = microphonePosition >= readPosition
                ? microphonePosition - readPosition
                : microphoneClip.samples - readPosition + microphonePosition;
            int maxQueuedInputFrames = inputFramesPerOutputFrame * MAXIMUM_QUEUED_FRAMES;
            if (availableInputFrames > maxQueuedInputFrames) {
                int droppedFrames = availableInputFrames - maxQueuedInputFrames;
                droppedInputFrames += droppedFrames;
                readPosition = microphonePosition - maxQueuedInputFrames;
                if (readPosition < 0) {
                    readPosition += microphoneClip.samples;
                }
                availableInputFrames = maxQueuedInputFrames;
            }

            queuedInputFrames = availableInputFrames;
            int requiredInputFrames = inputFramesPerOutputFrame + (sampleRate == VoiceEncoderSession.SAMPLE_RATE ? 0 : 1);
            if (availableInputFrames < requiredInputFrames) { return false; }

            if (captureSamples == null) {
                if (!microphoneClip.GetData(frameSamples, readPosition)) { return false; }
            }
            else {
                if (!microphoneClip.GetData(captureSamples, readPosition)) { return false; }
                ConvertCaptureSamples();
            }

            readPosition = (readPosition + inputFramesPerOutputFrame) % microphoneClip.samples;
            queuedInputFrames = availableInputFrames - inputFramesPerOutputFrame;
            samples = frameSamples;
            return true;
        }

        internal void ResetDiagnostics() {
            droppedInputFrames = 0;
        }

        private void ConvertCaptureSamples() {
            double sourceFrameScale = inputFramesPerOutputFrame / (double)frameSamples.Length;
            int maximumSourceFrameIndex = captureSamples.Length / channelCount - 1;
            for (int outputIndex = 0; outputIndex < frameSamples.Length; outputIndex++) {
                double sourceFramePosition = outputIndex * sourceFrameScale;
                int firstSourceFrameIndex = Math.Min((int)sourceFramePosition, maximumSourceFrameIndex);
                int secondSourceFrameIndex = Math.Min(firstSourceFrameIndex + 1, maximumSourceFrameIndex);
                float interpolation = (float)(sourceFramePosition - firstSourceFrameIndex);
                float firstSample = GetMonoSample(firstSourceFrameIndex);
                float secondSample = GetMonoSample(secondSourceFrameIndex);
                frameSamples[outputIndex] = firstSample + (secondSample - firstSample) * interpolation;
            }
        }

        private float GetMonoSample(int sourceFrameIndex) {
            int sampleIndex = sourceFrameIndex * channelCount;
            float sample = 0f;
            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++) {
                sample += captureSamples[sampleIndex + channelIndex];
            }
            return sample / channelCount;
        }

        private bool GetIsRecording() {
            if (microphoneClip == null || !MicrophoneDevices.ContainsDevice(microphoneApi, deviceName)) { return false; }

            try {
                return microphoneApi.IsRecording(deviceName);
            }
            catch (Exception) {
                return false;
            }
        }

    }

}
