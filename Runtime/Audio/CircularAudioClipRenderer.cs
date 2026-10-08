using System;
using UnityEngine;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat {

    internal sealed class CircularAudioClipRenderer : IDisposable {

        internal delegate int SampleProvider(Span<float> outputSamples);

        internal int BufferedContentSamples => hasStarted
            ? (int)Math.Clamp(contentScheduledPosition - readPosition, 0L, clipSamples)
            : 0;
        internal int RenderQuantumSamples => renderSamples.Length;
        internal int ScheduledLeadSamples => scheduler.ScheduledLeadSamples;
        internal long RebaseCount => rebaseCount;
        internal long UnderrunCount => underrunCount;
        internal bool HasStarted => hasStarted;

        private const int CLIP_SECONDS = 2;
        private const int RENDER_QUANTUM_SAMPLES = (int)VoiceFrameLength.TwentyMilliseconds;

        private readonly AudioSource audioSource;
        private readonly AudioClip playbackClip;
        private readonly CircularAudioClipScheduler scheduler;
        private readonly SampleProvider sampleProvider;
        private readonly float[] renderSamples = new float[RENDER_QUANTUM_SAMPLES];
        private readonly float[] silenceSamples = new float[RENDER_QUANTUM_SAMPLES];
        private readonly int clipSamples;
        private readonly int targetLeadSamples;
        private long readPosition;
        private long contentScheduledPosition;
        private long underrunCount;
        private long rebaseCount;
        private int previousTimeSamples;
        private double previousDspTime;
        private bool hasStarted;

        internal CircularAudioClipRenderer(
            AudioSource audioSource,
            string clipName,
            int targetLeadSamples,
            int maxBufferSamples,
            SampleProvider sampleProvider) {
            this.audioSource = audioSource != null ? audioSource : throw new ArgumentNullException(nameof(audioSource));
            this.sampleProvider = sampleProvider != null ? sampleProvider : throw new ArgumentNullException(nameof(sampleProvider));
            this.targetLeadSamples = Math.Max(1, targetLeadSamples);
            clipSamples = Math.Max(
                VoiceEncoderSession.SAMPLE_RATE * CLIP_SECONDS,
                maxBufferSamples + this.targetLeadSamples * 2 + RENDER_QUANTUM_SAMPLES);
            playbackClip = AudioClip.Create(
                clipName,
                clipSamples,
                1,
                VoiceEncoderSession.SAMPLE_RATE,
                false);
            scheduler = new CircularAudioClipScheduler(clipSamples, this.targetLeadSamples);
            audioSource.Stop();
            audioSource.clip = playbackClip;
        }

        public void Dispose() {
            audioSource.Stop();
            if (audioSource.clip == playbackClip) {
                audioSource.clip = null;
            }
            Object.Destroy(playbackClip);
        }

        internal void Update(bool canStart) {
            if (!hasStarted) {
                if (!canStart) { return; }
                Start();
                return;
            }

            AdvanceReadPosition();
            CircularAudioClipSchedule schedule = scheduler.Advance(readPosition);
            ClearConsumedSamples(schedule.ClearStartSample, schedule.ClearSampleCount);
            if (schedule.SkippedSampleCount > 0L) {
                underrunCount++;
                rebaseCount++;
                contentScheduledPosition = Math.Max(contentScheduledPosition, readPosition);
            }
            if (schedule.WriteSampleCount > 0) {
                RenderAndSchedule(schedule.WriteStartSample, schedule.WriteSampleCount);
            }
        }

        internal void Reset() {
            audioSource.Stop();
            ClearClip();
            audioSource.timeSamples = 0;
            audioSource.pitch = 1f;
            readPosition = 0L;
            contentScheduledPosition = 0L;
            scheduler.Reset();
            previousTimeSamples = 0;
            previousDspTime = AudioSettings.dspTime;
            hasStarted = false;
        }

        internal void ResetDiagnostics() {
            underrunCount = 0L;
            rebaseCount = 0L;
        }

        private void Start() {
            ClearClip();
            readPosition = 0L;
            contentScheduledPosition = 0L;
            CircularAudioClipSchedule schedule = scheduler.Start(0L);
            RenderAndSchedule(schedule.WriteStartSample, schedule.WriteSampleCount);
            audioSource.timeSamples = 0;
            previousTimeSamples = 0;
            previousDspTime = AudioSettings.dspTime;
            audioSource.Play();
            hasStarted = true;
        }

        private void AdvanceReadPosition() {
            int currentTimeSamples = audioSource.timeSamples;
            double currentDspTime = AudioSettings.dspTime;
            double elapsedDspTime = Math.Max(0d, currentDspTime - previousDspTime);
            long expectedAdvance = (long)Math.Round(
                elapsedDspTime * VoiceEncoderSession.SAMPLE_RATE * Math.Max(.01f, audioSource.pitch));
            long rawAdvance = currentTimeSamples - previousTimeSamples;
            long wrapCount = Math.Max(0L, (expectedAdvance - rawAdvance + clipSamples / 2L) / clipSamples);
            long sampleAdvance = rawAdvance + wrapCount * clipSamples;
            if (sampleAdvance < 0L) {
                sampleAdvance += clipSamples;
            }
            if (sampleAdvance > clipSamples) {
                rebaseCount++;
                sampleAdvance = Math.Min(expectedAdvance, clipSamples);
            }

            readPosition += sampleAdvance;
            previousTimeSamples = currentTimeSamples;
            previousDspTime = currentDspTime;
        }

        private void RenderAndSchedule(long writePosition, int sampleCount) {
            int remainingSamples = sampleCount;
            while (remainingSamples > 0) {
                int currentSampleCount = Math.Min(remainingSamples, renderSamples.Length);
                Span<float> currentSamples = renderSamples.AsSpan(0, currentSampleCount);
                currentSamples.Clear();
                int providedSampleCount = Math.Clamp(sampleProvider(currentSamples), 0, currentSampleCount);
                WriteSamples(currentSamples, writePosition);
                if (providedSampleCount > 0) {
                    contentScheduledPosition = writePosition + providedSampleCount;
                }
                writePosition += currentSampleCount;
                remainingSamples -= currentSampleCount;
            }
        }

        private void ClearConsumedSamples(long clearPosition, int sampleCount) {
            if (sampleCount <= 0) { return; }
            if (sampleCount >= clipSamples) {
                ClearClip();
                return;
            }

            int remainingSamples = sampleCount;
            while (remainingSamples > 0) {
                int currentSampleCount = Math.Min(remainingSamples, silenceSamples.Length);
                WriteSamples(silenceSamples.AsSpan(0, currentSampleCount), clearPosition);
                clearPosition += currentSampleCount;
                remainingSamples -= currentSampleCount;
            }
        }

        private void ClearClip() {
            long clearPosition = 0L;
            while (clearPosition < clipSamples) {
                int currentSampleCount = (int)Math.Min(silenceSamples.Length, clipSamples - clearPosition);
                WriteSamples(silenceSamples.AsSpan(0, currentSampleCount), clearPosition);
                clearPosition += currentSampleCount;
            }
        }

        private void WriteSamples(ReadOnlySpan<float> samples, long absolutePosition) {
            int clipOffset = (int)(absolutePosition % clipSamples);
            int firstWriteCount = Math.Min(samples.Length, clipSamples - clipOffset);
            playbackClip.SetData(samples.Slice(0, firstWriteCount), clipOffset);

            int secondWriteCount = samples.Length - firstWriteCount;
            if (secondWriteCount > 0) {
                playbackClip.SetData(samples.Slice(firstWriteCount, secondWriteCount), 0);
            }
        }

    }

}
