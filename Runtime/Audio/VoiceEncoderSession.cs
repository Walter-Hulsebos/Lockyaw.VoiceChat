using System;
using Concentus;
using Concentus.Enums;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceEncoderSession : IDisposable {

        internal const int SAMPLE_RATE = 48000;

        private readonly IOpusEncoder encoder;
        private readonly byte[] encodedBytes = new byte[VoiceFrame.MAXIMUM_PAYLOAD_BYTES];
        private readonly int samplesPerFrame;
        private readonly uint streamId;
        private readonly bool usesDiscontinuousTransmission;
        private uint sampleTimestamp;

        internal VoiceEncoderSession(VoiceCodecSettings settings) {
            streamId = unchecked((uint)Guid.NewGuid().GetHashCode());
            encoder = OpusCodecFactory.CreateEncoder(SAMPLE_RATE, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = settings.BitRate;
            encoder.Complexity = settings.Complexity;
            encoder.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
            encoder.PacketLossPercent = settings.ExpectedPacketLossPercent;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            encoder.UseDTX = settings.UseDiscontinuousTransmission;
            encoder.UseInbandFEC = settings.UseForwardErrorCorrection;
            encoder.UseVBR = settings.UseVariableBitRate;
            samplesPerFrame = settings.SamplesPerFrame;
            usesDiscontinuousTransmission = settings.UseDiscontinuousTransmission;
        }

        public void Dispose() {
            encoder.Dispose();
        }

        internal void ResetState() {
            encoder.ResetState();
        }

        internal bool TryEncode(ReadOnlySpan<float> samples, uint sequence, byte level, out VoiceFrame frame) {
            if (samples.Length != samplesPerFrame) {
                throw new ArgumentException($"Expected {samplesPerFrame} samples but received {samples.Length}.", nameof(samples));
            }

            int encodedLength = encoder.Encode(samples, samplesPerFrame, encodedBytes, encodedBytes.Length);
            uint frameSampleTimestamp = sampleTimestamp;
            sampleTimestamp = unchecked(sampleTimestamp + (uint)samplesPerFrame);
            if (usesDiscontinuousTransmission && encodedLength <= 2) {
                frame = default;
                return false;
            }

            frame = new VoiceFrame(
                streamId,
                sequence,
                frameSampleTimestamp,
                (ushort)samplesPerFrame,
                level,
                encodedBytes.AsSpan(0, encodedLength),
                new ConversationMembership(ConversationMembership.EVERYONE, 0));
            return true;
        }

    }

}
