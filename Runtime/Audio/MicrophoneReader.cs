using System;
using UnityEngine;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat {

    internal sealed class MicrophoneReader : IDisposable {

        internal string DeviceName => deviceName;
        internal bool IsRecording => microphoneClip != null && Microphone.IsRecording(deviceName);
        internal int SampleRate => sampleRate;
        internal int ChannelCount => channelCount;
        internal float QueuedMilliseconds => sampleRate <= 0 ? 0f : queuedInputFrames * 1000f / sampleRate;
        internal float DroppedMilliseconds => sampleRate <= 0 ? 0f : droppedInputFrames * 1000f / sampleRate;

        private const int CAPTURE_SECONDS = 1;
        private const int MAXIMUM_QUEUED_FRAMES = 3;

        private readonly string deviceName;
        private readonly float[] frameSamples;
        private float[] captureSamples;
        private AudioClip microphoneClip;
        private int readPosition;
        private int inputFramesPerOutputFrame;
        private int sampleRate;
        private int channelCount;
        private int queuedInputFrames;
        private long droppedInputFrames;

        internal MicrophoneReader(string deviceName, int samplesPerFrame) {
            this.deviceName = deviceName;
            frameSamples = new float[samplesPerFrame];
        }

        public void Dispose() {
            Stop();
        }

        internal bool Start() {
            Stop();
            microphoneClip = Microphone.Start(deviceName, true, CAPTURE_SECONDS, VoiceEncoderSession.SAMPLE_RATE);
            readPosition = 0;
            queuedInputFrames = 0;
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

        internal void Stop() {
            if (Microphone.IsRecording(deviceName)) {
                Microphone.End(deviceName);
            }

            if (microphoneClip != null) {
                Object.Destroy(microphoneClip);
                microphoneClip = null;
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

            int microphonePosition = Microphone.GetPosition(deviceName);
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

    }

}
