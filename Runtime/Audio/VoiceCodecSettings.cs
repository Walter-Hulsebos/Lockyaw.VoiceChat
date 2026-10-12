using System;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [Serializable]
    public struct VoiceCodecSettings {

        [SerializeField] private VoiceCodecApplication application;
        [SerializeField] private VoiceFrameLength frameLength;
        [SerializeField, Range(6000, 64000)] private int bitRate;
        [SerializeField, Range(0, 10)] private int complexity;
        [SerializeField, Range(0, 100)] private int expectedPacketLossPercent;
        [SerializeField] private bool useVariableBitRate;
        [SerializeField] private bool useForwardErrorCorrection;
        [SerializeField] private bool useDiscontinuousTransmission;

        // Capture sends 10 ms frames when the device does not deliver audio at least every 5 ms.
        public static VoiceCodecSettings Default => new VoiceCodecSettings(
            VoiceFrameLength.FiveMilliseconds,
            32000,
            3,
            0,
            true,
            false,
            false);

        public VoiceCodecApplication Application => application;
        public VoiceFrameLength FrameLength => frameLength;
        public int SamplesPerFrame => (int)frameLength;
        public int BitRate => bitRate;
        public int Complexity => complexity;
        public int ExpectedPacketLossPercent => expectedPacketLossPercent;
        public bool UseVariableBitRate => useVariableBitRate;
        public bool UseForwardErrorCorrection => useForwardErrorCorrection;
        public bool UseDiscontinuousTransmission => useDiscontinuousTransmission;

        public VoiceCodecSettings(
            VoiceFrameLength frameLength,
            int bitRate,
            int complexity,
            int expectedPacketLossPercent,
            bool useVariableBitRate,
            bool useForwardErrorCorrection,
            bool useDiscontinuousTransmission,
            VoiceCodecApplication application = VoiceCodecApplication.LowDelay) {
            this.application = application;
            this.frameLength = frameLength;
            this.bitRate = bitRate;
            this.complexity = complexity;
            this.expectedPacketLossPercent = expectedPacketLossPercent;
            this.useVariableBitRate = useVariableBitRate;
            this.useForwardErrorCorrection = useForwardErrorCorrection;
            this.useDiscontinuousTransmission = useDiscontinuousTransmission;
            Validate();
        }

        public VoiceCodecConfiguration ToConfiguration() => new(FrameLength, BitRate, Complexity, ExpectedPacketLossPercent, UseVariableBitRate, UseForwardErrorCorrection, UseDiscontinuousTransmission, Application);

        public void Validate() {
            if (application != VoiceCodecApplication.VoiceOptimized) {
                application = VoiceCodecApplication.LowDelay;
                useForwardErrorCorrection = false;
                useDiscontinuousTransmission = false;
            }
            if (!VoiceFrame.IsSupportedSampleCount((int)frameLength) || application == VoiceCodecApplication.VoiceOptimized && frameLength < VoiceFrameLength.TenMilliseconds) {
                frameLength = VoiceFrameLength.TenMilliseconds;
            }
            bitRate = Mathf.Clamp(bitRate, 6000, 64000);
            complexity = Mathf.Clamp(complexity, 0, 10);
            expectedPacketLossPercent = Mathf.Clamp(expectedPacketLossPercent, 0, 100);
        }

    }

}
