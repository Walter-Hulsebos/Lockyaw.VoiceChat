using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Pool;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Output/Speaker")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VoiceParticipant))]
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
        [SerializeField, Range(20, 250)] private int targetBufferMilliseconds = 60;
        [SerializeField, Range(60, 1000)] private int maxBufferMilliseconds = 350;
        [SerializeField, Range(0, 100)] private int maximumReorderDelayMilliseconds = 40;
        [SerializeField, Range(0, 8)] private int maxConcealedFrames = 3;
        [SerializeField, Range(0f, .1f)] private float maxPitchCorrection = .03f;
        [SerializeField, Range(0f, 2f)] private float pitchCorrectionGain = .5f;

        public readonly Signal<float> OnFrameLevel = new();

        public AudioSource AudioSource => audioSource;
        public SpatializationMode Spatialization => spatialization;
        public float LocalVolume => localVolume;
        public int BufferedSamples => sampleBuffer == null
            ? 0
            : sampleBuffer.GetSampleCount() + (playbackRenderer == null ? 0 : playbackRenderer.BufferedContentSamples);

        private static readonly ObjectPool<VoiceDecoderSession> decoderPool = new(
            CreateDecoder,
            ResetDecoder,
            ResetDecoder,
            DisposeDecoder,
            false,
            DEFAULT_DECODER_POOL_CAPACITY,
            MAXIMUM_DECODER_POOL_SIZE);

        private const int DEFAULT_DECODER_POOL_CAPACITY = 8;
        private const int MAXIMUM_DECODER_POOL_SIZE = 32;
        private const int MAXIMUM_PENDING_ENCODED_FRAMES = 16;
        private const uint MAXIMUM_SEQUENCE_ADVANCE = 1024;
        private const string VOICE_PLAYBACK_OBJECT_NAME = "Voice Playback";
        private const float PITCH_CORRECTION_ACTIVITY_TIMEOUT = .25f;

        private VoiceDecoderSession decoder;
        private EncodedFrameReorderBuffer reorderBuffer;
        private SampleRingBuffer sampleBuffer;
        private CircularAudioClipRenderer playbackRenderer;
        private ulong registeredPlaybackSourceId;
        private double lastFrameReceivedAt;
        private double nextDecoderWarningAt;
        private double nextFrameLevelWarningAt;
        private long receivedFrames;
        private long missingFrames;
        private long intentionalSilenceSamples;
        private long lateFrames;
        private long reorderedFrames;
        private long duplicateFrames;
        private long finalizedGaps;
        private long queuePressureEvents;
        private long rejectedSequenceAdvances;
        private long forwardErrorCorrectionAttempts;
        private long concealedFrames;
        private long decoderResets;
        private int maximumPendingEncodedFrames;
        private int targetPlaybackSamples;
        private bool wasLocallyMuted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            decoderPool.Clear();
        }

        private void Awake() {
            registeredPlaybackSourceId = PlaybackSourceRegistry.NO_SOURCE_ID;
            CacheComponentReferences();
            ConfigureAudioSource();
            RegisterPlaybackSource();
        }

        public override void OnDestroy() {
            StopPlayback();
            base.OnDestroy();
        }

        private void OnEnable() {
            VoiceChatSettings.OnReceivingEnabled.Listen(HandleReceivingEnabledChanged);
            RegisterPlaybackSource();
            if (IsSpawned && NetworkManager != null && NetworkManager.IsClient) {
                StartPlayback();
            }
        }

        private void OnDisable() {
            VoiceChatSettings.OnReceivingEnabled.Unlisten(HandleReceivingEnabledChanged);
            StopPlayback();
        }

        private void Update() {
            if (audioSource == null || sampleBuffer == null || reorderBuffer == null || playbackRenderer == null) { return; }

            bool isLocallyMuted = GetIsLocallyMuted();
            audioSource.mute = isLocallyMuted;
            if (isLocallyMuted && !wasLocallyMuted) {
                ResetPlayback();
            }
            wasLocallyMuted = isLocallyMuted;

            double currentTime = Time.unscaledTimeAsDouble;
            if (!isLocallyMuted) {
                ProcessPendingFrames(currentTime, false);
            }
            if (playbackRenderer == null || sampleBuffer == null) { return; }

            playbackRenderer.Update(sampleBuffer.GetSampleCount() >= targetPlaybackSamples);
            if (!playbackRenderer.HasStarted || currentTime - lastFrameReceivedAt > PITCH_CORRECTION_ACTIVITY_TIMEOUT) {
                audioSource.pitch = 1f;
                return;
            }

            float bufferedErrorSeconds = (BufferedSamples - targetPlaybackSamples) / (float)VoiceEncoderSession.SAMPLE_RATE;
            float pitchCorrection = Mathf.Clamp(bufferedErrorSeconds * pitchCorrectionGain, -maxPitchCorrection, maxPitchCorrection);
            audioSource.pitch = 1f + pitchCorrection;
        }

        private void Reset() {
            CacheComponentReferences();
            targetBufferMilliseconds = 60;
            maxBufferMilliseconds = 350;
            maximumReorderDelayMilliseconds = 40;
            maxConcealedFrames = 3;
            minDistance = 1.5f;
            maxDistance = 25f;
            localVolume = 1f;
            maxPitchCorrection = .03f;
            pitchCorrectionGain = .5f;
            spatialization = string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName())
                ? SpatializationMode.UnityThreeDimensional
                : SpatializationMode.ProjectSpatializer;
            EnsureAudioSource();
            ConfigureAudioSource();
        }

        private void OnValidate() {
            CacheComponentReferences();
            maxBufferMilliseconds = Mathf.Max(maxBufferMilliseconds, targetBufferMilliseconds + 20);
            maximumReorderDelayMilliseconds = Mathf.Clamp(maximumReorderDelayMilliseconds, 0, targetBufferMilliseconds);
            maxDistance = Mathf.Max(maxDistance, minDistance + .1f);
            localVolume = Mathf.Clamp(localVolume, 0f, 2f);
            ConfigureAudioSource();
            RegisterPlaybackSource();
        }

        public override void OnNetworkSpawn() {
            RegisterPlaybackSource();
            if (NetworkManager.IsClient && isActiveAndEnabled) {
                StartPlayback();
            }
        }

        public override void OnNetworkDespawn() {
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

        public void SetLocalVolume(float volume) {
            localVolume = Mathf.Clamp(volume, 0f, 2f);
            RefreshLocalVolume();
        }

        public void ClearPlayback() {
            if (sampleBuffer != null) {
                sampleBuffer.Clear();
            }
            if (decoder != null) {
                decoder.ResetState();
            }
            if (reorderBuffer != null) {
                reorderBuffer.Clear();
            }
            if (playbackRenderer != null) {
                playbackRenderer.Reset();
            }
        }

        public void ResetDiagnostics() {
            receivedFrames = 0;
            missingFrames = 0;
            intentionalSilenceSamples = 0;
            lateFrames = 0;
            reorderedFrames = 0;
            duplicateFrames = 0;
            finalizedGaps = 0;
            queuePressureEvents = 0;
            rejectedSequenceAdvances = 0;
            forwardErrorCorrectionAttempts = 0;
            concealedFrames = 0;
            decoderResets = 0;
            maximumPendingEncodedFrames = 0;
            if (sampleBuffer != null) {
                sampleBuffer.ResetDiagnostics();
            }
            if (playbackRenderer != null) {
                playbackRenderer.ResetDiagnostics();
            }
        }

        public VoicePlaybackDiagnostics GetDiagnostics() {
            SampleRingBuffer playbackBuffer = sampleBuffer;
            AudioSettings.GetDSPBufferSize(out int dspBufferSamples, out int dspBufferCount);
            CircularAudioClipRenderer renderer = playbackRenderer;
            float bufferedMilliseconds = BufferedSamples * 1000f / VoiceEncoderSession.SAMPLE_RATE;
            return new(
                0,
                AudioSettings.outputSampleRate,
                dspBufferSamples,
                dspBufferCount,
                bufferedMilliseconds,
                receivedFrames,
                missingFrames,
                intentionalSilenceSamples * 1000f / VoiceEncoderSession.SAMPLE_RATE,
                lateFrames,
                reorderedFrames,
                duplicateFrames,
                finalizedGaps,
                queuePressureEvents,
                rejectedSequenceAdvances,
                forwardErrorCorrectionAttempts,
                reorderBuffer == null ? 0 : reorderBuffer.Count,
                maximumPendingEncodedFrames,
                concealedFrames,
                decoderResets,
                playbackBuffer == null ? 0 : playbackBuffer.PrebufferWaitCount,
                playbackBuffer == null ? 0 : playbackBuffer.RebufferCount,
                playbackBuffer == null ? 0 : playbackBuffer.ConcurrentReadAbortCount,
                playbackBuffer == null ? 0 : playbackBuffer.PartialReadCount,
                playbackBuffer == null ? 0f : playbackBuffer.OverflowedSampleCount * 1000f / VoiceEncoderSession.SAMPLE_RATE,
                renderer == null ? 0 : renderer.RenderQuantumSamples,
                renderer == null ? 0f : renderer.ScheduledLeadSamples * 1000f / VoiceEncoderSession.SAMPLE_RATE,
                renderer == null ? 0L : renderer.UnderrunCount,
                renderer == null ? 0L : renderer.RebaseCount);
        }

        internal void RefreshLocalVolume() {
            CacheComponentReferences();
            if (audioSource == null) { return; }
            float participantVolume = participant == null ? 1f : participant.PlaybackVolume;
            audioSource.volume = localVolume * participantVolume;
        }

        internal void RetirePlaybackStream(ulong sourceClientId) {
            if (reorderBuffer != null && !reorderBuffer.HasCurrentSource(sourceClientId)) { return; }

            if (sampleBuffer != null) {
                sampleBuffer.Clear();
            }
            if (decoder != null) {
                decoder.ResetState();
            }
            if (reorderBuffer != null) {
                reorderBuffer.RetireCurrentStream(sourceClientId);
            }
            if (playbackRenderer != null) {
                playbackRenderer.Reset();
            }
        }

        internal void ResetPlayback() {
            if (sampleBuffer != null) {
                sampleBuffer.Clear();
            }
            if (decoder != null) {
                decoder.ResetState();
            }
            if (reorderBuffer != null) {
                reorderBuffer.ResetCurrentStream();
            }
            if (playbackRenderer != null) {
                playbackRenderer.Reset();
            }
        }

        internal void ReceiveFrame(VoiceFrame frame) {
            if (decoder == null || reorderBuffer == null || sampleBuffer == null || playbackRenderer == null || !frame.IsValid || GetIsLocallyMuted()) { return; }

            receivedFrames++;
            double receivedAt = Time.unscaledTimeAsDouble;
            EncodedFrameReorderBuffer.Arrival arrival = reorderBuffer.Enqueue(frame, receivedAt);
            if (arrival == EncodedFrameReorderBuffer.Arrival.Invalid) { return; }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Duplicate) {
                duplicateFrames++;
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Late) {
                lateFrames++;
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.SequenceAdvanceRejected) {
                rejectedSequenceAdvances++;
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.StreamFormatRejected) {
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.SampleTimelineRejected) {
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.CapacityReached) {
                queuePressureEvents++;
                ProcessPendingFrames(receivedAt, true);
                arrival = reorderBuffer.Enqueue(frame, receivedAt);
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.CapacityReached) { return; }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Discontinuity) {
                RestartEncodedStream(frame, receivedAt);
                return;
            }
            if (arrival == EncodedFrameReorderBuffer.Arrival.Reordered) {
                reorderedFrames++;
            }

            lastFrameReceivedAt = receivedAt;
            maximumPendingEncodedFrames = Math.Max(maximumPendingEncodedFrames, reorderBuffer.Count);
            bool forceDeadline = reorderBuffer.RequiresForcedDeadline;
            if (forceDeadline) {
                queuePressureEvents++;
            }
            ProcessPendingFrames(receivedAt, forceDeadline);
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
                Debug.LogError("Speaker needs an AudioSource.", this);
                return;
            }

            ConfigureAudioSource();
            int maxBufferSamples = VoiceEncoderSession.SAMPLE_RATE * maxBufferMilliseconds / 1000;
            int configuredTargetSamples = VoiceEncoderSession.SAMPLE_RATE * targetBufferMilliseconds / 1000;
            int renderLeadSamples = GetRenderLeadSamples(maxBufferSamples, configuredTargetSamples);
            targetPlaybackSamples = Math.Min(maxBufferSamples, configuredTargetSamples + renderLeadSamples);
            SampleRingBuffer playbackBuffer = new(maxBufferSamples, targetPlaybackSamples);
            sampleBuffer = playbackBuffer;
            decoder = decoderPool.Get();
            reorderBuffer = new(MAXIMUM_PENDING_ENCODED_FRAMES, maximumReorderDelayMilliseconds, MAXIMUM_SEQUENCE_ADVANCE);
            playbackRenderer = new CircularAudioClipRenderer(
                audioSource,
                $"Voice {NetworkObjectId}",
                renderLeadSamples,
                maxBufferSamples,
                playbackBuffer.Read);
            RegisterPlaybackSource();
        }

        private void StopPlayback() {
            UnregisterPlaybackSource();
            if (audioSource != null) {
                audioSource.Stop();
                audioSource.pitch = 1f;
            }
            if (playbackRenderer != null) {
                playbackRenderer.Dispose();
                playbackRenderer = null;
            }
            if (decoder != null) {
                decoderPool.Release(decoder);
                decoder = null;
            }
            reorderBuffer = null;
            sampleBuffer = null;
            targetPlaybackSamples = 0;
        }

        private void ProcessPendingFrames(double currentTime, bool forceDeadline) {
            if (decoder == null || reorderBuffer == null || sampleBuffer == null) { return; }

            VoiceDecoderSession processingDecoder = decoder;
            EncodedFrameReorderBuffer processingReorderBuffer = reorderBuffer;
            SampleRingBuffer processingSampleBuffer = sampleBuffer;
            int renderQuantumSamples = playbackRenderer == null
                ? (int)VoiceFrameLength.TwentyMilliseconds
                : playbackRenderer.RenderQuantumSamples;
            while (processingReorderBuffer.TryTake(
                currentTime,
                BufferedSamples,
                renderQuantumSamples,
                forceDeadline,
                out VoiceFrame frame,
                out uint missingFrameCount,
                out uint intentionalSilenceSampleCount,
                out bool gapFinalized)) {
                try {
                    DecodeFrame(frame, missingFrameCount, intentionalSilenceSampleCount, gapFinalized);
                }
                catch (Exception exception) {
                    HandleDecoderFailure(exception);
                    return;
                }

                DispatchFrameLevel(frame.Level);
                if (decoder != processingDecoder ||
                    reorderBuffer != processingReorderBuffer ||
                    sampleBuffer != processingSampleBuffer) {
                    return;
                }
                forceDeadline = false;
            }
        }

        private void DecodeFrame(VoiceFrame frame, uint missingFrameCount, uint intentionalSilenceSampleCount, bool gapFinalized) {
            if (gapFinalized) {
                finalizedGaps++;
            }
            missingFrames += missingFrameCount;
            intentionalSilenceSamples += intentionalSilenceSampleCount;
            long trailingSilenceSampleCount = sampleBuffer.TakeTrailingSilenceSampleCount();
            long missingSampleCount = (long)missingFrameCount * frame.SampleCount;
            long totalGapSampleCount = missingSampleCount + intentionalSilenceSampleCount;
            long remainingGapSampleCount = Math.Max(0L, totalGapSampleCount - trailingSilenceSampleCount);
            if (intentionalSilenceSampleCount > 0) {
                if (trailingSilenceSampleCount == 0L && missingFrameCount <= maxConcealedFrames) {
                    concealedFrames += missingFrameCount;
                    for (uint i = 0; i < missingFrameCount; i++) {
                        sampleBuffer.Write(decoder.Conceal(frame.SampleCount));
                    }
                    remainingGapSampleCount -= missingSampleCount;
                }

                decoder.ResetState();
                decoderResets++;
                sampleBuffer.WriteSilence((int)Math.Min(remainingGapSampleCount, sampleBuffer.Capacity));
                sampleBuffer.Write(decoder.Decode(frame, false));
                return;
            }

            if (missingFrameCount > maxConcealedFrames || trailingSilenceSampleCount > 0L) {
                decoder.ResetState();
                decoderResets++;
                sampleBuffer.WriteSilence((int)Math.Min(remainingGapSampleCount, sampleBuffer.Capacity));
            }
            else if (missingFrameCount > 0) {
                forwardErrorCorrectionAttempts++;
                concealedFrames += missingFrameCount;
                for (uint i = 1; i < missingFrameCount; i++) {
                    sampleBuffer.Write(decoder.Conceal(frame.SampleCount));
                }
                sampleBuffer.Write(decoder.Decode(frame, true));
            }

            sampleBuffer.Write(decoder.Decode(frame, false));
        }

        private void RestartEncodedStream(VoiceFrame frame, double receivedAt) {
            decoder.ResetState();
            decoderResets++;

            EncodedFrameReorderBuffer.Arrival arrival = reorderBuffer.BeginStream(frame, receivedAt);
            if (arrival != EncodedFrameReorderBuffer.Arrival.Accepted) { return; }
            lastFrameReceivedAt = receivedAt;
            maximumPendingEncodedFrames = Math.Max(maximumPendingEncodedFrames, reorderBuffer.Count);
            ProcessPendingFrames(receivedAt, false);
        }

        private void HandleDecoderFailure(Exception exception) {
            decoder.ResetState();
            reorderBuffer.ResetCurrentStream();
            decoderResets++;
            if (Time.unscaledTimeAsDouble < nextDecoderWarningAt) { return; }
            nextDecoderWarningAt = Time.unscaledTimeAsDouble + 1d;
            Debug.LogWarning($"A voice frame could not be decoded: {exception.Message}", this);
        }

        private void DispatchFrameLevel(byte level) {
            try {
                OnFrameLevel.Dispatch(level / (float)byte.MaxValue);
            }
            catch (Exception exception) {
                if (Time.unscaledTimeAsDouble < nextFrameLevelWarningAt) { return; }
                nextFrameLevelWarningAt = Time.unscaledTimeAsDouble + 1d;
                Debug.LogWarning($"A voice level listener failed: {exception.Message}", this);
            }
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

        private void CacheComponentReferences() {
            if (participant == null) {
                TryGetComponent(out participant);
            }
        }

        private void EnsureAudioSource() {
            if (audioSource != null) { return; }

            Transform playbackTransform = transform.Find(VOICE_PLAYBACK_OBJECT_NAME);
            if (playbackTransform == null) {
                GameObject playbackObject = new(VOICE_PLAYBACK_OBJECT_NAME);
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
            RefreshLocalVolume();

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

        private static VoiceDecoderSession CreateDecoder() => new();

        private static void ResetDecoder(VoiceDecoderSession voiceDecoder) {
            voiceDecoder.ResetState();
        }

        private static void DisposeDecoder(VoiceDecoderSession voiceDecoder) {
            voiceDecoder.Dispose();
        }

        private static int GetRenderLeadSamples(int maxBufferSamples, int configuredTargetSamples) {
            AudioSettings.GetDSPBufferSize(out int dspBufferSamples, out int dspBufferCount);
            int desiredRenderLeadSamples = Math.Max(
                (int)VoiceFrameLength.TwentyMilliseconds * 2,
                dspBufferSamples * dspBufferCount);
            return Math.Clamp(desiredRenderLeadSamples, 1, Math.Max(1, maxBufferSamples - configuredTargetSamples));
        }

        private bool GetIsLocallyMuted() {
            if (!VoiceChatSettings.IsReceivingEnabled) { return true; }
            if (participant != null && participant.IsPlaybackMuted) { return true; }

            VoiceParticipant localVoiceParticipant;
            return VoiceParticipant.TryGetLocal(NetworkManager, out localVoiceParticipant) && localVoiceParticipant.IsDeafened;
        }

    }

}
