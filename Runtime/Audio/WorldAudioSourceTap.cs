using System.Threading;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("")]
    internal sealed class WorldAudioSourceTap : MonoBehaviour {

        internal WorldAudioHistory History => history;

        private readonly WorldAudioHistory history = new();
        private int sourceSampleRate;
        private int readerCount;

        private void Awake() {
            hideFlags = HideFlags.HideInInspector;
            sourceSampleRate = AudioSettings.outputSampleRate;
            AudioSettings.OnAudioConfigurationChanged += HandleAudioConfigurationChanged;
            enabled = false;
        }

        private void OnDestroy() {
            AudioSettings.OnAudioConfigurationChanged -= HandleAudioConfigurationChanged;
        }

        private void OnAudioFilterRead(float[] interleavedSamples, int channelCount) {
            history.WriteInterleaved(interleavedSamples, channelCount, Volatile.Read(ref sourceSampleRate));
        }

        internal static WorldAudioSourceTap Acquire(AudioSource audioSource, out long initialSamplePosition) {
            if (!audioSource.TryGetComponent(out WorldAudioSourceTap sourceTap)) {
                sourceTap = audioSource.gameObject.AddComponent<WorldAudioSourceTap>();
            }
            if (sourceTap.readerCount == 0) {
                sourceTap.history.RequestReset();
                initialSamplePosition = 0;
                sourceTap.enabled = true;
            }
            else {
                initialSamplePosition = sourceTap.history.PublishedSamplePosition;
            }
            sourceTap.readerCount++;
            return sourceTap;
        }

        internal void Release() {
            if (readerCount <= 0) { return; }
            readerCount--;
            if (readerCount == 0) { enabled = false; }
        }

        private void HandleAudioConfigurationChanged(bool deviceWasChanged) {
            Volatile.Write(ref sourceSampleRate, AudioSettings.outputSampleRate);
            history.RequestReset();
        }

    }

}
