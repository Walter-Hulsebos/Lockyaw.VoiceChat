using System;

namespace Lockyaw.VoiceChat {

    internal static class AudioMath {

        private const float MINIMUM_DECIBELS = -60f;
        private const float MINIMUM_AMPLITUDE = .000001f;

        internal static void ApplyGain(ReadOnlySpan<float> samples, Span<float> output, float gain) {
            for (int i = 0; i < samples.Length; i++) {
                output[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
            }
        }

        internal static float GetRootMeanSquare(ReadOnlySpan<float> samples) {
            if (samples.Length == 0) { return 0f; }

            double sum = 0d;
            for (int i = 0; i < samples.Length; i++) {
                float sample = samples[i];
                sum += sample * sample;
            }
            return MathF.Sqrt((float)(sum / samples.Length));
        }

        internal static float GetDecibels(ReadOnlySpan<float> samples) {
            float rootMeanSquare = Math.Max(GetRootMeanSquare(samples), MINIMUM_AMPLITUDE);
            return Math.Max(MINIMUM_DECIBELS, 20f * MathF.Log10(rootMeanSquare));
        }

        internal static byte GetNetworkLevel(ReadOnlySpan<float> samples) => GetNetworkLevel(GetDecibels(samples));

        internal static byte GetNetworkLevel(float decibels) {
            float normalizedLevel = Math.Clamp((decibels - MINIMUM_DECIBELS) / -MINIMUM_DECIBELS, 0f, 1f);
            return (byte)MathF.Round(normalizedLevel * byte.MaxValue);
        }

    }

}
