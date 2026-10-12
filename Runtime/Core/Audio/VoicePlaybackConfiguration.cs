using System;

namespace Lockyaw.VoiceChat {

    internal readonly struct VoicePlaybackConfiguration {

        internal int TargetSamples { get; }
        internal int MaximumSamples { get; }
        internal int MaximumStreams { get; }
        internal float MaximumPitchCorrection { get; }
        internal float PitchCorrectionGain { get; }
        internal float ReceiveTimeout { get; }

        internal const int MINIMUM_TARGET_MILLISECONDS = 5;

        // The target is a minimum; playback raises it to cover the device callback, packet size and measured jitter.
        internal VoicePlaybackConfiguration(int targetMilliseconds, int maximumMilliseconds, int maximumStreams = 1, float maximumPitchCorrection = .03f, float pitchCorrectionGain = .5f, float receiveTimeout = .5f) {
            TargetSamples = Math.Clamp(targetMilliseconds, MINIMUM_TARGET_MILLISECONDS, 250) * 48;
            MaximumSamples = Math.Clamp(maximumMilliseconds, TargetSamples / 48 + 20, 1000) * 48;
            MaximumStreams = Math.Clamp(maximumStreams, 1, 16);
            MaximumPitchCorrection = Math.Clamp(maximumPitchCorrection, 0f, .1f);
            PitchCorrectionGain = Math.Clamp(pitchCorrectionGain, 0f, 2f);
            ReceiveTimeout = Math.Clamp(receiveTimeout, .1f, 2f);
        }

    }

}
