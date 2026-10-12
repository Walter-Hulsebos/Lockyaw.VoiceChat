using System;

namespace Lockyaw.VoiceChat {

    internal sealed class DecodedVoiceHistory {

        internal long WritePosition { get; private set; }

        private readonly float[] samples = new float[VoiceEncoderSession.SAMPLE_RATE];

        internal void Write(ReadOnlySpan<float> source) {
            for (int i = 0; i < source.Length; i++) {
                samples[(int)(WritePosition % samples.Length)] = source[i];
                WritePosition++;
            }
        }

        internal void WriteSilence(int count) {
            for (int i = 0; i < Math.Min(count, samples.Length); i++) {
                samples[(int)(WritePosition % samples.Length)] = 0f;
                WritePosition++;
            }
        }

        internal int Mix(ref long cursor, Span<float> destination, float gain) {
            cursor = Math.Clamp(cursor, Math.Max(0L, WritePosition - samples.Length), WritePosition);
            int count = (int)Math.Min(destination.Length, WritePosition - cursor);
            for (int i = 0; i < count; i++) {
                destination[i] += samples[(int)((cursor + i) % samples.Length)] * gain;
            }
            cursor += count;
            return count;
        }

    }

}
