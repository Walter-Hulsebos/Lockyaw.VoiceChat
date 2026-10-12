using System;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal sealed class WorldAudioSourceReader : IDisposable {

        internal AudioSource AudioSource { get; private set; }
        internal WorldAudioHistory History { get; private set; }
        internal long InitialSamplePosition { get; private set; }

        private WorldAudioSourceTap sourceTap;

        public void Dispose() {
            if (sourceTap != null) { sourceTap.Release(); }
            sourceTap = null;
            AudioSource = null;
            History = null;
            InitialSamplePosition = WorldAudioHistory.UNASSIGNED_SAMPLE_POSITION;
        }

        internal void Initialize(AudioSource audioSource) {
            Dispose();
            if (audioSource == null) { return; }
            AudioSource = audioSource;
            sourceTap = WorldAudioSourceTap.Acquire(audioSource, out long initialPosition);
            History = sourceTap.History;
            InitialSamplePosition = initialPosition;
        }

    }

}
