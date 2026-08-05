using System;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [Serializable]
    public struct VoiceCodecSettings {

        [SerializeField] private VoiceFrameLength frameLength;
        [SerializeField, Range(6000, 64000)] private int bitRate;
        [SerializeField, Range(0, 10)] private int complexity;
        [SerializeField, Range(0, 100)] private int expectedPacketLossPercent;
        [SerializeField] private bool useVariableBitRate;
        [SerializeField] private bool useForwardErrorCorrection;
        [SerializeField] private bool useDiscontinuousTransmission;

        public static VoiceCodecSettings Default => new VoiceCodecSettings(
            VoiceFrameLength.TwentyMilliseconds,
            32000,
            8,
            10,
            true,
            true,
            true);

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
            bool useDiscontinuousTransmission) {
            this.frameLength = frameLength;
            this.bitRate = bitRate;
            this.complexity = complexity;
            this.expectedPacketLossPercent = expectedPacketLossPercent;
            this.useVariableBitRate = useVariableBitRate;
            this.useForwardErrorCorrection = useForwardErrorCorrection;
            this.useDiscontinuousTransmission = useDiscontinuousTransmission;
            Validate();
        }

        public void Validate() {
            if (!VoiceFrame.IsSupportedSampleCount((int)frameLength)) {
                frameLength = VoiceFrameLength.TwentyMilliseconds;
            }
            bitRate = Mathf.Clamp(bitRate, 6000, 64000);
            complexity = Mathf.Clamp(complexity, 0, 10);
            expectedPacketLossPercent = Mathf.Clamp(expectedPacketLossPercent, 0, 100);
        }

    }

}
