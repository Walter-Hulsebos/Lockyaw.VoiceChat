#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Channel Speaker")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(ChannelTuner))]
    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    public sealed class ChannelSpeaker : NetworkBehaviour {

        [SerializeField, HideInInspector] private ChannelTuner tuner;

        [Header("References")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioMixerGroup outputMixerGroup;

        [Header("Playback Space")]
        [SerializeField] private SpatializationMode spatialization = SpatializationMode.UnityThreeDimensional;
        [SerializeField, Min(.01f)] private float minDistance = 1.5f;
        [SerializeField, Min(.1f)] private float maxDistance = 25f;

        [Header("Routing")]
        [SerializeField] private bool suppressOriginClient = true;
        [SerializeField, Range(1, 16)] private int maxConcurrentStreams = 8;
        [SerializeField, Range(.1f, 2f)] private float receiveTimeout = .5f;

        [Header("Buffer")]
        [SerializeField, Range(5, 250), Tooltip("Minimum playback buffer. Playback raises it to cover the audio callback, packet size and measured network jitter.")] private int targetBufferMilliseconds = 10;
        [SerializeField, Range(60, 1000)] private int maxBufferMilliseconds = 80;

        public readonly Signal<float> OnFrameLevel = new();

        public AudioSource AudioSource => audioSource;
        public SpatializationMode Spatialization => spatialization;
        public int ActiveStreams => playbackOutput == null ? 0 : playbackOutput.ActiveStreams;
        public int Channel => tuner == null ? BroadcastRoute.NO_CHANNEL : tuner.CurrentChannel;
        internal static IReadOnlyList<ChannelSpeaker> ActiveSpeakers => activeSpeakers;
        internal VoicePlaybackOutput PlaybackOutput => playbackOutput;

        private static readonly List<ChannelSpeaker> activeSpeakers = new(8);

        private VoiceSession session;
        private VoicePlaybackEngine boundEngine;
        private VoicePlaybackOutput playbackOutput;
        private VoiceAudioRenderer audioRenderer;
        private ulong registeredPlaybackSourceId;
        private byte lastLevel;

        private void Awake() {
            if (!Application.isPlaying) { return; }
            EnsureSetup();
            if (tuner != null) { tuner.OnChannelChanged.Listen(HandleChannelChanged); }
        }

        public override void OnDestroy() {
            if (tuner != null) { tuner.OnChannelChanged.Unlisten(HandleChannelChanged); }
            UnregisterSpeaker();
            StopPlayback();
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) {
                RegisterSpeaker();
                if (IsClient) { StartPlayback(); }
            }
        }

        private void OnDisable() {
            UnregisterSpeaker();
            StopPlayback();
        }

        private void Update() {
            EnsurePlayback();
            if (playbackOutput == null) { return; }
            playbackOutput.ConfigureRoute(Channel, NetworkObjectId, NetworkManager.LocalClientId, suppressOriginClient);
            bool deafened = VoiceParticipant.TryGetLocal(NetworkManager, out VoiceParticipant participant) && participant.IsDeafened;
            playbackOutput.SetMuted(deafened);
            byte level = playbackOutput.Level;
            if (lastLevel == level) { return; }
            lastLevel = level;
            OnFrameLevel.Dispatch(level / (float)byte.MaxValue);
        }

        private void Reset() {
            TryGetComponent(out tuner);
            spatialization = string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName()) ? SpatializationMode.UnityThreeDimensional : SpatializationMode.ProjectSpatializer;
            minDistance = 1.5f;
            maxDistance = 25f;
            suppressOriginClient = true;
            maxConcurrentStreams = 8;
            receiveTimeout = .5f;
            targetBufferMilliseconds = 10;
            maxBufferMilliseconds = 80;
            VoiceComponentSetup.RequestChannelSetup(this);
        }

        private void OnValidate() {
            TryGetComponent(out tuner);
            minDistance = Mathf.Max(.01f, minDistance);
            maxDistance = Mathf.Max(maxDistance, minDistance + .1f);
            maxConcurrentStreams = Mathf.Clamp(maxConcurrentStreams, 1, 16);
            receiveTimeout = Mathf.Clamp(receiveTimeout, .1f, 2f);
            targetBufferMilliseconds = Mathf.Clamp(targetBufferMilliseconds, 5, 250);
            maxBufferMilliseconds = Mathf.Clamp(maxBufferMilliseconds, targetBufferMilliseconds + 20, 1000);
        }

        public override void OnNetworkSpawn() {
            RegisterSpeaker();
            if (IsClient && isActiveAndEnabled) { StartPlayback(); }
        }

        public override void OnNetworkDespawn() {
            UnregisterSpeaker();
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

        public void ClearPlayback() {
            if (playbackOutput != null) { playbackOutput.Reset(); }
        }

        internal void EnsureSetup(bool resetAudioSource = false) {
            TryGetComponent(out tuner);
            bool hadSource = audioSource != null;
            AudioSource playbackSource = VoiceComponentSetup.EnsurePlaybackSource(this, audioSource, "Broadcast Playback");
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

        internal static void CollectListenerClientIds(NetworkManager networkManager, int channel, NativeList<ulong> results) {
            results.Clear();
            if (networkManager == null || networkManager.SpawnManager == null || channel < 0) { return; }
            IReadOnlyList<ulong> clients = networkManager.ConnectedClientsIds;
            for (int i = 0; i < activeSpeakers.Count; i++) {
                ChannelSpeaker speaker = activeSpeakers[i];
                if (speaker == null || speaker.NetworkManager != networkManager || !speaker.IsSpawned || !speaker.isActiveAndEnabled || speaker.Channel != channel) { continue; }
                for (int clientIndex = 0; clientIndex < clients.Count; clientIndex++) {
                    ulong clientId = clients[clientIndex];
                    if (speaker.NetworkObject.IsNetworkVisibleTo(clientId) && !ContainsClientId(results, clientId)) { results.Add(clientId); }
                }
            }
        }

        internal static bool IsPlaybackSource(AudioSource candidate) => PlaybackSourceRegistry.Contains(candidate);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            activeSpeakers.Clear();
        }

        private void HandleChannelChanged(int channel) {
            if (playbackOutput != null) { playbackOutput.ConfigureRoute(channel, NetworkObjectId, NetworkManager.LocalClientId, suppressOriginClient); }
        }

        private void StartPlayback() {
            if (playbackOutput != null) { return; }
            EnsureSetup();
            if (audioSource == null || NetworkManager == null) { return; }
            session = VoiceSession.GetOrCreate(NetworkManager);
            if (session == null || session.PlaybackEngine == null) { return; }
            boundEngine = session.PlaybackEngine;
            playbackOutput = boundEngine.CreateOutput(new(targetBufferMilliseconds, maxBufferMilliseconds, maxConcurrentStreams, .03f, .5f, receiveTimeout));
            if (playbackOutput == null) { return; }
            playbackOutput.ConfigureRoute(Channel, NetworkObjectId, NetworkManager.LocalClientId, suppressOriginClient);
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

        private void RegisterSpeaker() {
            if (!activeSpeakers.Contains(this)) { activeSpeakers.Add(this); }
        }

        private void UnregisterSpeaker() {
            activeSpeakers.Remove(this);
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

        private static bool ContainsClientId(NativeList<ulong> clientIds, ulong clientId) {
            for (int i = 0; i < clientIds.Length; i++) {
                if (clientIds[i] == clientId) { return true; }
            }
            return false;
        }

    }

}
