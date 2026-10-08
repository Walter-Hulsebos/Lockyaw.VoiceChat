using NUnit.Framework;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class SampleRingBufferTests {

        [Test]
        public void ReadAfterUnderflowDoesNotWaitForPrebufferAgain() {
            SampleRingBuffer buffer = new(64, 8);
            float[] initialSamples = CreateSamples(16, 1f);
            float[] outputSamples = new float[4];
            buffer.Write(initialSamples);

            for (int i = 0; i < 4; i++) {
                Assert.That(buffer.Read(outputSamples), Is.EqualTo(4));
            }
            Assert.That(buffer.Read(outputSamples), Is.Zero);

            buffer.Write(CreateSamples(2, 17f));
            Assert.That(buffer.Read(outputSamples), Is.EqualTo(2));
            Assert.That(outputSamples[0], Is.Not.Zero);
            Assert.That(buffer.PrebufferWaitCount, Is.Zero);
            Assert.That(buffer.TakeTrailingSilenceSampleCount(), Is.EqualTo(6));
        }

        [Test]
        public void ClearRequiresInitialPrebufferAgain() {
            SampleRingBuffer buffer = new(64, 8);
            float[] outputSamples = new float[4];
            buffer.Write(CreateSamples(8, 1f));
            Assert.That(buffer.Read(outputSamples), Is.EqualTo(4));

            buffer.Clear();
            buffer.Write(CreateSamples(2, 9f));
            Assert.That(buffer.Read(outputSamples), Is.Zero);
            Assert.That(buffer.PrebufferWaitCount, Is.EqualTo(1));
        }

        [Test]
        public void ConfiguredReserveSurvivesBatchedProduction() {
            SampleRingBuffer buffer = new(64, 10);
            float[] startSamples = CreateSamples(10, 1f);
            float[] initialOutputSamples = new float[4];
            float[] outputSamples = new float[1];
            buffer.Write(startSamples);
            Assert.That(buffer.Read(initialOutputSamples), Is.EqualTo(4));

            for (int i = 0; i < 24; i++) {
                Assert.That(buffer.Read(outputSamples), Is.EqualTo(1));
                if ((i + 1) % 4 == 0) {
                    buffer.Write(CreateSamples(4, 20f + i));
                }
            }

            Assert.That(buffer.RebufferCount, Is.Zero);
            Assert.That(buffer.PartialReadCount, Is.Zero);
            Assert.That(buffer.OverflowedSampleCount, Is.Zero);
        }

        private static float[] CreateSamples(int sampleCount, float firstSample) {
            float[] samples = new float[sampleCount];
            for (int i = 0; i < samples.Length; i++) {
                samples[i] = firstSample + i;
            }
            return samples;
        }

    }

}
