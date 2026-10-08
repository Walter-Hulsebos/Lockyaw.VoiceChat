using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Pool;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Channel Speaker")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(ChannelTuner))]
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
        [SerializeField, Range(20, 250)] private int targetBufferMilliseconds = 60;
        [SerializeField, Range(60, 1000)] private int maxBufferMilliseconds = 350;
        [SerializeField, Range(0, 100)] private int maximumReorderDelayMilliseconds = 40;
        [SerializeField, Range(0, 8)] private int maxConcealedFrames = 3;

        public readonly Signal<float> OnFrameLevel = new();

        public AudioSource AudioSource => audioSource;
        public SpatializationMode Spatialization => spatialization;
        public int ActiveStreams => GetActiveStreamCount();
        public int Channel => tuner == null ? BroadcastRoute.NO_CHANNEL : tuner.CurrentChannel;

        private static readonly List<ChannelSpeaker> activeSpeakers = new(8);

        private const string BROADCAST_PLAYBACK_OBJECT_NAME = "Broadcast Playback";

        private readonly Dictionary<ulong, BroadcastPlaybackStream> streams = new(8);
        private BroadcastPlaybackStream[] playbackStreams = Array.Empty<BroadcastPlaybackStream>();
        private CircularAudioClipRenderer playbackRenderer;
        private ulong registeredPlaybackSourceId;
        private int playbackStartBufferSamples;
        private double nextDecoderWarningAt;
        private double nextFrameLevelWarningAt;

        private void Awake() {
            registeredPlaybackSourceId = PlaybackSourceRegistry.NO_SOURCE_ID;
            if (tuner == null) {
                TryGetComponent(out tuner);
            }
            if (tuner != null) {
                tuner.OnChannelChanged.Listen(HandleChannelChanged);
            }
            ConfigureAudioSource();
            RegisterPlaybackSource();
        }

        public override void OnDestroy() {
            if (tuner != null) {
                tuner.OnChannelChanged.Unlisten(HandleChannelChanged);
            }
            UnregisterSpeaker();
            StopPlayback();
            base.OnDestroy();
        }

        private void OnEnable() {
            VoiceChatSettings.OnReceivingEnabled.Listen(HandleReceivingEnabledChanged);
            RegisterPlaybackSource();
            if (IsSpawned) {
                RegisterSpeaker();
            }
            if (IsSpawned && NetworkManager != null && NetworkManager.IsClient) {
                StartPlayback();
            }
        }

        private void OnDisable() {
            VoiceChatSettings.OnReceivingEnabled.Unlisten(HandleReceivingEnabledChanged);
            UnregisterSpeaker();
            StopPlayback();
        }

        private void Update() {
            CircularAudioClipRenderer renderer = playbackRenderer;
            if (renderer == null) { return; }

            BroadcastPlaybackStream[] streamsSnapshot = Volatile.Read(ref playbackStreams);
            double currentTime = Time.unscaledTimeAsDouble;
            for (int i = 0; i < streamsSnapshot.Length; i++) {
                BroadcastPlaybackStream stream = streamsSnapshot[i];
                try {
                    stream.Process(currentTime, renderer.RenderQuantumSamples, false);
                }
                catch (Exception exception) {
                    LogDecoderWarning(exception);
                }
                DispatchPendingFrameLevel(stream);
                if (Volatile.Read(ref playbackStreams) != streamsSnapshot) { return; }
                if (stream.HasTimedOut(currentTime, receiveTimeout)) {
                    stream.MarkTimedOut();
                }
            }
            renderer.Update(CanStartPlayback(streamsSnapshot));
        }

        private void Reset() {
            TryGetComponent(out tuner);
            spatialization = SpatializationMode.UnityThreeDimensional;
            minDistance = 1.5f;
            maxDistance = 25f;
            suppressOriginClient = true;
            maxConcurrentStreams = 8;
            receiveTimeout = .5f;
            targetBufferMilliseconds = 60;
            maxBufferMilliseconds = 350;
            maximumReorderDelayMilliseconds = 40;
            maxConcealedFrames = 3;
            EnsureAudioSource();
            ConfigureAudioSource();
        }

        private void OnValidate() {
            if (tuner == null) {
                TryGetComponent(out tuner);
            }
            minDistance = Mathf.Max(.01f, minDistance);
            maxDistance = Mathf.Max(maxDistance, minDistance + .1f);
            maxConcurrentStreams = Mathf.Clamp(maxConcurrentStreams, 1, 16);
            receiveTimeout = Mathf.Clamp(receiveTimeout, .1f, 2f);
            targetBufferMilliseconds = Mathf.Clamp(targetBufferMilliseconds, 20, 250);
            maxBufferMilliseconds = Mathf.Max(maxBufferMilliseconds, targetBufferMilliseconds);
            maximumReorderDelayMilliseconds = Mathf.Clamp(maximumReorderDelayMilliseconds, 0, targetBufferMilliseconds);
            ConfigureAudioSource();
            RegisterPlaybackSource();
        }

        public override void OnNetworkSpawn() {
            RegisterSpeaker();
            RegisterPlaybackSource();
            if (NetworkManager.IsClient && isActiveAndEnabled) {
                StartPlayback();
            }
        }

        public override void OnNetworkDespawn() {
            UnregisterSpeaker();
            StopPlayback();
        }

        public void SetAudioSource(AudioSource newAudioSource) {
            if (audioSource == newAudioSource) { return; }

            bool shouldRestart = isActiveAndEnabled && IsSpawned && NetworkManager != null && NetworkManager.IsClient;
            StopPlayback();
            audioSource = newAudioSource;
            ConfigureAudioSource();
            if (shouldRestart) {
                StartPlayback();
            }
            else {
                RegisterPlaybackSource();
            }
        }

        public void SetSpatialization(SpatializationMode mode) {
            spatialization = mode;
            ConfigureAudioSource();
        }

        public void ClearPlayback() {
            foreach (BroadcastPlaybackStream stream in streams.Values) {
                stream.Dispose();
            }
            streams.Clear();
            RefreshPlaybackStreams();
            if (playbackRenderer != null) {
                playbackRenderer.Reset();
            }
        }

        internal static void Dispatch(NetworkManager networkManager, BroadcastFrame frame) {
            if (!VoiceChatSettings.IsReceivingEnabled || networkManager == null || networkManager.SpawnManager == null || !frame.IsValid) { return; }

            using (ListPool<ChannelSpeaker>.Get(out List<ChannelSpeaker> speakersSnapshot)) {
                speakersSnapshot.AddRange(activeSpeakers);
                for (int i = 0; i < speakersSnapshot.Count; i++) {
                    ChannelSpeaker speaker = speakersSnapshot[i];
                    if (!IsRegisteredWithNetworkManager(speaker, networkManager)) { continue; }
                    speaker.ReceiveFrame(frame);
                }
            }
        }

        internal static void CollectListenerClientIds(NetworkManager networkManager, int channel, NativeList<ulong> results) {
            results.Clear();
            if (networkManager == null || networkManager.SpawnManager == null || channel < 0) { return; }

            IReadOnlyList<ulong> connectedClientIds = networkManager.ConnectedClientsIds;
            for (int speakerIndex = 0; speakerIndex < activeSpeakers.Count; speakerIndex++) {
                ChannelSpeaker speaker = activeSpeakers[speakerIndex];
                if (!IsRegisteredWithNetworkManager(speaker, networkManager) ||
                    !speaker.IsSpawned ||
                    !speaker.isActiveAndEnabled ||
                    speaker.Channel != channel) {
                    continue;
                }

                for (int clientIndex = 0; clientIndex < connectedClientIds.Count; clientIndex++) {
                    ulong clientId = connectedClientIds[clientIndex];
                    if (!speaker.NetworkObject.IsNetworkVisibleTo(clientId) || ContainsClientId(results, clientId)) { continue; }
                    results.Add(clientId);
                }
            }
        }

        internal static bool IsPlaybackSource(AudioSource candidate) {
            return PlaybackSourceRegistry.Contains(candidate);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            activeSpeakers.Clear();
        }

        private void ReceiveFrame(BroadcastFrame frame) {
            if (!VoiceChatSettings.IsReceivingEnabled || playbackRenderer == null || tuner == null || !frame.CanPlayOn(tuner.CurrentChannel, NetworkObjectId)) { return; }
            if (suppressOriginClient && frame.Route.HasOriginClient && frame.Route.OriginClientId == NetworkManager.LocalClientId) { return; }

            ulong originNetworkObjectId = frame.Route.OriginNetworkObjectId;
            if (!streams.TryGetValue(originNetworkObjectId, out BroadcastPlaybackStream stream)) {
                if (streams.Count >= maxConcurrentStreams && !TryRemoveOldestInactiveStream()) { return; }

                int maxBufferSamples = VoiceEncoderSession.SAMPLE_RATE * maxBufferMilliseconds / 1000;
                stream = new(maxBufferSamples, playbackStartBufferSamples, maxConcealedFrames, maximumReorderDelayMilliseconds);
                streams.Add(originNetworkObjectId, stream);
                RefreshPlaybackStreams();
            }

            if (stream.OriginClientId != frame.Route.OriginClientId) {
                stream.OriginClientId = frame.Route.OriginClientId;
                stream.PlaybackVolume = 1f;
            }
            RefreshParticipantVolume(stream);
            try {
                stream.Receive(frame.Frame, Time.unscaledTimeAsDouble, playbackRenderer.RenderQuantumSamples);
            }
            catch (Exception exception) {
                LogDecoderWarning(exception);
            }
            DispatchPendingFrameLevel(stream);
        }

        private int RenderSamples(Span<float> outputSamples) {
            outputSamples.Clear();
            BroadcastPlaybackStream[] streamsSnapshot = Volatile.Read(ref playbackStreams);
            int renderedSampleCount = 0;
            for (int i = 0; i < streamsSnapshot.Length; i++) {
                BroadcastPlaybackStream stream = streamsSnapshot[i];
                RefreshParticipantVolume(stream);
                renderedSampleCount = Math.Max(renderedSampleCount, stream.MixInto(outputSamples));
            }

            for (int i = 0; i < outputSamples.Length; i++) {
                outputSamples[i] = Math.Clamp(outputSamples[i], -1f, 1f);
            }
            return renderedSampleCount;
        }

        private void HandleChannelChanged(int channel) {
            ClearPlayback();
        }

        private void HandleReceivingEnabledChanged(bool isEnabled) {
            if (!isEnabled) {
                StopPlayback();
                return;
            }
            if (isActiveAndEnabled && IsSpawned && NetworkManager != null && NetworkManager.IsClient) {
                StartPlayback();
            }
        }

        private void StartPlayback() {
            StopPlayback();
            if (!VoiceChatSettings.IsReceivingEnabled) { return; }
            EnsureAudioSource();
            if (audioSource == null) {
                Debug.LogError("Channel Speaker needs an AudioSource.", this);
                return;
            }

            ConfigureAudioSource();
            int maxBufferSamples = VoiceEncoderSession.SAMPLE_RATE * maxBufferMilliseconds / 1000;
            int configuredTargetSamples = VoiceEncoderSession.SAMPLE_RATE * targetBufferMilliseconds / 1000;
            int renderLeadSamples = GetRenderLeadSamples(maxBufferSamples, configuredTargetSamples);
            playbackStartBufferSamples = Math.Min(maxBufferSamples, configuredTargetSamples + renderLeadSamples);
            playbackRenderer = new CircularAudioClipRenderer(
                audioSource,
                $"Broadcast {NetworkObjectId}",
                renderLeadSamples,
                maxBufferSamples,
                RenderSamples);
            RegisterPlaybackSource();
        }

        private void StopPlayback() {
            UnregisterPlaybackSource();
            if (audioSource != null) {
                audioSource.Stop();
            }
            if (playbackRenderer != null) {
                playbackRenderer.Dispose();
                playbackRenderer = null;
            }

            foreach (BroadcastPlaybackStream stream in streams.Values) {
                stream.Dispose();
            }
            streams.Clear();
            Volatile.Write(ref playbackStreams, Array.Empty<BroadcastPlaybackStream>());
            playbackStartBufferSamples = 0;
        }

        private void RemoveStream(ulong originNetworkObjectId) {
            if (!streams.Remove(originNetworkObjectId, out BroadcastPlaybackStream stream)) { return; }
            stream.Dispose();
        }

        private bool TryRemoveOldestInactiveStream() {
            ulong oldestOriginNetworkObjectId = BroadcastRoute.UNASSIGNED_NETWORK_OBJECT_ID;
            BroadcastPlaybackStream oldestStream = null;
            foreach (KeyValuePair<ulong, BroadcastPlaybackStream> pair in streams) {
                if (pair.Value.IsActive ||
                    oldestStream != null && pair.Value.LastFrameReceivedAt >= oldestStream.LastFrameReceivedAt) {
                    continue;
                }
                oldestOriginNetworkObjectId = pair.Key;
                oldestStream = pair.Value;
            }
            if (oldestStream == null) { return false; }

            RemoveStream(oldestOriginNetworkObjectId);
            return true;
        }

        private void RefreshPlaybackStreams() {
            BroadcastPlaybackStream[] refreshedStreams = new BroadcastPlaybackStream[streams.Count];
            streams.Values.CopyTo(refreshedStreams, 0);
            Volatile.Write(ref playbackStreams, refreshedStreams);
        }

        private void LogDecoderWarning(Exception exception) {
            if (Time.unscaledTimeAsDouble < nextDecoderWarningAt) { return; }
            nextDecoderWarningAt = Time.unscaledTimeAsDouble + 1d;
            Debug.LogWarning($"A broadcast voice stream was reset: {exception.Message}", this);
        }

        private void DispatchFrameLevel(byte level) {
            try {
                OnFrameLevel.Dispatch(level / (float)byte.MaxValue);
            }
            catch (Exception exception) {
                if (Time.unscaledTimeAsDouble < nextFrameLevelWarningAt) { return; }
                nextFrameLevelWarningAt = Time.unscaledTimeAsDouble + 1d;
                Debug.LogWarning($"A broadcast voice level listener failed: {exception.Message}", this);
            }
        }

        private void DispatchPendingFrameLevel(BroadcastPlaybackStream stream) {
            if (!stream.TryConsumeFrameLevel(out byte frameLevel)) { return; }
            DispatchFrameLevel(frameLevel);
        }

        private void RegisterPlaybackSource() {
            if (!Application.isPlaying || !isActiveAndEnabled || audioSource == null) {
                UnregisterPlaybackSource();
                return;
            }

            ulong audioSourceId = PlaybackSourceRegistry.GetSourceId(audioSource);
            if (registeredPlaybackSourceId == audioSourceId) { return; }

            UnregisterPlaybackSource();
            registeredPlaybackSourceId = PlaybackSourceRegistry.Register(audioSource);
        }

        private void UnregisterPlaybackSource() {
            if (registeredPlaybackSourceId == PlaybackSourceRegistry.NO_SOURCE_ID) { return; }

            PlaybackSourceRegistry.Unregister(registeredPlaybackSourceId);
            registeredPlaybackSourceId = PlaybackSourceRegistry.NO_SOURCE_ID;
        }

        private void RegisterSpeaker() {
            if (!activeSpeakers.Contains(this)) {
                activeSpeakers.Add(this);
            }
        }

        private void UnregisterSpeaker() {
            activeSpeakers.Remove(this);
        }

        private void EnsureAudioSource() {
            if (audioSource != null) { return; }

            Transform playbackTransform = transform.Find(BROADCAST_PLAYBACK_OBJECT_NAME);
            if (playbackTransform == null) {
                GameObject playbackObject = new(BROADCAST_PLAYBACK_OBJECT_NAME);
                playbackTransform = playbackObject.transform;
                playbackTransform.SetParent(transform, false);
            }

            if (!playbackTransform.TryGetComponent(out audioSource)) {
                audioSource = playbackTransform.gameObject.AddComponent<AudioSource>();
            }
        }

        private void ConfigureAudioSource() {
            if (audioSource == null) { return; }

            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.dopplerLevel = 0f;
            audioSource.minDistance = minDistance;
            audioSource.maxDistance = maxDistance;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.outputAudioMixerGroup = outputMixerGroup;
            audioSource.spatializePostEffects = true;

            switch (spatialization) {
                case SpatializationMode.TwoDimensional:
                    audioSource.spatialBlend = 0f;
                    audioSource.spatialize = false;
                    break;
                case SpatializationMode.UnityThreeDimensional:
                    audioSource.spatialBlend = 1f;
                    audioSource.spatialize = false;
                    break;
                case SpatializationMode.ProjectSpatializer:
                    audioSource.spatialBlend = 1f;
                    audioSource.spatialize = true;
                    break;
            }
        }

        private void RefreshParticipantVolume(BroadcastPlaybackStream stream) {
            if (stream.OriginClientId == BroadcastRoute.UNASSIGNED_CLIENT_ID || !VoiceParticipant.TryGet(NetworkManager, stream.OriginClientId, out VoiceParticipant participant)) { return; }
            stream.PlaybackVolume = participant.IsPlaybackMuted ? 0f : participant.PlaybackVolume;
        }

        private static bool IsRegisteredWithNetworkManager(ChannelSpeaker speaker, NetworkManager networkManager) {
            return speaker != null &&
                   speaker.NetworkObject != null &&
                   networkManager != null &&
                   networkManager.SpawnManager != null &&
                   networkManager.SpawnManager.SpawnedObjects.TryGetValue(speaker.NetworkObjectId, out NetworkObject networkObject) &&
                   networkObject == speaker.NetworkObject;
        }

        private static bool ContainsClientId(NativeList<ulong> clientIds, ulong clientId) {
            for (int i = 0; i < clientIds.Length; i++) {
                if (clientIds[i] == clientId) { return true; }
            }
            return false;
        }

        private static int GetRenderLeadSamples(int maxBufferSamples, int configuredTargetSamples) {
            AudioSettings.GetDSPBufferSize(out int dspBufferSamples, out int dspBufferCount);
            int desiredRenderLeadSamples = Math.Max(
                (int)VoiceFrameLength.TwentyMilliseconds * 2,
                dspBufferSamples * dspBufferCount);
            return Math.Clamp(desiredRenderLeadSamples, 1, Math.Max(1, maxBufferSamples - configuredTargetSamples));
        }

        private int GetActiveStreamCount() {
            int activeStreamCount = 0;
            foreach (BroadcastPlaybackStream stream in streams.Values) {
                if (stream.IsActive) {
                    activeStreamCount++;
                }
            }
            return activeStreamCount;
        }

        private static bool CanStartPlayback(BroadcastPlaybackStream[] streamsSnapshot) {
            for (int i = 0; i < streamsSnapshot.Length; i++) {
                if (streamsSnapshot[i].CanStart) { return true; }
            }
            return false;
        }

    }

}
