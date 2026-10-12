#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Pool;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Area Microphone")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(ChannelTuner))]
    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    public sealed class AreaMicrophone : NetworkBehaviour {

        [SerializeField, HideInInspector] private ChannelTuner tuner;
        [SerializeField, HideInInspector] private BroadcastButton broadcastButton;

        [Header("Pickup")]
        [SerializeField, Min(.1f)] private float radius = 4f;
        [SerializeField] private LayerMask capturedLayers = ~0;
        [SerializeField, Range(0f, 2f)] private float pickupGain = 1f;
        [SerializeField] private bool restrictVoiceToButtonOperator;

        [Header("World Audio")]
        [SerializeField] private bool captureWorldAudio = true;
        [SerializeField, Range(.25f, 5f)] private float sourceRefreshInterval = 1f;
        [SerializeField, Range(1, 128)] private int maxCapturedSources = 32;
        [SerializeField] private VoiceCodecSettings codecSettings = default;

        [Header("Relay Validation")]
        [SerializeField, Range(20, 120), Tooltip("Audio the host relays per second, counted in 10 ms frames. 100 is real time at any frame length.")] private int maxFramesPerSecond = 110;

        private readonly struct SourceCandidate {

            internal AudioSource AudioSource { get; }
            internal bool IsPlaying { get; }
            internal float SquaredDistance { get; }
            internal ulong SourceId { get; }

            internal SourceCandidate(AudioSource audioSource, bool isPlaying, float squaredDistance) {
                AudioSource = audioSource;
                IsPlaying = isPlaying;
                SquaredDistance = squaredDistance;
                SourceId = PlaybackSourceRegistry.GetSourceId(audioSource);
            }

        }

        public float Radius => radius;
        public int Channel => tuner == null ? BroadcastRoute.NO_CHANNEL : tuner.CurrentChannel;
        public bool IsOpen => broadcastButton == null || broadcastButton.IsBroadcasting;
        public bool CapturesWorldAudio => captureWorldAudio;
        public ulong CaptureClientId => captureClientId.Value;
        internal int MaximumFramesPerSecond => maxFramesPerSecond;
        internal static IReadOnlyList<AreaMicrophone> ActiveMicrophones => activeMicrophones;

        private static readonly List<AreaMicrophone> activeMicrophones = new(8);
        private static AudioSource[] sharedAudioSources = Array.Empty<AudioSource>();
        private static float nextSharedAudioSourceRefreshAt;
        private static int sharedAudioSourceSnapshotVersion;

        private readonly List<WorldAudioSourceReader> sourceReaders = new(32);
        private readonly List<SourceCandidate> selectedSourceCandidates = new(32);
        private readonly ObjectPool<WorldAudioSourceReader> sourceReaderPool = new(CreateSourceReader, null, DisposeSourceReader, DisposeSourceReader, false, 16, 128);
        private readonly NetworkVariable<ulong> captureClientId = new(BroadcastRoute.UNASSIGNED_CLIENT_ID, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private VoiceSession session;
        private VoiceWorldCaptureProcessor processor;
        private VoiceProcessingWorker processorOwner;
        private float nextCaptureClientRefreshAt;
        private int observedAudioSourceSnapshotVersion = -1;
        private bool sourceReadersNeedSynchronization = true;

        private void Awake() {
            CacheReferences();
            if (!VoiceFrame.IsSupportedSampleCount(codecSettings.SamplesPerFrame)) { codecSettings = VoiceCodecSettings.Default; }
        }

        public override void OnDestroy() {
            UnregisterMicrophone();
            DisposeWorldCapture();
            sourceReaderPool.Clear();
            DetachSession();
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) { RegisterMicrophone(); }
        }

        private void OnDisable() {
            UnregisterMicrophone();
            DisposeWorldCapture();
        }

        private void Update() {
            if (HasAuthority && Time.unscaledTime >= nextCaptureClientRefreshAt) { RefreshCaptureClient(); }
            if (!CanPrepareWorldAudio() || !IsOpen) {
                DisposeWorldCapture();
                return;
            }
            EnsureProcessor();
            RefreshSharedAudioSources();
            RefreshSourceSelection();
            if (sourceReadersNeedSynchronization) { SynchronizeSourceReaders(); }
            RefreshCaptureControl();
        }

        private void Reset() {
            CacheReferences();
            radius = 4f;
            capturedLayers = ~0;
            pickupGain = 1f;
            restrictVoiceToButtonOperator = broadcastButton != null;
            captureWorldAudio = true;
            sourceRefreshInterval = 1f;
            maxCapturedSources = 32;
            codecSettings = VoiceCodecSettings.Default;
            maxFramesPerSecond = 110;
        }

        private void OnValidate() {
            CacheReferences();
            observedAudioSourceSnapshotVersion = -1;
            nextSharedAudioSourceRefreshAt = 0f;
            radius = Mathf.Max(.1f, radius);
            pickupGain = Mathf.Clamp(pickupGain, 0f, 2f);
            sourceRefreshInterval = Mathf.Clamp(sourceRefreshInterval, .25f, 5f);
            maxCapturedSources = Mathf.Clamp(maxCapturedSources, 1, 128);
            codecSettings.Validate();
        }

        public override void OnNetworkSpawn() {
            session = VoiceSession.GetOrCreate(NetworkManager);
            session.OnSendingEnabled.Listen(HandleSendingEnabledChanged);
            RegisterMicrophone();
            RefreshCaptureClient();
        }

        public override void OnNetworkDespawn() {
            UnregisterMicrophone();
            DisposeWorldCapture();
            DetachSession();
        }

        public void SetRadius(float newRadius) {
            radius = Mathf.Max(.1f, newRadius);
            observedAudioSourceSnapshotVersion = -1;
        }

        internal void RefreshCaptureControl() {
            if (processor == null) { return; }
            List<VoiceWorldSource> sources = new(sourceReaders.Count);
            for (int i = 0; i < sourceReaders.Count; i++) {
                WorldAudioSourceReader reader = sourceReaders[i];
                if (!CanCaptureSource(reader.AudioSource, out float attenuation)) { continue; }
                sources.Add(new(reader.History, reader.InitialSamplePosition, attenuation * pickupGain));
            }
            processor.Configure(new(CanPrepareWorldAudio() && IsOpen, CaptureClientId, sources.ToArray()));
        }

        public bool ContainsPosition(Vector3 position) => (position - transform.position).sqrMagnitude <= radius * radius;

        internal bool CanCaptureVoiceFrom(ulong originClientId) {
            if (!IsOpen || Channel < 0) { return false; }
            return !restrictVoiceToButtonOperator || broadcastButton == null || broadcastButton.OperatorClientId == originClientId;
        }

        internal static void CollectVoiceCaptures(NetworkManager networkManager, Vector3 sourcePosition, ulong originClientId, List<AreaMicrophone> results) {
            results.Clear();
            if (networkManager == null || networkManager.SpawnManager == null) { return; }
            for (int i = 0; i < activeMicrophones.Count; i++) {
                AreaMicrophone microphone = activeMicrophones[i];
                if (!IsRegisteredWithNetworkManager(microphone, networkManager) || !microphone.IsSpawned || !microphone.isActiveAndEnabled || !microphone.ContainsPosition(sourcePosition) || !microphone.CanCaptureVoiceFrom(originClientId)) { continue; }
                int matchingIndex = FindChannelIndex(results, microphone.Channel);
                if (matchingIndex < 0) { results.Add(microphone); }
                else if (microphone.NetworkObjectId < results[matchingIndex].NetworkObjectId) { results[matchingIndex] = microphone; }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            activeMicrophones.Clear();
            sharedAudioSources = Array.Empty<AudioSource>();
            nextSharedAudioSourceRefreshAt = 0f;
            sharedAudioSourceSnapshotVersion = 0;
        }

        private void HandleSendingEnabledChanged(bool isEnabled) {
            if (!isEnabled) { DisposeWorldCapture(); }
        }

        private void EnsureProcessor() {
            if (processor != null && (session == null || processorOwner != session.ProcessingWorker)) { DisposeWorldCapture(); }
            if (processor != null || session == null || !session.isActiveAndEnabled || session.NetworkSession == null || session.ProcessingWorker == null) { return; }
            processorOwner = session.ProcessingWorker;
            processor = new(session.NetworkSession, VoiceRouteId.World(NetworkObjectId), codecSettings.ToConfiguration());
            if (!processorOwner.Register(processor)) {
                processor.Dispose();
                processor = null;
            }
        }

        private void DisposeWorldCapture() {
            VoiceWorldCaptureProcessor current = processor;
            processor = null;
            if (current != null) {
                current.Stop();
                if (processorOwner != null) { processorOwner.Unregister(current); }
                processorOwner = null;
            }
            for (int i = 0; i < sourceReaders.Count; i++) { sourceReaderPool.Release(sourceReaders[i]); }
            sourceReaders.Clear();
            selectedSourceCandidates.Clear();
            observedAudioSourceSnapshotVersion = -1;
            sourceReadersNeedSynchronization = true;
        }

        private void DetachSession() {
            if (session != null) { session.OnSendingEnabled.Unlisten(HandleSendingEnabledChanged); }
            session = null;
        }

        private bool CanPrepareWorldAudio() {
            return session != null && session.isActiveAndEnabled && session.IsSendingEnabled && captureWorldAudio && isActiveAndEnabled && IsSpawned && IsClient && Channel >= 0 && NetworkManager != null && NetworkManager.LocalClientId == CaptureClientId;
        }

        private void RefreshSourceSelection() {
            if (observedAudioSourceSnapshotVersion == sharedAudioSourceSnapshotVersion) { return; }

            observedAudioSourceSnapshotVersion = sharedAudioSourceSnapshotVersion;
            selectedSourceCandidates.Clear();
            for (int i = 0; i < sharedAudioSources.Length; i++) {
                AudioSource audioSource = sharedAudioSources[i];
                if (!TryCreateSourceCandidate(audioSource, out SourceCandidate candidate)) { continue; }
                AddPreferredSource(candidate);
            }
            sourceReadersNeedSynchronization = true;
        }

        private void SynchronizeSourceReaders() {
            using (HashSetPool<AudioSource>.Get(out HashSet<AudioSource> selectedAudioSources)) {
                for (int i = 0; i < selectedSourceCandidates.Count; i++) {
                    AudioSource audioSource = selectedSourceCandidates[i].AudioSource;
                    if (audioSource != null) {
                        selectedAudioSources.Add(audioSource);
                    }
                }

                for (int i = sourceReaders.Count - 1; i >= 0; i--) {
                    WorldAudioSourceReader sourceReader = sourceReaders[i];
                    if (selectedAudioSources.Remove(sourceReader.AudioSource)) { continue; }

                    sourceReaderPool.Release(sourceReader);
                    sourceReaders.RemoveAt(i);
                }

                for (int i = 0; i < selectedSourceCandidates.Count; i++) {
                    AudioSource audioSource = selectedSourceCandidates[i].AudioSource;
                    if (audioSource == null || !selectedAudioSources.Remove(audioSource)) { continue; }

                    WorldAudioSourceReader sourceReader = sourceReaderPool.Get();
                    sourceReader.Initialize(audioSource);
                    sourceReaders.Add(sourceReader);
                }
            }
            sourceReadersNeedSynchronization = false;
        }

        private void RegisterMicrophone() {
            if (!activeMicrophones.Contains(this)) {
                activeMicrophones.Add(this);
                nextSharedAudioSourceRefreshAt = 0f;
            }
        }

        private void UnregisterMicrophone() {
            if (!activeMicrophones.Remove(this) || activeMicrophones.Count > 0) { return; }

            sharedAudioSources = Array.Empty<AudioSource>();
            nextSharedAudioSourceRefreshAt = 0f;
            sharedAudioSourceSnapshotVersion = unchecked(sharedAudioSourceSnapshotVersion + 1);
        }

        private void RefreshCaptureClient() {
            nextCaptureClientRefreshAt = Time.unscaledTime + 1f;
            if (!IsSpawned || !HasAuthority || NetworkManager == null || !captureClientId.CanClientWrite(NetworkManager.LocalClientId)) { return; }

            IReadOnlyList<ulong> connectedClientIds = NetworkManager.ConnectedClientsIds;
            ulong selectedClientId = BroadcastRoute.UNASSIGNED_CLIENT_ID;
            for (int i = 0; i < connectedClientIds.Count; i++) {
                ulong clientId = connectedClientIds[i];
                if (clientId == NetworkManager.LocalClientId && !NetworkManager.IsClient) { continue; }
                if (NetworkManager.CMBServiceConnection && clientId == NetworkManager.ServerClientId) { continue; }
                if (!VoiceParticipant.IsClientSendingEnabled(NetworkManager, clientId)) { continue; }
                if (!NetworkObject.IsNetworkVisibleTo(clientId) || clientId >= selectedClientId) { continue; }
                selectedClientId = clientId;
            }

            if (captureClientId.Value != selectedClientId) {
                captureClientId.Value = selectedClientId;
            }
        }

        private bool CanCaptureSource(AudioSource candidate, out float attenuation) {
            attenuation = 0f;
            if (candidate == null || !candidate.isActiveAndEnabled || !candidate.isPlaying) { return false; }
            if ((capturedLayers.value & (1 << candidate.gameObject.layer)) == 0) { return false; }
            if (PlaybackSourceRegistry.Contains(candidate)) { return false; }

            float squaredDistance = (transform.position - candidate.transform.position).sqrMagnitude;
            float squaredRadius = radius * radius;
            if (squaredDistance > squaredRadius) { return false; }

            float distance = Mathf.Sqrt(squaredDistance);
            attenuation = radius <= .1f ? 1f : 1f - distance / radius;
            return attenuation > 0f;
        }

        private bool TryCreateSourceCandidate(AudioSource audioSource, out SourceCandidate candidate) {
            candidate = default;
            if (audioSource == null || !audioSource.isActiveAndEnabled) { return false; }
            if ((capturedLayers.value & (1 << audioSource.gameObject.layer)) == 0) { return false; }
            if (PlaybackSourceRegistry.Contains(audioSource)) { return false; }

            float squaredDistance = (transform.position - audioSource.transform.position).sqrMagnitude;
            if (squaredDistance > radius * radius) { return false; }

            candidate = new(audioSource, audioSource.isPlaying, squaredDistance);
            return true;
        }

        private void AddPreferredSource(SourceCandidate candidate) {
            int sourceLimit = Mathf.Clamp(maxCapturedSources, 1, 128);
            if (selectedSourceCandidates.Count < sourceLimit) {
                selectedSourceCandidates.Add(candidate);
                return;
            }

            int leastPreferredIndex = 0;
            for (int i = 1; i < selectedSourceCandidates.Count; i++) {
                if (CompareSourceCandidates(selectedSourceCandidates[leastPreferredIndex], selectedSourceCandidates[i]) < 0) {
                    leastPreferredIndex = i;
                }
            }
            if (CompareSourceCandidates(candidate, selectedSourceCandidates[leastPreferredIndex]) >= 0) { return; }
            selectedSourceCandidates[leastPreferredIndex] = candidate;
        }

        private void CacheReferences() {
            if (tuner == null) {
                TryGetComponent(out tuner);
            }
            if (broadcastButton == null) {
                TryGetComponent(out broadcastButton);
            }
        }

        private static void DisposeSourceReader(WorldAudioSourceReader sourceReader) {
            sourceReader.Dispose();
        }

        private static WorldAudioSourceReader CreateSourceReader() => new();

        private static void RefreshSharedAudioSources() {
            float currentTime = Time.unscaledTime;
            if (currentTime < nextSharedAudioSourceRefreshAt) { return; }

            #if UNITY_6000_7_OR_NEWER
            sharedAudioSources = Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude);
            #else
            sharedAudioSources = Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            #endif
            sharedAudioSourceSnapshotVersion = unchecked(sharedAudioSourceSnapshotVersion + 1);
            nextSharedAudioSourceRefreshAt = currentTime + GetFastestSourceRefreshInterval();
        }

        private static float GetFastestSourceRefreshInterval() {
            float fastestRefreshInterval = float.PositiveInfinity;
            for (int i = 0; i < activeMicrophones.Count; i++) {
                AreaMicrophone microphone = activeMicrophones[i];
                if (microphone == null ||
                    !microphone.isActiveAndEnabled ||
                    !microphone.IsSpawned ||
                    !microphone.captureWorldAudio) {
                    continue;
                }
                float refreshInterval = Mathf.Clamp(microphone.sourceRefreshInterval, .25f, 5f);
                fastestRefreshInterval = Mathf.Min(fastestRefreshInterval, refreshInterval);
            }
            return float.IsPositiveInfinity(fastestRefreshInterval) ? 1f : fastestRefreshInterval;
        }

        private static int CompareSourceCandidates(SourceCandidate left, SourceCandidate right) {
            if (left.IsPlaying != right.IsPlaying) {
                return left.IsPlaying ? -1 : 1;
            }
            int distanceComparison = left.SquaredDistance.CompareTo(right.SquaredDistance);
            return distanceComparison != 0
                ? distanceComparison
                : left.SourceId.CompareTo(right.SourceId);
        }

        private static int FindChannelIndex(List<AreaMicrophone> microphones, int channel) {
            for (int i = 0; i < microphones.Count; i++) {
                if (microphones[i].Channel == channel) { return i; }
            }
            return -1;
        }

        private static bool IsRegisteredWithNetworkManager(AreaMicrophone microphone, NetworkManager networkManager) {
            return microphone != null &&
                   microphone.NetworkObject != null &&
                   networkManager != null &&
                   networkManager.SpawnManager != null &&
                   networkManager.SpawnManager.SpawnedObjects.TryGetValue(microphone.NetworkObjectId, out NetworkObject networkObject) &&
                   networkObject == microphone.NetworkObject;
        }

    }

}
