using System;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class VoiceAudioRenderer : MonoBehaviour {

        private VoicePlaybackOutput output;
        private AudioSource audioSource;
        private AudioClip silenceClip;
        private int outputSampleRate;

        private void Awake() {
            outputSampleRate = AudioSettings.outputSampleRate;
            AudioSettings.OnAudioConfigurationChanged += HandleAudioConfigurationChanged;
        }

        private void OnDestroy() {
            AudioSettings.OnAudioConfigurationChanged -= HandleAudioConfigurationChanged;
            Detach();
        }

        private void OnAudioFilterRead(float[] samples, int channels) {
            VoicePlaybackOutput current = Volatile.Read(ref output);
            if (current == null) {
                Array.Clear(samples, 0, samples.Length);
                return;
            }
            current.Render(samples, channels, Volatile.Read(ref outputSampleRate));
        }

        internal static VoiceAudioRenderer Ensure(AudioSource source) {
            if (source == null) { return null; }
            if (source.TryGetComponent(out VoiceAudioRenderer renderer)) { return renderer; }
#if UNITY_EDITOR
            renderer = Application.isPlaying ? source.gameObject.AddComponent<VoiceAudioRenderer>() : Undo.AddComponent<VoiceAudioRenderer>(source.gameObject);
#else
            renderer = source.gameObject.AddComponent<VoiceAudioRenderer>();
#endif
            renderer.hideFlags = HideFlags.HideInInspector;
            return renderer;
        }

        internal void Attach(AudioSource source, VoicePlaybackOutput playbackOutput) {
            Detach();
            if (source == null || playbackOutput == null) { return; }
            audioSource = source;
            outputSampleRate = Math.Max(8000, AudioSettings.outputSampleRate);
            silenceClip = AudioClip.Create("Voice Output", outputSampleRate, 1, outputSampleRate, false);
            audioSource.clip = silenceClip;
            audioSource.loop = true;
            audioSource.playOnAwake = false;
            audioSource.pitch = 1f;
            Volatile.Write(ref output, playbackOutput);
            audioSource.Play();
        }

        internal void Detach() {
            Volatile.Write(ref output, null);
            if (audioSource != null && audioSource.clip == silenceClip) {
                audioSource.Stop();
                audioSource.clip = null;
            }
            audioSource = null;
            if (silenceClip == null) { return; }
            if (Application.isPlaying) { Destroy(silenceClip); }
            else { DestroyImmediate(silenceClip); }
            silenceClip = null;
        }

        private void HandleAudioConfigurationChanged(bool deviceWasChanged) {
            Volatile.Write(ref outputSampleRate, AudioSettings.outputSampleRate);
        }

    }

}
