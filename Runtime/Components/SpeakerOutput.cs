using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Output/Speaker")]
    [DisallowMultipleComponent]
    public sealed class SpeakerOutput : NetworkBehaviour {

        [SerializeField, HideInInspector] private VoiceParticipant participant;

        [Header("References")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioMixerGroup outputMixerGroup;

        [Header("Playback Space")]
        [SerializeField] private SpatializationMode spatialization = SpatializationMode.UnityThreeDimensional;
        [SerializeField, Min(.01f)] private float minDistance = 1.5f;
        [SerializeField, Min(.1f)] private float maxDistance = 25f;

        [Header("Local Playback")]
        [SerializeField, Range(0f, 2f)] private float localVolume = 1f;

        [Header("Buffer")]
        [SerializeField, Range(5, 250), Tooltip("Minimum playback buffer. Playback raises it to cover the audio callback, packet size and measured network jitter.")] private int targetBufferMilliseconds = 10;
        [SerializeField, Range(60, 1000)] private int maxBufferMilliseconds = 80;
        [SerializeField, Range(0f, .1f)] private float maxPitchCorrection = .03f;
        [SerializeField, Range(0f, 2f)] private float pitchCorrectionGain = .5f;

        public readonly Signal<float> OnFrameLevel = new();

        public AudioSource AudioSource => audioSource;
        public SpatializationMode Spatialization => spatialization;
        public float LocalVolume => localVolume;
        public int BufferedSamples => playbackOutput == null ? 0 : playbackOutput.BufferedSamples;
        internal VoicePlaybackOutput PlaybackOutput => playbackOutput;

        private VoiceSession session;
        private VoicePlaybackEngine boundEngine;
        private VoicePlaybackOutput playbackOutput;
        private VoiceAudioRenderer audioRenderer;
        private ulong registeredPlaybackSourceId;
        private byte lastLevel;

        private void Awake() {
            if (!Application.isPlaying) { return; }
            participant = VoiceComponentSetup.EnsureParticipant(gameObject);
            EnsureSetup();
        }

        public override void OnDestroy() {
            StopPlayback();
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned && IsClient) { StartPlayback(); }
        }

        private void OnDisable() {
            StopPlayback();
        }

        private void Update() {
            EnsurePlayback();
            if (playbackOutput == null) { return; }
            playbackOutput.SetMuted(GetIsLocallyMuted());
            byte level = playbackOutput.Level;
            if (lastLevel == level) { return; }
            lastLevel = level;
            OnFrameLevel.Dispatch(level / (float)byte.MaxValue);
        }

        private void Reset() {
            TryGetComponent(out participant);
            targetBufferMilliseconds = 10;
            maxBufferMilliseconds = 80;
            minDistance = 1.5f;
            maxDistance = 25f;
            localVolume = 1f;
            maxPitchCorrection = .03f;
            pitchCorrectionGain = .5f;
            spatialization = string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName()) ? SpatializationMode.UnityThreeDimensional : SpatializationMode.ProjectSpatializer;
            VoiceComponentSetup.RequestParticipantSetup(this, true);
        }

        private void OnValidate() {
            TryGetComponent(out participant);
            targetBufferMilliseconds = Mathf.Clamp(targetBufferMilliseconds, 5, 250);
            maxBufferMilliseconds = Mathf.Clamp(maxBufferMilliseconds, targetBufferMilliseconds + 20, 1000);
            minDistance = Mathf.Max(.01f, minDistance);
            maxDistance = Mathf.Max(maxDistance, minDistance + .1f);
            localVolume = Mathf.Clamp(localVolume, 0f, 2f);
        }

        public override void OnNetworkSpawn() {
            if (IsClient && isActiveAndEnabled) { StartPlayback(); }
        }

        public override void OnNetworkDespawn() {
            StopPlayback();
        }

        public void SetAudioSource(AudioSource newAudioSource) {
            if (audioSource == newAudioSource) { return; }
            bool restart = IsSpawned && IsClient && isActiveAndEnabled;
            StopPlayback();
            audioSource = newAudioSource;
            EnsureSetup();
            if (restart) { StartPlayback(); }
        }

        public void SetSpatialization(SpatializationMode mode) {
            spatialization = mode;
            ConfigureAudioSource();
        }

        public void SetLocalVolume(float volume) {
            if (!float.IsFinite(volume)) { return; }
            localVolume = Mathf.Clamp(volume, 0f, 2f);
            RefreshLocalVolume();
        }

        public void ClearPlayback() {
            if (playbackOutput != null) { playbackOutput.Reset(); }
        }

        public void ResetDiagnostics() {
            if (playbackOutput != null) { playbackOutput.ResetDiagnostics(); }
        }

        public VoicePlaybackDiagnostics GetDiagnostics() {
            if (playbackOutput == null) { return default; }
            playbackOutput.GetBufferDiagnostics(out int prebufferWaits, out int underruns, out int partialReads, out long overflowedSamples);
            int outputSampleRate = AudioSettings.outputSampleRate;
            AudioSettings.GetDSPBufferSize(out int dspBufferSamples, out int dspBufferCount);
            float outputBufferMilliseconds = outputSampleRate > 0 ? dspBufferSamples * dspBufferCount * 1000f / outputSampleRate : 0f;
            return new(playbackOutput.LastCallbackSamples, outputSampleRate, BufferedSamples / 48f, playbackOutput.ReceivedFrames, playbackOutput.QueueDrops, prebufferWaits, underruns, partialReads, overflowedSamples / 48f, playbackOutput.AudioCallbacks, playbackOutput.RenderedSignalSamples,
                playbackOutput.TargetSamples / 48f, playbackOutput.JitterSamples / 48f, (float)playbackOutput.LatencyMilliseconds, outputBufferMilliseconds);
        }

        internal void EnsureSetup(bool resetAudioSource = false) {
            TryGetComponent(out participant);
            bool hadSource = audioSource != null;
            AudioSource playbackSource = VoiceComponentSetup.EnsurePlaybackSource(this, audioSource, "Voice Playback");
            VoiceAudioRenderer playbackRenderer = VoiceAudioRenderer.Ensure(playbackSource);
            VoiceComponentSetup.RecordConfiguration(this);
            audioSource = playbackSource;
            audioRenderer = playbackRenderer;
            if (!hadSource || Application.isPlaying || resetAudioSource) {
                VoiceComponentSetup.RecordConfiguration(audioSource);
                ConfigureAudioSource();
                VoiceComponentSetup.CompleteConfiguration(audioSource);
            }
            VoiceComponentSetup.CompleteConfiguration(this);
        }

        internal void EnsurePlayback() {
            if (!IsSpawned || !IsClient || !isActiveAndEnabled || NetworkManager == null) { return; }
            VoiceSession currentSession = VoiceSession.GetOrCreate(NetworkManager);
            VoicePlaybackEngine currentEngine = currentSession == null ? null : currentSession.PlaybackEngine;
            if (playbackOutput != null && (boundEngine != currentEngine || !playbackOutput.IsActive)) { StopPlayback(); }
            if (playbackOutput == null && currentEngine != null) { StartPlayback(); }
        }

        internal void RefreshLocalVolume() {
            if (playbackOutput != null) { playbackOutput.SetGain(localVolume); }
        }

        internal void ResetPlayback() {
            ClearPlayback();
        }

        internal void RetirePlaybackStream(ulong sourceClientId) {
            if (playbackOutput != null) { playbackOutput.RetireSource(sourceClientId); }
        }

        internal void ReceiveFrame(VoiceFrame frame) {
            if (playbackOutput != null) { playbackOutput.Enqueue(frame, frame.SourceClientId); }
        }

        private void StartPlayback() {
            if (playbackOutput != null) { return; }
            EnsureSetup();
            if (audioSource == null || NetworkManager == null) { return; }
            session = VoiceSession.GetOrCreate(NetworkManager);
            if (session == null || session.PlaybackEngine == null) { return; }
            boundEngine = session.PlaybackEngine;
            playbackOutput = boundEngine.CreateOutput(new(targetBufferMilliseconds, maxBufferMilliseconds, 1, maxPitchCorrection, pitchCorrectionGain));
            if (playbackOutput == null) { return; }
            playbackOutput.SetGain(localVolume);
            playbackOutput.SetMuted(GetIsLocallyMuted());
            audioRenderer.Attach(audioSource, playbackOutput);
            registeredPlaybackSourceId = PlaybackSourceRegistry.Register(audioSource);
        }

        private void StopPlayback() {
            if (playbackOutput != null) {
                playbackOutput.Deactivate();
                playbackOutput = null;
            }
            if (audioRenderer != null) { audioRenderer.Detach(); }
            if (registeredPlaybackSourceId != PlaybackSourceRegistry.NO_SOURCE_ID) { PlaybackSourceRegistry.Unregister(registeredPlaybackSourceId); }
            registeredPlaybackSourceId = PlaybackSourceRegistry.NO_SOURCE_ID;
            session = null;
            boundEngine = null;
            lastLevel = 0;
        }

        private void ConfigureAudioSource() {
            if (audioSource == null) { return; }
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.dopplerLevel = 0f;
            audioSource.minDistance = minDistance;
            audioSource.maxDistance = maxDistance;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.spatializePostEffects = true;
            audioSource.volume = 1f;
            if (outputMixerGroup != null) { audioSource.outputAudioMixerGroup = outputMixerGroup; }
            audioSource.spatialBlend = spatialization == SpatializationMode.TwoDimensional ? 0f : 1f;
            audioSource.spatialize = spatialization == SpatializationMode.ProjectSpatializer;
        }

        private bool GetIsLocallyMuted() {
            if (participant != null && (participant.IsPlaybackMuted || participant.IsOwner && !participant.LocalLoopback)) { return true; }
            return VoiceParticipant.TryGetLocal(NetworkManager, out VoiceParticipant localParticipant) && localParticipant.IsDeafened;
        }

    }

}
