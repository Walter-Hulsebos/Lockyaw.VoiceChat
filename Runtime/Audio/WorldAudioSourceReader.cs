using System;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal sealed class WorldAudioSourceReader : IDisposable {

        internal AudioSource AudioSource { get; private set; }

        private WorldAudioSourceTap sourceTap;
        private long nextSamplePosition;

        internal WorldAudioSourceReader() { }

        public void Dispose() {
            if (sourceTap != null) {
                sourceTap.Release();
                sourceTap = null;
            }
            AudioSource = null;
            nextSamplePosition = WorldAudioSourceTap.UNASSIGNED_SAMPLE_POSITION;
        }

        internal void Initialize(AudioSource audioSource) {
            Dispose();
            AudioSource = audioSource;
            sourceTap = WorldAudioSourceTap.Acquire(audioSource, out nextSamplePosition);
        }

        internal bool Read(Span<float> destination, int maximumBufferedFrameCount) {
            if (sourceTap != null && AudioSource != null) {
                return sourceTap.Read(ref nextSamplePosition, destination, maximumBufferedFrameCount);
            }

            destination.Clear();
            return false;
        }

    }
}
