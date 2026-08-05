using System;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal static class AudioMath {

        private const float MINIMUM_DECIBELS = -60f;
        private const float MINIMUM_AMPLITUDE = .000001f;

        internal static float GetRootMeanSquare(ReadOnlySpan<float> samples) {
            if (samples.Length == 0) { return 0f; }

            double sum = 0d;
            for (int i = 0; i < samples.Length; i++) {
                float sample = samples[i];
                sum += sample * sample;
            }
            return Mathf.Sqrt((float)(sum / samples.Length));
        }

        internal static float GetDecibels(ReadOnlySpan<float> samples) {
            float rootMeanSquare = Mathf.Max(GetRootMeanSquare(samples), MINIMUM_AMPLITUDE);
            return Mathf.Max(MINIMUM_DECIBELS, 20f * Mathf.Log10(rootMeanSquare));
        }

        internal static byte GetNetworkLevel(ReadOnlySpan<float> samples) => GetNetworkLevel(GetDecibels(samples));

        internal static byte GetNetworkLevel(float decibels) {
            float normalizedLevel = Mathf.InverseLerp(MINIMUM_DECIBELS, 0f, decibels);
            return (byte)Mathf.RoundToInt(normalizedLevel * byte.MaxValue);
        }

    }

}
