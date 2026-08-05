using System;
using Concentus;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceDecoderSession : IDisposable {

        private const int MAXIMUM_SAMPLES_PER_FRAME = (int)VoiceFrameLength.FortyMilliseconds;

        private readonly IOpusDecoder decoder;
        private readonly byte[] encodedBytes = new byte[VoiceFrame.MAXIMUM_PAYLOAD_BYTES];
        private readonly float[] decodedSamples = new float[MAXIMUM_SAMPLES_PER_FRAME];

        internal VoiceDecoderSession() {
            decoder = OpusCodecFactory.CreateDecoder(VoiceEncoderSession.SAMPLE_RATE, 1);
        }

        public void Dispose() {
            decoder.Dispose();
        }

        internal void ResetState() {
            decoder.ResetState();
        }

        internal ReadOnlySpan<float> Decode(VoiceFrame frame, bool useForwardErrorCorrection) {
            if (!frame.IsValid) { return ReadOnlySpan<float>.Empty; }
            if (frame.IsSilence) {
                Span<float> silenceSamples = decodedSamples.AsSpan(0, frame.SampleCount);
                silenceSamples.Clear();
                return silenceSamples;
            }

            int encodedLength = frame.CopyPayloadTo(encodedBytes);
            int decodedLength = decoder.Decode(
                encodedBytes.AsSpan(0, encodedLength),
                decodedSamples.AsSpan(0, frame.SampleCount),
                frame.SampleCount,
                useForwardErrorCorrection);
            return decodedSamples.AsSpan(0, decodedLength);
        }

        internal ReadOnlySpan<float> Conceal(int sampleCount) {
            if (!VoiceFrame.IsSupportedSampleCount(sampleCount)) { return ReadOnlySpan<float>.Empty; }

            int decodedLength = decoder.Decode(
                ReadOnlySpan<byte>.Empty,
                decodedSamples.AsSpan(0, sampleCount),
                sampleCount,
                false);
            return decodedSamples.AsSpan(0, decodedLength);
        }

    }

}
