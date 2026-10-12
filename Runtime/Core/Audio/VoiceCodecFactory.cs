using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace Lockyaw.VoiceChat {

    internal static class VoiceCodecFactory {

        // The package ships managed Opus only; the general factory probes for unbundled native libraries.
#pragma warning disable CS0618
        internal static IOpusEncoder CreateEncoder(VoiceCodecApplication application) => new OpusEncoder(VoiceEncoderSession.SAMPLE_RATE, 1, application == VoiceCodecApplication.VoiceOptimized ? OpusApplication.OPUS_APPLICATION_VOIP : OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);

        internal static IOpusDecoder CreateDecoder() => new OpusDecoder(VoiceEncoderSession.SAMPLE_RATE, 1);
#pragma warning restore CS0618

    }

}
