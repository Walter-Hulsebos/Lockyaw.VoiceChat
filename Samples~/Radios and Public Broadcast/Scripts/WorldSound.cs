using System;
using UnityEngine;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat.BroadcastingSample {

    [AddComponentMenu("Lockyaw Voice Chat/World Sound")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public sealed class WorldSound : MonoBehaviour {

        [SerializeField, HideInInspector] private AudioSource audioSource;

        [Header("Tone")]
        [SerializeField, Range(100f, 2000f)] private float frequency = 440f;
        [SerializeField, Range(0f, 1f)] private float volume = .15f;

        private AudioClip audioClip;

        private void Awake() {
            CacheComponentReferences();
            CreateTone();
        }

        private void OnDestroy() {
            if (audioClip != null) {
                Object.Destroy(audioClip);
            }
        }

        private void Reset() {
            CacheComponentReferences();
        }

        private void OnValidate() {
            CacheComponentReferences();
        }

        private void CreateTone() {
            const int SAMPLE_RATE = 48000;
            float[] samples = new float[SAMPLE_RATE];
            for (int i = 0; i < samples.Length; i++) {
                float time = i / (float)SAMPLE_RATE;
                float pulse = time % .5f < .18f ? 1f : 0f;
                samples[i] = MathF.Sin(2f * MathF.PI * frequency * time) * volume * pulse;
            }

            audioClip = AudioClip.Create("Capturable World Tone", samples.Length, 1, SAMPLE_RATE, false);
            audioClip.SetData(samples, 0);
            audioSource.clip = audioClip;
            audioSource.playOnAwake = true;
            audioSource.loop = true;
            audioSource.spatialBlend = 1f;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = 12f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.Play();
        }

        private void CacheComponentReferences() {
            if (audioSource == null) {
                TryGetComponent(out audioSource);
            }
        }

    }

}
