using System;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Pool;

using Object = UnityEngine.Object;

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
        public int BufferedSamples => sampleBuffer == null ? 0 : sampleBuffer.GetSampleCount();

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
        private const float RECEIVE_TIMEOUT = .5f;

        private VoiceDecoderSession decoder;
        private EncodedFrameReorderBuffer reorderBuffer;
        private SampleRingBuffer sampleBuffer;
        private AudioClip playbackClip;
        private int registeredPlaybackSourceId;
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
        private int audioCallbackSamples;
        private int maximumPendingEncodedFrames;
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
            RegisterPlaybackSource();
            if (IsSpawned && NetworkManager != null && NetworkManager.IsClient) {
                StartPlayback();
            }
        }

        private void OnDisable() {
            StopPlayback();
        }

        private void Update() {
            if (audioSource == null || sampleBuffer == null || reorderBuffer == null) { return; }

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
            if (currentTime - lastFrameReceivedAt > RECEIVE_TIMEOUT && reorderBuffer.HasStarted) {
                ResetPlayback();
            }

            float configuredTargetSamples = VoiceEncoderSession.SAMPLE_RATE * targetBufferMilliseconds / 1000f;
            float targetSamples = Math.Max(configuredTargetSamples, Volatile.Read(ref audioCallbackSamples));
            float bufferedErrorSeconds = (sampleBuffer.GetSampleCount() - targetSamples) / VoiceEncoderSession.SAMPLE_RATE;
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
            if (audioSource != null) {
                audioSource.volume = localVolume;
            }
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
            if (audioSource != null) {
                audioSource.pitch = 1f;
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
        }

        public VoicePlaybackDiagnostics GetDiagnostics() {
            SampleRingBuffer playbackBuffer = sampleBuffer;
            AudioSettings.GetDSPBufferSize(out int dspBufferSamples, out int dspBufferCount);
            float bufferedMilliseconds = playbackBuffer == null
                ? 0f
                : playbackBuffer.GetSampleCount() * 1000f / VoiceEncoderSession.SAMPLE_RATE;
            return new(
                Volatile.Read(ref audioCallbackSamples),
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
                playbackBuffer == null ? 0f : playbackBuffer.OverflowedSampleCount * 1000f / VoiceEncoderSession.SAMPLE_RATE);
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
            if (audioSource != null) {
                audioSource.pitch = 1f;
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
            if (audioSource != null) {
                audioSource.pitch = 1f;
            }
        }

        internal void ReceiveFrame(VoiceFrame frame) {
            if (decoder == null || reorderBuffer == null || sampleBuffer == null || !frame.IsValid || GetIsLocallyMuted()) { return; }

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

        private void HandleAudioRead(float[] outputSamples, SampleRingBuffer playbackBuffer) {
            Volatile.Write(ref audioCallbackSamples, outputSamples.Length);
            playbackBuffer.Read(outputSamples.AsSpan());
        }

        private void StartPlayback() {
            StopPlayback();
            EnsureAudioSource();
            if (audioSource == null) {
                Debug.LogError("Speaker needs an AudioSource.", this);
                return;
            }

            ConfigureAudioSource();
            int maxBufferSamples = VoiceEncoderSession.SAMPLE_RATE * maxBufferMilliseconds / 1000;
            int prebufferSamples = VoiceEncoderSession.SAMPLE_RATE * targetBufferMilliseconds / 1000;
            SampleRingBuffer playbackBuffer = new(maxBufferSamples, prebufferSamples);
            sampleBuffer = playbackBuffer;
            decoder = decoderPool.Get();
            reorderBuffer = new(MAXIMUM_PENDING_ENCODED_FRAMES, maximumReorderDelayMilliseconds, MAXIMUM_SEQUENCE_ADVANCE);
            playbackClip = AudioClip.Create(
                $"Voice {NetworkObjectId}",
                VoiceEncoderSession.SAMPLE_RATE,
                1,
                VoiceEncoderSession.SAMPLE_RATE,
                true,
                outputSamples => HandleAudioRead(outputSamples, playbackBuffer));
            audioSource.clip = playbackClip;
            RegisterPlaybackSource();
            audioSource.Play();
        }

        private void StopPlayback() {
            UnregisterPlaybackSource();
            if (audioSource != null) {
                audioSource.Stop();
                if (audioSource.clip == playbackClip) {
                    audioSource.clip = null;
                }
                audioSource.pitch = 1f;
            }
            if (playbackClip != null) {
                Object.Destroy(playbackClip);
                playbackClip = null;
            }
            if (decoder != null) {
                decoderPool.Release(decoder);
                decoder = null;
            }
            reorderBuffer = null;
            sampleBuffer = null;
        }

        private void ProcessPendingFrames(double currentTime, bool forceDeadline) {
            if (decoder == null || reorderBuffer == null || sampleBuffer == null) { return; }

            VoiceDecoderSession processingDecoder = decoder;
            EncodedFrameReorderBuffer processingReorderBuffer = reorderBuffer;
            SampleRingBuffer processingSampleBuffer = sampleBuffer;
            while (processingReorderBuffer.TryTake(
                currentTime,
                processingSampleBuffer.GetSampleCount(),
                Volatile.Read(ref audioCallbackSamples),
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
            if (intentionalSilenceSampleCount > 0) {
                if (missingFrameCount <= maxConcealedFrames) {
                    concealedFrames += missingFrameCount;
                    for (uint i = 0; i < missingFrameCount; i++) {
                        sampleBuffer.Write(decoder.Conceal(frame.SampleCount));
                    }
                }

                decoder.ResetState();
                decoderResets++;
                long remainingSilenceSampleCount = Math.Max(0L, intentionalSilenceSampleCount - trailingSilenceSampleCount);
                sampleBuffer.WriteSilence((int)Math.Min(remainingSilenceSampleCount, sampleBuffer.Capacity));
                sampleBuffer.Write(decoder.Decode(frame, false));
                return;
            }

            if (missingFrameCount > maxConcealedFrames) {
                decoder.ResetState();
                decoderResets++;
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

            int audioSourceId = audioSource.GetInstanceID();
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
            audioSource.volume = localVolume;

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

        private bool GetIsLocallyMuted() {
            if (participant != null && participant.IsPlaybackMuted) { return true; }

            VoiceParticipant localVoiceParticipant;
            return VoiceParticipant.TryGetLocal(NetworkManager, out localVoiceParticipant) && localVoiceParticipant.IsDeafened;
        }

    }

}
