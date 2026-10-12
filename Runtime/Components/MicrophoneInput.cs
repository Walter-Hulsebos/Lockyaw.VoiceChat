using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Input/Microphone")]
    [DisallowMultipleComponent]
    public sealed class MicrophoneInput : NetworkBehaviour {

        [SerializeField, HideInInspector] private VoiceParticipant participant;

        [Header("Device")]
        [SerializeField] private VoiceDeviceId deviceId;
        [SerializeField, Min(.25f)] private float reconnectInterval = 2f;

        [Header("Transmission")]
        [SerializeField] private TransmissionMode transmissionMode = TransmissionMode.Open;
        [SerializeField, Range(-60f, 0f)] private float activationThreshold = -42f;
        [SerializeField, Range(0f, 2f)] private float activationRelease = .2f;

        [Header("Encoding")]
        [SerializeField] private VoiceCodecSettings codecSettings = default;

        [Header("Presence")]
        [SerializeField, Range(.05f, .5f)] private float stateUpdateInterval = .1f;

        public readonly Signal<VoiceDeviceId> OnDeviceChanged = new();
        public readonly Signal<bool> OnCaptureActive = new();
        public readonly Signal<VoiceCaptureState> OnCaptureStateChanged = new();
        public VoiceDeviceId DeviceId => deviceId;
        public VoiceDeviceId SelectedDeviceId => GetDiagnostics().DeviceId;
        public string SelectedDeviceName => GetDiagnostics().DeviceName;
        public VoiceCaptureState CaptureState => processor == null ? VoiceCaptureState.Disabled : processor.State;
        public bool IsCapturing => CaptureState == VoiceCaptureState.Running;
        public bool IsTransmissionHeld => isTransmissionHeld;
        public VoiceCodecSettings CodecSettings => codecSettings;

        private VoiceSession session;
        private VoiceCaptureProcessor processor;
        private VoiceProcessingWorker processorOwner;
        private bool isCaptureRequested = true;
        private bool isTransmissionHeld;
        private bool observedCaptureActive;
        private VoiceCaptureState observedState;
        private float nextPresenceAt;
        private bool previousSpeaking;
        private byte previousLevel;

        private void Awake() {
            if (Application.isPlaying) { VoiceComponentSetup.EnsureParticipant(gameObject); }
            CacheReferences();
            if (!VoiceFrame.IsSupportedSampleCount(codecSettings.SamplesPerFrame)) { codecSettings = VoiceCodecSettings.Default; }
        }

        public override void OnDestroy() {
            ReleaseProcessor();
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) { EnsureProcessor(); }
        }

        private void OnDisable() {
            ReleaseProcessor();
        }

        private void Update() {
            if (!IsSpawned || !IsClient || !IsOwner) { return; }
            EnsureProcessor();
            RefreshControl();
            VoiceCaptureState state = CaptureState;
            if (observedState != state) {
                observedState = state;
                OnCaptureStateChanged.Dispatch(state);
            }
            bool active = IsCapturing;
            if (observedCaptureActive != active) {
                observedCaptureActive = active;
                OnCaptureActive.Dispatch(active);
            }
            bool speaking = processor != null && processor.IsSpeaking;
            byte level = processor == null ? (byte)0 : processor.Level;
            if (previousSpeaking == speaking && previousLevel == level || Time.unscaledTime < nextPresenceAt && previousSpeaking == speaking) { return; }
            nextPresenceAt = Time.unscaledTime + stateUpdateInterval;
            previousSpeaking = speaking;
            previousLevel = level;
            if (participant != null) { participant.SetLocalActivity(speaking, level); }
        }

        private void Reset() {
            VoiceComponentSetup.RequestParticipantSetup(this);
            CacheReferences();
            codecSettings = VoiceCodecSettings.Default;
            transmissionMode = TransmissionMode.Open;
            activationThreshold = -42f;
            activationRelease = .2f;
            reconnectInterval = 2f;
            stateUpdateInterval = .1f;
            deviceId = VoiceDeviceId.Default;
        }

        private void OnValidate() {
            CacheReferences();
            codecSettings.Validate();
            reconnectInterval = Mathf.Max(.25f, reconnectInterval);
            stateUpdateInterval = Mathf.Clamp(stateUpdateInterval, .05f, .5f);
        }

        public override void OnNetworkSpawn() {
            EnsureProcessor();
        }

        public override void OnNetworkDespawn() {
            ReleaseProcessor();
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            EnsureProcessor();
        }

        public override void OnLostOwnership() {
            ReleaseProcessor();
            base.OnLostOwnership();
        }

        public void SetInputDevice(VoiceDeviceId selectedDevice) {
            if (deviceId == selectedDevice) { return; }
            deviceId = selectedDevice;
            RefreshControl();
            OnDeviceChanged.Dispatch(deviceId);
        }

        public void StartCapture() {
            isCaptureRequested = true;
            EnsureProcessor();
            RefreshControl();
        }

        public void StopCapture() {
            isCaptureRequested = false;
            RefreshControl();
        }

        public void SetTransmissionHeld(bool isHeld) {
            isTransmissionHeld = isHeld;
            RefreshControl();
        }

        public void BeginTransmission() {
            SetTransmissionHeld(true);
        }

        public void EndTransmission() {
            SetTransmissionHeld(false);
        }

        public void ResetDiagnostics() {
            VoiceCaptureProcessor current = processor;
            if (current != null && processorOwner != null) { processorOwner.TryPost(current.ResetDiagnostics); }
        }

        internal void RefreshControl() {
            if (processor == null) { return; }
            bool enabled = isCaptureRequested && isActiveAndEnabled && IsSpawned && IsClient && IsOwner && session != null && session.isActiveAndEnabled && session.IsSendingEnabled && participant != null && participant.isActiveAndEnabled;
            ConversationMembership membership = participant == null ? new(ConversationMembership.EVERYONE, 0) : participant.Membership;
            bool muted = participant == null || participant.IsInputMuted || participant.IsDeafened;
            processor.Configure(new(enabled, deviceId, isTransmissionHeld, muted, membership, OwnerClientId, session == null ? 1f : session.MicrophoneVolume));
        }

        internal void CacheReferences() {
            if (participant == null) { TryGetComponent(out participant); }
        }

        public IReadOnlyList<VoiceDeviceDescriptor> GetAvailableDevices() => MicrophoneDevices.GetAvailableDevices();

        public VoiceCaptureDiagnostics GetDiagnostics() => processor == null ? new() : processor.Diagnostics;

        private void EnsureProcessor() {
            if (processor != null && (session == null || processorOwner != session.CaptureWorker)) { ReleaseProcessor(); }
            if (processor != null || !IsSpawned || !IsClient || !IsOwner || !isActiveAndEnabled) { return; }
            CacheReferences();
            session = VoiceSession.GetOrCreate(NetworkManager);
            if (session == null || !session.isActiveAndEnabled || session.NetworkSession == null || session.CaptureWorker == null) { return; }
            processorOwner = session.CaptureWorker;
            processor = new(session.NetworkSession, VoiceRouteId.Participant(NetworkObjectId), codecSettings.ToConfiguration(), transmissionMode, activationThreshold, activationRelease, reconnectInterval);
            RefreshControl();
            if (!processorOwner.Register(processor)) {
                processor.Stop();
                processor.Dispose();
                processor = null;
            }
        }

        private void ReleaseProcessor() {
            VoiceCaptureProcessor current = processor;
            processor = null;
            if (current == null) { return; }
            current.Stop();
            if (processorOwner != null) { processorOwner.Unregister(current); }
            processorOwner = null;
            if (participant != null) { participant.SetLocalActivity(false, 0); }
        }

    }

}
