using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Input/Microphone")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VoiceParticipant))]
    public sealed class MicrophoneInput : NetworkBehaviour {

        [SerializeField, HideInInspector] private VoiceParticipant participant;

        [Header("Device")]
        [SerializeField] private string requestedDeviceName = "";
        [SerializeField, Min(.25f)] private float reconnectInterval = 2f;

        [Header("Transmission")]
        [SerializeField] private TransmissionMode transmissionMode = TransmissionMode.Open;
        [SerializeField, Range(-60f, 0f)] private float activationThreshold = -42f;
        [SerializeField, Range(0f, 2f)] private float activationRelease = .2f;

        [Header("Encoding")]
        [SerializeField] private VoiceCodecSettings codecSettings = default;

        [Header("Presence")]
        [SerializeField, Range(.05f, .5f)] private float stateUpdateInterval = .1f;

        public readonly Signal<string> OnDeviceChanged = new();
        public readonly Signal<bool> OnCaptureActive = new();

        public string SelectedDeviceName => microphoneReader == null ? "" : microphoneReader.DeviceName;
        public bool IsCapturing => isCaptureActive;
        public bool IsTransmissionHeld => isTransmissionHeld;
        public VoiceCodecSettings CodecSettings => codecSettings;

        private const int MAXIMUM_FRAMES_PER_UPDATE = 8;

        private MicrophoneReader microphoneReader;
        private VoiceEncoderSession encoder;
        private Coroutine startCaptureRoutine;
        private uint sequence;
        private float nextReconnectAt;
        private float voiceReleaseAt;
        private float nextStateUpdateAt;
        private bool isTransmissionHeld;
        private bool previousSpeaking;
        private byte previousLevel;
        private bool isCaptureRequested = true;
        private bool isAndroidPermissionRequestBlocked;
        private bool isCaptureActive;
        private long capturedFrames;
        private long encodedFrames;
        private long suppressedFrames;
        private long submittedFrames;
        private int maximumFramesPerUpdate;

        private void Awake() {
            CacheComponentReferences();
            if (!VoiceFrame.IsSupportedSampleCount(codecSettings.SamplesPerFrame)) {
                codecSettings = VoiceCodecSettings.Default;
            }
        }

        public override void OnDestroy() {
            StopCapture();
            base.OnDestroy();
        }

        private void OnEnable() {
            if (!HasLocalCaptureOwnership()) { return; }
            StartCaptureAutomatically();
        }

        private void OnDisable() {
            SuspendCapture();
        }

        private void Update() {
            if (!CanCapture()) {
                if (HasCaptureResources()) {
                    SuspendCapture();
                }
                return;
            }

            if (!GetIsReaderRecording()) {
                if (HasCaptureResources()) {
                    SuspendCapture();
                }

                if (isCaptureRequested && !isAndroidPermissionRequestBlocked && startCaptureRoutine == null && Time.unscaledTime >= nextReconnectAt) {
                    nextReconnectAt = Time.unscaledTime + reconnectInterval;
                    StartCaptureAutomatically();
                }
                return;
            }

            int processedFrames = 0;
            while (processedFrames < MAXIMUM_FRAMES_PER_UPDATE && TryReadCaptureFrame(out ReadOnlySpan<float> samples)) {
                ProcessFrame(samples);
                processedFrames++;
                if (microphoneReader == null) { break; }
            }
            maximumFramesPerUpdate = Math.Max(maximumFramesPerUpdate, processedFrames);
        }

        private void Reset() {
            CacheComponentReferences();
            codecSettings = VoiceCodecSettings.Default;
            transmissionMode = TransmissionMode.Open;
            activationThreshold = -42f;
            activationRelease = .2f;
            reconnectInterval = 2f;
            stateUpdateInterval = .1f;
        }

        private void OnValidate() {
            CacheComponentReferences();
            codecSettings.Validate();
            reconnectInterval = Mathf.Max(.25f, reconnectInterval);
            stateUpdateInterval = Mathf.Clamp(stateUpdateInterval, .05f, .5f);
        }

        public override void OnNetworkSpawn() {
            if (!HasLocalCaptureOwnership()) {
                SuspendCapture();
                return;
            }
            StartCaptureAutomatically();
        }

        public override void OnNetworkDespawn() {
            SuspendCapture();
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            if (!HasLocalCaptureOwnership()) { return; }
            StartCaptureAutomatically();
        }

        public override void OnLostOwnership() {
            SuspendCapture();
            base.OnLostOwnership();
        }

        public void SetDevice(string deviceName) {
            string normalizedDeviceName = deviceName == null ? "" : deviceName;
            if (requestedDeviceName == normalizedDeviceName) { return; }

            requestedDeviceName = normalizedDeviceName;
            if (IsSpawned && IsOwner) {
                StartCapture();
            }
        }

        public void StartCapture() {
            isCaptureRequested = true;
            isAndroidPermissionRequestBlocked = false;
            RestartCapture();
        }

        public void StopCapture() {
            isCaptureRequested = false;
            SuspendCapture();
        }

        public void SetTransmissionHeld(bool isHeld) {
            isTransmissionHeld = isHeld;
        }

        public void BeginTransmission() {
            SetTransmissionHeld(true);
        }

        public void EndTransmission() {
            SetTransmissionHeld(false);
        }

        public void ResetDiagnostics() {
            capturedFrames = 0;
            encodedFrames = 0;
            suppressedFrames = 0;
            submittedFrames = 0;
            maximumFramesPerUpdate = 0;
            if (microphoneReader != null) {
                microphoneReader.ResetDiagnostics();
            }
        }

        public string[] GetAvailableDevices() => Microphone.devices;

        public VoiceCaptureDiagnostics GetDiagnostics() {
            return new(
                SelectedDeviceName,
                microphoneReader == null ? 0 : microphoneReader.SampleRate,
                microphoneReader == null ? 0 : microphoneReader.ChannelCount,
                microphoneReader == null ? 0f : microphoneReader.QueuedMilliseconds,
                microphoneReader == null ? 0f : microphoneReader.DroppedMilliseconds,
                capturedFrames,
                encodedFrames,
                suppressedFrames,
                submittedFrames,
                maximumFramesPerUpdate);
        }

        private IEnumerator StartCaptureRoutine() {
            if (!CanCapture()) {
                startCaptureRoutine = null;
                yield break;
            }

            yield return RequestAndroidMicrophonePermissionRoutine();

            startCaptureRoutine = null;
            bool hadAndroidPermissionFailure = isAndroidPermissionRequestBlocked;
            bool hasAndroidMicrophonePermission = false;
            if (!hadAndroidPermissionFailure && !TryGetAndroidMicrophonePermission(out hasAndroidMicrophonePermission)) {
                hadAndroidPermissionFailure = true;
            }

            if (hadAndroidPermissionFailure || !hasAndroidMicrophonePermission) {
                isAndroidPermissionRequestBlocked = true;
                if (!hadAndroidPermissionFailure) {
                    Debug.LogWarning("Android microphone access is unavailable for the local voice participant.", this);
                }
                SuspendCapture();
                yield break;
            }

            if (!isCaptureRequested || !CanCapture()) {
                yield break;
            }

            string selectedDeviceName = ResolveDeviceName();
            VoiceEncoderSession startedEncoder = null;
            MicrophoneReader startedReader = null;
            try {
                startedEncoder = new(codecSettings);
                startedReader = new(selectedDeviceName, codecSettings.SamplesPerFrame);
                if (!startedReader.Start()) {
                    DisposeStartedResources(startedReader, startedEncoder);
                    nextReconnectAt = Time.unscaledTime + reconnectInterval;
                    yield break;
                }
            }
            catch (Exception exception) {
                DisposeStartedResources(startedReader, startedEncoder);
                Debug.LogWarning($"Microphone capture could not start: {exception.Message}", this);
                nextReconnectAt = Time.unscaledTime + reconnectInterval;
                yield break;
            }

            if (!isCaptureRequested || !CanCapture()) {
                DisposeStartedResources(startedReader, startedEncoder);
                yield break;
            }

            encoder = startedEncoder;
            microphoneReader = startedReader;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log($"Voice capture started from '{selectedDeviceName}' at {startedReader.SampleRate} Hz with {startedReader.ChannelCount} channel(s).", this);
#endif
            SetCaptureActive(true);
            OnDeviceChanged.Dispatch(selectedDeviceName);
        }

        private IEnumerator RequestAndroidMicrophonePermissionRoutine() {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!TryGetAndroidMicrophonePermission(out bool hasAndroidMicrophonePermission) || hasAndroidMicrophonePermission) { yield break; }

            bool androidPermissionRequestFinished = false;
            UnityEngine.Android.PermissionCallbacks permissionCallbacks = new();
            permissionCallbacks.PermissionGranted += permissionName => androidPermissionRequestFinished = true;
            permissionCallbacks.PermissionDenied += permissionName => androidPermissionRequestFinished = true;
            permissionCallbacks.PermissionDeniedAndDontAskAgain += permissionName => androidPermissionRequestFinished = true;
            try {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, permissionCallbacks);
            }
            catch (Exception exception) {
                BlockAndroidPermissionAfterException(exception);
                yield break;
            }

            while (!androidPermissionRequestFinished) {
                yield return null;
            }
#else
            yield break;
#endif
        }

        private void ProcessFrame(ReadOnlySpan<float> samples) {
            capturedFrames++;
            float decibels = AudioMath.GetDecibels(samples);
            byte level = AudioMath.GetNetworkLevel(decibels);
            bool isAboveThreshold = decibels >= activationThreshold;
            if (isAboveThreshold) {
                voiceReleaseAt = Time.unscaledTime + activationRelease;
            }

            bool isVoiceActive = isAboveThreshold || Time.unscaledTime < voiceReleaseAt;
            bool shouldTransmit = GetShouldTransmit(isVoiceActive) && !participant.IsInputMuted && !participant.IsDeafened;
            bool isSpeaking = shouldTransmit && isVoiceActive;
            UpdatePresence(isSpeaking, level, false);
            if (!shouldTransmit || encoder == null || !CanCapture()) { return; }

            VoiceFrame frame;
            try {
                bool hasEncodedFrame = encoder.TryEncode(samples, sequence, level, out frame);
                encodedFrames++;
                if (!hasEncodedFrame) {
                    suppressedFrames++;
                    return;
                }
            }
            catch (Exception exception) {
                Debug.LogWarning($"Voice encoding stopped after an invalid audio frame: {exception.Message}", this);
                SuspendCapture();
                nextReconnectAt = Time.unscaledTime + reconnectInterval;
                return;
            }

            sequence = unchecked(sequence + 1);
            try {
                participant.SendFrame(frame);
                submittedFrames++;
            }
            catch (Exception exception) {
                Debug.LogWarning($"Voice frame transmission failed: {exception.Message}", this);
            }
        }

        private void UpdatePresence(bool isSpeaking, byte level, bool forceUpdate) {
            if (participant == null || !participant.isActiveAndEnabled || !IsClient || !participant.IsSpawned || !participant.IsOwner) { return; }

            bool speakingChanged = previousSpeaking != isSpeaking;
            bool levelChanged = Math.Abs(previousLevel - level) >= 8;
            if (!forceUpdate && !speakingChanged && (!levelChanged || Time.unscaledTime < nextStateUpdateAt)) { return; }

            previousSpeaking = isSpeaking;
            previousLevel = level;
            nextStateUpdateAt = Time.unscaledTime + stateUpdateInterval;
            participant.SetLocalActivity(isSpeaking, isSpeaking ? level : (byte)0);
        }

        private void StartCaptureAutomatically() {
            if (!isCaptureRequested || isAndroidPermissionRequestBlocked || startCaptureRoutine != null || HasCaptureResources() || !CanCapture()) { return; }
            startCaptureRoutine = StartCoroutine(StartCaptureRoutine());
        }

        private void RestartCapture() {
            SuspendCapture();
            StartCaptureAutomatically();
        }

        private void SuspendCapture() {
            if (startCaptureRoutine != null) {
                StopCoroutine(startCaptureRoutine);
                startCaptureRoutine = null;
            }

            StopCaptureResources();
            UpdatePresence(false, 0, true);
        }

        private void StopCaptureResources() {
            MicrophoneReader readerToDispose = microphoneReader;
            VoiceEncoderSession encoderToDispose = encoder;
            microphoneReader = null;
            encoder = null;

            DisposeStartedResources(readerToDispose, encoderToDispose);
            SetCaptureActive(false);
        }

        private void DisposeStartedResources(MicrophoneReader readerToDispose, VoiceEncoderSession encoderToDispose) {
            if (readerToDispose != null) {
                try {
                    readerToDispose.Dispose();
                }
                catch (Exception exception) {
                    Debug.LogWarning($"Microphone cleanup failed: {exception.Message}", this);
                }
            }

            if (encoderToDispose != null) {
                try {
                    encoderToDispose.Dispose();
                }
                catch (Exception exception) {
                    Debug.LogWarning($"Voice encoder cleanup failed: {exception.Message}", this);
                }
            }
        }

        private void SetCaptureActive(bool isActive) {
            if (isCaptureActive == isActive) { return; }
            isCaptureActive = isActive;
            OnCaptureActive.Dispatch(isActive);
        }

        private void CacheComponentReferences() {
            if (participant == null) {
                TryGetComponent(out participant);
            }
        }

        private void BlockAndroidPermissionAfterException(Exception exception) {
            isAndroidPermissionRequestBlocked = true;
            Debug.LogWarning($"Android microphone permission could not be checked: {exception.Message}", this);
        }

        private bool TryReadCaptureFrame(out ReadOnlySpan<float> samples) {
            samples = ReadOnlySpan<float>.Empty;
            if (microphoneReader == null) { return false; }

            try {
                return microphoneReader.TryReadFrame(out samples);
            }
            catch (Exception exception) {
                Debug.LogWarning($"Microphone capture was interrupted: {exception.Message}", this);
                SuspendCapture();
                nextReconnectAt = Time.unscaledTime + reconnectInterval;
                return false;
            }
        }

        private string ResolveDeviceName() {
            string[] deviceNames = Microphone.devices;
            if (!string.IsNullOrWhiteSpace(requestedDeviceName)) {
                for (int i = 0; i < deviceNames.Length; i++) {
                    if (deviceNames[i] == requestedDeviceName) { return requestedDeviceName; }
                }
            }
            return deviceNames.Length == 0 ? "" : deviceNames[0];
        }

        private bool GetShouldTransmit(bool isVoiceActive) {
            return transmissionMode switch {
                TransmissionMode.Open => true,
                TransmissionMode.PushToTalk => isTransmissionHeld,
                TransmissionMode.VoiceActivated => isVoiceActive,
                _ => false
            };
        }

        private bool CanCapture() {
            return isActiveAndEnabled &&
                   HasLocalCaptureOwnership() &&
                   participant.isActiveAndEnabled;
        }

        private bool HasLocalCaptureOwnership() {
            if (!IsSpawned || !IsClient || !IsOwner || participant == null || !participant.IsSpawned || !participant.IsClient || !participant.IsOwner) { return false; }

            NetworkManager currentNetworkManager = NetworkManager;
            return currentNetworkManager != null &&
                   OwnerClientId == currentNetworkManager.LocalClientId &&
                   participant.NetworkObject == NetworkObject &&
                   participant.OwnerClientId == currentNetworkManager.LocalClientId;
        }

        private bool HasCaptureResources() {
            return microphoneReader != null ||
                   encoder != null ||
                   isCaptureActive;
        }

        private bool GetIsReaderRecording() {
            if (microphoneReader == null) { return false; }

            try {
                return microphoneReader.IsRecording;
            }
            catch (Exception exception) {
                Debug.LogWarning($"Microphone capture was interrupted: {exception.Message}", this);
                return false;
            }
        }

        private bool TryGetAndroidMicrophonePermission(out bool hasAndroidMicrophonePermission) {
#if UNITY_ANDROID && !UNITY_EDITOR
            try {
                hasAndroidMicrophonePermission = UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
                return true;
            }
            catch (Exception exception) {
                hasAndroidMicrophonePermission = false;
                BlockAndroidPermissionAfterException(exception);
                return false;
            }
#else
            hasAndroidMicrophonePermission = true;
            return true;
#endif
        }

    }

}
