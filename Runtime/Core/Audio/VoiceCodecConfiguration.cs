using System;

namespace Lockyaw.VoiceChat {

    public readonly struct VoiceCodecConfiguration {

        public VoiceCodecApplication Application { get; }
        public VoiceFrameLength FrameLength { get; }
        public int SamplesPerFrame => (int)FrameLength;
        public int BitRate { get; }
        public int Complexity { get; }
        public int ExpectedPacketLossPercent { get; }
        public bool UseVariableBitRate { get; }
        public bool UseForwardErrorCorrection { get; }
        public bool UseDiscontinuousTransmission { get; }

        public VoiceCodecConfiguration(VoiceFrameLength frameLength, int bitRate, int complexity, int expectedPacketLossPercent, bool useVariableBitRate, bool useForwardErrorCorrection, bool useDiscontinuousTransmission, VoiceCodecApplication application = VoiceCodecApplication.LowDelay) {
            Application = application == VoiceCodecApplication.VoiceOptimized ? VoiceCodecApplication.VoiceOptimized : VoiceCodecApplication.LowDelay;
            FrameLength = VoiceFrame.IsSupportedSampleCount((int)frameLength) ? frameLength : VoiceFrameLength.TenMilliseconds;
            // Speech coding, FEC and DTX need frames of at least 10 ms.
            if (Application == VoiceCodecApplication.VoiceOptimized && FrameLength < VoiceFrameLength.TenMilliseconds) { FrameLength = VoiceFrameLength.TenMilliseconds; }
            BitRate = Math.Clamp(bitRate, 6000, 64000);
            Complexity = Math.Clamp(complexity, 0, 10);
            ExpectedPacketLossPercent = Math.Clamp(expectedPacketLossPercent, 0, 100);
            UseVariableBitRate = useVariableBitRate;
            UseForwardErrorCorrection = Application == VoiceCodecApplication.VoiceOptimized && useForwardErrorCorrection;
            UseDiscontinuousTransmission = Application == VoiceCodecApplication.VoiceOptimized && useDiscontinuousTransmission;
        }

    }

}
