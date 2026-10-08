using NUnit.Framework;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class CircularAudioClipSchedulerTests {

        [Test]
        public void StartAndAdvanceRequestOnlyLeadAndPlayheadProgress() {
            CircularAudioClipScheduler scheduler = new(16, 8);

            CircularAudioClipSchedule startSchedule = scheduler.Start(100);
            Assert.That(startSchedule.WriteStartSample, Is.EqualTo(100));
            Assert.That(startSchedule.WriteSampleCount, Is.EqualTo(8));

            CircularAudioClipSchedule advanceSchedule = scheduler.Advance(103);
            Assert.That(advanceSchedule.ClearStartSample, Is.EqualTo(100));
            Assert.That(advanceSchedule.ClearSampleCount, Is.EqualTo(3));
            Assert.That(advanceSchedule.WriteStartSample, Is.EqualTo(108));
            Assert.That(advanceSchedule.WriteSampleCount, Is.EqualTo(3));
            Assert.That(advanceSchedule.SkippedSampleCount, Is.Zero);

            CircularAudioClipSchedule repeatedSchedule = scheduler.Advance(103);
            Assert.That(repeatedSchedule.ClearSampleCount, Is.Zero);
            Assert.That(repeatedSchedule.WriteSampleCount, Is.Zero);
            Assert.That(repeatedSchedule.SkippedSampleCount, Is.Zero);
        }

        [Test]
        public void AdvanceAcrossClipBoundaryPreservesAbsoluteSchedule() {
            CircularAudioClipScheduler scheduler = new(16, 4);

            CircularAudioClipSchedule startSchedule = scheduler.Start(14);
            Assert.That(startSchedule.WriteStartSample, Is.EqualTo(14));
            Assert.That(startSchedule.WriteSampleCount, Is.EqualTo(4));

            CircularAudioClipSchedule advanceSchedule = scheduler.Advance(17);
            Assert.That(advanceSchedule.ClearStartSample, Is.EqualTo(14));
            Assert.That(advanceSchedule.ClearSampleCount, Is.EqualTo(3));
            Assert.That(advanceSchedule.WriteStartSample, Is.EqualTo(18));
            Assert.That(advanceSchedule.WriteSampleCount, Is.EqualTo(3));
            Assert.That(advanceSchedule.SkippedSampleCount, Is.Zero);
        }

        [Test]
        public void AdvancePastScheduledAudioRebasesAndReportsSkippedSamples() {
            CircularAudioClipScheduler scheduler = new(32, 8);
            scheduler.Start(0);

            CircularAudioClipSchedule hitchSchedule = scheduler.Advance(11);
            Assert.That(hitchSchedule.SkippedSampleCount, Is.EqualTo(3));
            Assert.That(hitchSchedule.WriteStartSample, Is.EqualTo(11));
            Assert.That(hitchSchedule.WriteSampleCount, Is.EqualTo(8));

            CircularAudioClipSchedule recoverySchedule = scheduler.Advance(12);
            Assert.That(recoverySchedule.SkippedSampleCount, Is.Zero);
            Assert.That(recoverySchedule.WriteStartSample, Is.EqualTo(19));
            Assert.That(recoverySchedule.WriteSampleCount, Is.EqualTo(1));
        }

    }

}
