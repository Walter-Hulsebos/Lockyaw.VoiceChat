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
        [SerializeField, Range(20, 120)] private int maxFramesPerSecond = 110;

        private readonly struct SourceCandidate {

            internal AudioSource AudioSource { get; }
            internal bool IsPlaying { get; }
            internal float SquaredDistance { get; }
            internal int InstanceId { get; }

            internal SourceCandidate(AudioSource audioSource, bool isPlaying, float squaredDistance) {
                AudioSource = audioSource;
                IsPlaying = isPlaying;
                SquaredDistance = squaredDistance;
                InstanceId = audioSource.GetInstanceID();
            }

        }

        public float Radius => radius;
        public int Channel => tuner == null ? BroadcastRoute.NO_CHANNEL : tuner.CurrentChannel;
        public bool IsOpen => broadcastButton == null || broadcastButton.IsBroadcasting;
        public bool CapturesWorldAudio => captureWorldAudio;
        public ulong CaptureClientId => captureClientId.Value;

        private static readonly List<AreaMicrophone> activeMicrophones = new(8);
        private static AudioSource[] sharedAudioSources = Array.Empty<AudioSource>();
        private static float nextSharedAudioSourceRefreshAt;
        private static int sharedAudioSourceSnapshotVersion;

        private const double AUTHORITY_STREAM_TIMEOUT = 2d;
        private const int MAXIMUM_WORLD_FRAMES_PER_UPDATE = 2;
        private const uint MAXIMUM_SEQUENCE_ADVANCE = 1024;

        private readonly List<WorldAudioSourceReader> sourceReaders = new(32);
        private readonly List<SourceCandidate> selectedSourceCandidates = new(32);
        private readonly ObjectPool<WorldAudioSourceReader> sourceReaderPool = new(
            CreateSourceReader,
            null,
            DisposeSourceReader,
            DisposeSourceReader,
            false,
            16,
            128);
        private readonly NetworkVariable<ulong> captureClientId = new(
            BroadcastRoute.UNASSIGNED_CLIENT_ID,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        private float[] mixedSamples = Array.Empty<float>();
        private float[] sourceSamples = Array.Empty<float>();
        private VoiceEncoderSession encoder;
        private FrameReplayWindow authorityReplayWindow;
        private uint sequence;
        private double authorityWindowStartedAt;
        private double authorityLastFrameAt;
        private ulong authorityCaptureClientId = BroadcastRoute.UNASSIGNED_CLIENT_ID;
        private int authorityFramesInWindow;
        private float nextCaptureAt;
        private float nextCaptureClientRefreshAt;
        private float nextRouterWarningAt;
        private int observedAudioSourceSnapshotVersion = -1;
        private bool sourceReadersNeedSynchronization = true;

        private void Awake() {
            CacheReferences();
            if (!VoiceFrame.IsSupportedSampleCount(codecSettings.SamplesPerFrame)) {
                codecSettings = VoiceCodecSettings.Default;
            }
        }

        public override void OnDestroy() {
            UnregisterMicrophone();
            DisposeWorldCapture();
            sourceReaderPool.Clear();
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) {
                RegisterMicrophone();
            }
        }

        private void OnDisable() {
            UnregisterMicrophone();
            DisposeWorldCapture();
        }

        private void Update() {
            if (HasAuthority && Time.unscaledTime >= nextCaptureClientRefreshAt) {
                RefreshCaptureClient();
            }

            if (!CanPrepareWorldAudio()) {
                SuspendWorldCapture();
                DisposeEncodingResources();
                return;
            }

            EnsureWorldCapture();
            RefreshSharedAudioSources();
            RefreshSourceSelection();
            if (!IsOpen) {
                SuspendWorldCapture();
                return;
            }
            if (sourceReadersNeedSynchronization) {
                SynchronizeSourceReaders();
            }

            float frameDuration = codecSettings.SamplesPerFrame / (float)VoiceEncoderSession.SAMPLE_RATE;
            int processedFrames = 0;
            while (Time.unscaledTime >= nextCaptureAt && processedFrames < MAXIMUM_WORLD_FRAMES_PER_UPDATE) {
                CaptureWorldFrame();
                nextCaptureAt += frameDuration;
                processedFrames++;
            }
            if (Time.unscaledTime - nextCaptureAt > frameDuration * MAXIMUM_WORLD_FRAMES_PER_UPDATE) {
                nextCaptureAt = Time.unscaledTime + frameDuration;
            }
        }

        private void Reset() {
            TryGetComponent(out tuner);
            TryGetComponent(out broadcastButton);
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
            ResetAuthorityStream();
            RegisterMicrophone();
            RefreshCaptureClient();
        }

        public override void OnNetworkDespawn() {
            UnregisterMicrophone();
            DisposeWorldCapture();
            ResetAuthorityStream();
        }

        public void SetRadius(float newRadius) {
            radius = Mathf.Max(.1f, newRadius);
            observedAudioSourceSnapshotVersion = -1;
        }

        public bool ContainsPosition(Vector3 position) {
            return (position - transform.position).sqrMagnitude <= radius * radius;
        }

        internal static void CollectVoiceCaptures(
            NetworkManager networkManager,
            Vector3 sourcePosition,
            ulong originClientId,
            List<AreaMicrophone> results) {
            results.Clear();
            if (networkManager == null || networkManager.SpawnManager == null) { return; }

            for (int i = 0; i < activeMicrophones.Count; i++) {
                AreaMicrophone microphone = activeMicrophones[i];
                if (!IsRegisteredWithNetworkManager(microphone, networkManager) ||
                    !microphone.IsSpawned ||
                    !microphone.isActiveAndEnabled ||
                    !microphone.ContainsPosition(sourcePosition) ||
                    !microphone.CanCaptureVoiceFrom(originClientId)) {
                    continue;
                }

                int matchingIndex = FindChannelIndex(results, microphone.Channel);
                if (matchingIndex < 0) {
                    results.Add(microphone);
                }
                else if (microphone.NetworkObjectId < results[matchingIndex].NetworkObjectId) {
                    results[matchingIndex] = microphone;
                }
            }
        }

        internal void SubmitVoiceFrame(ulong originNetworkObjectId, ulong originClientId, VoiceFrame frame) {
            if (!IsSpawned || !frame.IsValid) { return; }

            BroadcastRoute route = new(Channel, NetworkObjectId, originNetworkObjectId, 0, originClientId);
            BroadcastFrame broadcastFrame = new(route, frame);
            if (!broadcastFrame.IsValid) { return; }

            if (HasAuthority) {
                ulong senderClientId = NetworkManager.DistributedAuthorityMode
                    ? originClientId
                    : NetworkManager.ServerClientId;
                if (CanRelayVoiceFrame(broadcastFrame, senderClientId)) {
                    RelayFrame(broadcastFrame);
                }
                return;
            }
            SubmitVoiceFrameRpc(broadcastFrame);
        }

        internal bool CanCaptureVoiceFrom(ulong originClientId) {
            if (!IsOpen || Channel < 0) { return false; }
            return !restrictVoiceToButtonOperator ||
                   broadcastButton == null ||
                   broadcastButton.OperatorClientId == originClientId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            activeMicrophones.Clear();
            sharedAudioSources = Array.Empty<AudioSource>();
            nextSharedAudioSourceRefreshAt = 0f;
            sharedAudioSourceSnapshotVersion = 0;
        }

        [Rpc(SendTo.Authority, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
        private void SubmitWorldFrameRpc(VoiceFrame frame, RpcParams rpcParams = default) {
            SubmitWorldFrameAtAuthority(frame, rpcParams.Receive.SenderClientId);
        }

        private void SubmitWorldFrameAtAuthority(VoiceFrame frame, ulong senderClientId) {
            if (!HasAuthority ||
                !frame.IsValid ||
                !IsOpen ||
                senderClientId != CaptureClientId ||
                frame.SourceClientId != senderClientId ||
                !AcceptFrameAtAuthority(frame, senderClientId)) {
                return;
            }

            BroadcastRoute route = new(Channel, NetworkObjectId, NetworkObjectId);
            BroadcastFrame broadcastFrame = new(route, frame);
            if (!broadcastFrame.IsValid) { return; }
            RelayFrame(broadcastFrame);
        }

        [Rpc(SendTo.Authority, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
        private void SubmitVoiceFrameRpc(BroadcastFrame frame, RpcParams rpcParams = default) {
            if (!HasAuthority || !CanRelayVoiceFrame(frame, rpcParams.Receive.SenderClientId)) { return; }
            RelayFrame(frame);
        }

        private void CaptureWorldFrame() {
            if (encoder == null || mixedSamples.Length == 0) { return; }

            Array.Clear(mixedSamples, 0, mixedSamples.Length);
            int capturedSourceCount = 0;
            Span<float> sourceFrameSamples = sourceSamples.AsSpan(0, mixedSamples.Length);
            for (int i = 0; i < sourceReaders.Count && capturedSourceCount < maxCapturedSources; i++) {
                WorldAudioSourceReader sourceReader = sourceReaders[i];
                AudioSource candidate = sourceReader.AudioSource;
                if (!CanCaptureSource(candidate, out float attenuation)) { continue; }
                if (!sourceReader.Read(sourceFrameSamples, MAXIMUM_WORLD_FRAMES_PER_UPDATE)) { continue; }

                float sourceGain = attenuation * pickupGain;
                for (int sampleIndex = 0; sampleIndex < mixedSamples.Length; sampleIndex++) {
                    mixedSamples[sampleIndex] += sourceFrameSamples[sampleIndex] * sourceGain;
                }
                capturedSourceCount++;
            }

            if (capturedSourceCount == 0) { return; }
            for (int i = 0; i < mixedSamples.Length; i++) {
                mixedSamples[i] = Math.Clamp(mixedSamples[i], -1f, 1f);
            }

            float decibels = AudioMath.GetDecibels(mixedSamples);
            VoiceFrame frame;
            try {
                if (!encoder.TryEncode(mixedSamples, sequence, AudioMath.GetNetworkLevel(decibels), out frame)) { return; }
            }
            catch (Exception exception) {
                Debug.LogWarning($"Area Microphone restarted world-audio capture: {exception.Message}", this);
                SuspendWorldCapture();
                DisposeEncodingResources();
                return;
            }

            frame.SourceClientId = NetworkManager.LocalClientId;
            sequence = unchecked(sequence + 1);
            if (HasAuthority) {
                SubmitWorldFrameAtAuthority(frame, NetworkManager.LocalClientId);
            }
            else {
                SubmitWorldFrameRpc(frame);
            }
        }

        private void EnsureWorldCapture() {
            if (encoder != null && mixedSamples.Length == codecSettings.SamplesPerFrame) { return; }

            DisposeEncodingResources();
            encoder = new(codecSettings);
            mixedSamples = new float[codecSettings.SamplesPerFrame];
            sourceSamples = new float[GetOutputSampleCount(codecSettings.SamplesPerFrame)];
            nextCaptureAt = Time.unscaledTime;
        }

        private void SuspendWorldCapture() {
            ReleaseSourceReaders();
            nextCaptureAt = Time.unscaledTime;
        }

        private void DisposeWorldCapture() {
            ReleaseSourceReaders();
            selectedSourceCandidates.Clear();
            observedAudioSourceSnapshotVersion = -1;
            sourceReadersNeedSynchronization = true;
            DisposeEncodingResources();
        }

        private void ReleaseSourceReaders() {
            if (sourceReaders.Count == 0) { return; }

            for (int i = 0; i < sourceReaders.Count; i++) {
                sourceReaderPool.Release(sourceReaders[i]);
            }
            sourceReaders.Clear();
            sourceReadersNeedSynchronization = true;
        }

        private void DisposeEncodingResources() {
            if (encoder != null) {
                encoder.Dispose();
                encoder = null;
            }
            mixedSamples = Array.Empty<float>();
            sourceSamples = Array.Empty<float>();
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

        private void ResetAuthorityStream() {
            authorityReplayWindow.Clear();
            authorityWindowStartedAt = 0d;
            authorityLastFrameAt = 0d;
            authorityCaptureClientId = BroadcastRoute.UNASSIGNED_CLIENT_ID;
            authorityFramesInWindow = 0;
        }

        private bool AcceptFrameAtAuthority(VoiceFrame frame, ulong captureClientId) {
            if (authorityCaptureClientId != captureClientId) {
                ResetAuthorityStream();
                authorityCaptureClientId = captureClientId;
            }

            double currentTime = Time.unscaledTimeAsDouble;
            bool allowForwardRebase = currentTime - authorityLastFrameAt >= AUTHORITY_STREAM_TIMEOUT;
            if (currentTime - authorityWindowStartedAt >= 1d) {
                authorityWindowStartedAt = currentTime;
                authorityFramesInWindow = 0;
            }

            authorityFramesInWindow++;
            if (authorityFramesInWindow > maxFramesPerSecond) { return false; }
            if (!authorityReplayWindow.TryAccept(
                frame.SourceClientId,
                frame.StreamId,
                frame.Sequence,
                MAXIMUM_SEQUENCE_ADVANCE,
                allowForwardRebase)) {
                return false;
            }

            authorityLastFrameAt = currentTime;
            return true;
        }

        private void RelayFrame(BroadcastFrame frame) {
            if (!frame.IsValid) { return; }
            if (BroadcastRouter.TryGet(NetworkManager, out BroadcastRouter router)) {
                router.SubmitFrame(frame);
                return;
            }
            if (Time.unscaledTime >= nextRouterWarningAt) {
                nextRouterWarningAt = Time.unscaledTime + 5f;
                Debug.LogError("Area Microphone needs one spawned Broadcast Router in the session.", this);
            }
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
                if (!NetworkObject.IsNetworkVisibleTo(clientId) || clientId >= selectedClientId) { continue; }
                selectedClientId = clientId;
            }

            if (captureClientId.Value != selectedClientId) {
                captureClientId.Value = selectedClientId;
            }
        }

        private bool CanRelayVoiceFrame(BroadcastFrame frame, ulong senderClientId) {
            if (!frame.IsValid ||
                !frame.Route.IsDirect ||
                frame.Route.SourceNetworkObjectId != NetworkObjectId ||
                !frame.Route.MatchesChannel(Channel) ||
                frame.Frame.SourceClientId != frame.Route.OriginClientId ||
                NetworkManager == null ||
                NetworkManager.SpawnManager == null) {
                return false;
            }

            if (NetworkManager.DistributedAuthorityMode) {
                if (senderClientId != frame.Route.OriginClientId) { return false; }
            }
            else if (senderClientId != NetworkManager.ServerClientId) {
                return false;
            }

            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(frame.Route.OriginNetworkObjectId, out NetworkObject originObject)) {
                return false;
            }

            if (!originObject.TryGetComponent(out VoiceParticipant participant)) { return false; }
            return participant.OwnerClientId == frame.Route.OriginClientId &&
                   ContainsPosition(participant.transform.position) &&
                   CanCaptureVoiceFrom(frame.Route.OriginClientId);
        }

        private bool CanPrepareWorldAudio() {
            return captureWorldAudio &&
                   isActiveAndEnabled &&
                   IsSpawned &&
                   IsClient &&
                   Channel >= 0 &&
                   NetworkManager != null &&
                   NetworkManager.LocalClientId == CaptureClientId;
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

            sharedAudioSources = Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
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

        private static int GetOutputSampleCount(int minimumSampleCount) {
            int sampleCount = 1;
            while (sampleCount < minimumSampleCount) {
                sampleCount <<= 1;
            }
            return sampleCount;
        }

        private static int CompareSourceCandidates(SourceCandidate left, SourceCandidate right) {
            if (left.IsPlaying != right.IsPlaying) {
                return left.IsPlaying ? -1 : 1;
            }
            int distanceComparison = left.SquaredDistance.CompareTo(right.SquaredDistance);
            return distanceComparison != 0
                ? distanceComparison
                : left.InstanceId.CompareTo(right.InstanceId);
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
