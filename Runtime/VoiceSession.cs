using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Session")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager))]
    [DefaultExecutionOrder(-100)]
    public sealed class VoiceSession : MonoBehaviour {

        [Header("Connection")]
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private VoiceDeliveryMode deliveryMode = VoiceDeliveryMode.Automatic;
        [SerializeField] private string listenAddress = "0.0.0.0";
        [SerializeField] private string advertisedAddress = "";
        [SerializeField, Range(1, 65535)] private int voicePort = 7778;
        [SerializeField, Range(1, 65535)] private int steamVirtualPort = 37;
        [SerializeField, Range(1, 128)] private int maxPeers = 128;

        [Header("Local Audio")]
        [SerializeField] private bool sendingEnabled = true;
        [SerializeField] private bool receivingEnabled = true;
        [SerializeField, Range(0f, 4f)] private float microphoneVolume = 1f;
        [SerializeField] private bool overrideInputDevice;
        [SerializeField] private VoiceDeviceId inputDevice;

        [Header("Decode Policy")]
        [SerializeField, Range(0, 100)] private int maximumReorderDelayMilliseconds = 10;
        [SerializeField, Range(0, 8)] private int maxConcealedFrames = 3;

        private sealed class BroadcastReceiver : IVoicePacketReceiver {

            private readonly VoicePlaybackOutput[] outputs;

            internal BroadcastReceiver(VoicePlaybackOutput[] outputs) {
                this.outputs = outputs;
            }

            public void ReceivePacket(VoicePacket packet) {
                for (int i = 0; i < outputs.Length; i++) { outputs[i].ReceivePacket(packet); }
            }

        }

        public readonly Signal<bool> OnSendingEnabled = new();
        public readonly Signal<bool> OnReceivingEnabled = new();
        public readonly Signal<VoiceDeviceId> OnInputDeviceChanged = new();
        public readonly Signal OnDevicesChanged = new();
        public bool IsSendingEnabled => sendingEnabled;
        public bool IsReceivingEnabled => receivingEnabled;
        public float MicrophoneVolume => microphoneVolume;
        public VoiceDeviceId InputDevice => inputDevice;
        public NetworkManager NetworkManager => networkManager;
        public VoiceDeliveryMode DeliveryMode => deliveryMode;
        public VoiceTransportCapability TransportCapability => networkSession == null ? VoiceTransportCapability.FrameDriven : networkSession.Capability;
        public VoiceTransportState TransportState => !SupportsThreadedAudio ? VoiceTransportState.Unavailable : networkSession == null ? VoiceTransportState.Stopped : networkSession.State;
        public string TransportFailure => !SupportsThreadedAudio ? "This platform requires a browser voice backend." : networkSession == null ? "" : networkSession.FailureReason;
        internal VoiceProcessingWorker ProcessingWorker => processingWorker;
        // Device capture runs on its own worker so the microphone signal can wake it directly.
        internal VoiceProcessingWorker CaptureWorker => captureWorker;
        internal VoicePlaybackEngine PlaybackEngine => playbackEngine;
        internal VoiceNetworkSession NetworkSession => networkSession;

        private readonly List<VoiceParticipant> participants = new(16);
        private readonly HashSet<ulong> activeParticipantOwners = new();
        private readonly List<VoiceRouteSnapshot> routes = new(32);
        private readonly List<VoiceRouteRecipient> recipients = new(32);
        private readonly List<AreaMicrophone> voiceCaptures = new(8);
        private readonly HashSet<VoiceRouteId> registeredReceivers = new();
        private readonly HashSet<VoiceRouteId> currentReceivers = new();
        private readonly List<VoiceRouteId> removedReceivers = new();
        private VoiceProcessingWorker processingWorker;
        private VoiceProcessingWorker captureWorker;
        private VoicePlaybackEngine playbackEngine;
        private VoiceNetworkSession networkSession;
        private float nextDevicesRefreshAt;
        private float nextRoutesRefreshAt;
        private bool observedSendingEnabled = true;
        private bool observedReceivingEnabled = true;
        private float observedMicrophoneVolume = 1f;
        private bool observedOverrideInputDevice;
        private VoiceDeviceId observedInputDevice;
#if UNITY_WEBGL && !UNITY_EDITOR
        private static bool SupportsThreadedAudio => false;
#else
        private static bool SupportsThreadedAudio => true;
#endif
        private bool isStopping;
        private ulong observedEpoch;
        private IReadOnlyList<VoiceDeviceDescriptor> observedDevices;
        private VoiceCaptureState observedDevicesState;

        private void Awake() {
            CacheReferences();
            observedSendingEnabled = sendingEnabled;
            observedReceivingEnabled = receivingEnabled;
            observedMicrophoneVolume = microphoneVolume;
            observedOverrideInputDevice = overrideInputDevice;
            observedInputDevice = inputDevice;
            EnsureRuntime();
        }

        private void OnDestroy() {
            StopRuntime();
        }

        private void OnEnable() {
            CacheReferences();
        }

        private void OnDisable() {
            StopRuntime();
        }

        private void Update() {
            SynchronizeSettings();
            if (networkManager == null || !networkManager.IsListening) {
                if (networkSession != null) { StopRuntime(); }
                return;
            }
            EnsureRuntime();
            if (networkSession == null) { return; }
            networkSession.Pump();
            if (observedEpoch != networkSession.Epoch) {
                observedEpoch = networkSession.Epoch;
                playbackEngine.Reset();
                nextRoutesRefreshAt = 0f;
            }
            if (Time.unscaledTime >= nextRoutesRefreshAt) {
                PublishRouting();
                nextRoutesRefreshAt = Time.unscaledTime + .025f;
            }
            if (Time.unscaledTime >= nextDevicesRefreshAt && networkManager.IsClient) {
                RefreshDevices();
                nextDevicesRefreshAt = Time.unscaledTime + 2f;
            }
        }

        private void Reset() {
            CacheReferences();
            voicePort = 7778;
            listenAddress = "0.0.0.0";
            advertisedAddress = "";
            maximumReorderDelayMilliseconds = 10;
            maxConcealedFrames = 3;
            microphoneVolume = 1f;
        }

        private void OnValidate() {
            CacheReferences();
            voicePort = Mathf.Clamp(voicePort, 1, 65535);
            steamVirtualPort = Mathf.Clamp(steamVirtualPort, 1, 65535);
            maxPeers = Mathf.Clamp(maxPeers, 1, 128);
            maximumReorderDelayMilliseconds = Mathf.Clamp(maximumReorderDelayMilliseconds, 0, 100);
            maxConcealedFrames = Mathf.Clamp(maxConcealedFrames, 0, 8);
            microphoneVolume = float.IsNaN(microphoneVolume) || float.IsInfinity(microphoneVolume) ? 0f : Mathf.Clamp(microphoneVolume, 0f, 4f);
        }

        public void SetSendingEnabled(bool isEnabled) {
            if (sendingEnabled == isEnabled) { return; }
            sendingEnabled = isEnabled;
            SynchronizeSettings();
        }

        public void SetReceivingEnabled(bool isEnabled) {
            if (receivingEnabled == isEnabled) { return; }
            receivingEnabled = isEnabled;
            SynchronizeSettings();
        }

        public void SetMicrophoneVolume(float volume) {
            float normalizedVolume = float.IsNaN(volume) || float.IsInfinity(volume) ? 0f : Mathf.Clamp(volume, 0f, 4f);
            if (microphoneVolume == normalizedVolume) { return; }
            microphoneVolume = normalizedVolume;
            SynchronizeSettings();
        }

        public void SetAudioEnabled(bool isEnabled) {
            SetSendingEnabled(isEnabled);
            SetReceivingEnabled(isEnabled);
        }

        public void SetInputDevice(VoiceDeviceId deviceId) {
            overrideInputDevice = true;
            inputDevice = deviceId;
            SynchronizeSettings();
        }

        public void RefreshDevices() {
            MicrophoneDevices.Refresh();
            IReadOnlyList<VoiceDeviceDescriptor> devices = MicrophoneDevices.GetAvailableDevices();
            VoiceCaptureState state = MicrophoneDevices.State;
            if (ReferenceEquals(observedDevices, devices) && observedDevicesState == state) { return; }
            observedDevices = devices;
            observedDevicesState = state;
            OnDevicesChanged.Dispatch();
        }

        public void ConfigureConnection(VoiceNetworkOptions options) {
            if (options == null) { throw new ArgumentNullException(nameof(options)); }
            if (networkSession != null) { throw new InvalidOperationException("Configure the voice connection before starting the game session."); }
            deliveryMode = options.DeliveryMode;
            listenAddress = string.IsNullOrWhiteSpace(options.ListenAddress) ? "0.0.0.0" : options.ListenAddress;
            advertisedAddress = options.AdvertisedAddress == null ? "" : options.AdvertisedAddress;
            voicePort = Math.Max(1, (int)options.Port);
            steamVirtualPort = Math.Max(1, (int)options.SteamVirtualPort);
            maxPeers = Mathf.Clamp(options.MaxPeers, 1, 128);
        }

        internal void RefreshParticipant(VoiceParticipant participant) {
            if (playbackEngine != null && participant != null) {
                playbackEngine.SetParticipantGain(participant.OwnerClientId, participant.IsPlaybackMuted ? 0f : participant.PlaybackVolume);
            }
            nextRoutesRefreshAt = 0f;
        }

        internal void ApplyInputPreference(MicrophoneInput microphone) {
            if (microphone != null && overrideInputDevice) { microphone.SetInputDevice(inputDevice); }
        }

        public static VoiceSession GetOrCreate(NetworkManager manager) {
            if (manager == null) { return null; }
            if (!manager.TryGetComponent(out VoiceSession session)) { session = VoiceComponentSetup.AddComponent<VoiceSession>(manager.gameObject); }
            session.networkManager = manager;
            if (Application.isPlaying && session.isActiveAndEnabled) { session.EnsureRuntime(); }
            return session;
        }

        public static bool TryGet(NetworkManager manager, out VoiceSession session) {
            session = null;
            return manager != null && manager.TryGetComponent(out session);
        }

        public IReadOnlyList<VoiceDeviceDescriptor> GetAvailableDevices() => MicrophoneDevices.GetAvailableDevices();

        private void EnsureRuntime() {
            if (!SupportsThreadedAudio || isStopping || !isActiveAndEnabled || networkManager == null || !networkManager.IsListening) { return; }
            if (processingWorker == null) {
                playbackEngine = new(maximumReorderDelayMilliseconds, maxConcealedFrames);
                playbackEngine.SetReceivingEnabled(receivingEnabled);
                processingWorker = new();
                processingWorker.Register(playbackEngine);
                playbackEngine.SetWakeHandler(processingWorker.Wake);
                captureWorker = new("Voice Capture", System.Threading.ThreadPriority.Highest);
            }
            if (networkSession != null) { return; }
            VoiceNetworkOptions options = new() {
                DeliveryMode = deliveryMode,
                ListenAddress = listenAddress,
                AdvertisedAddress = advertisedAddress,
                Port = (ushort)voicePort,
                SteamVirtualPort = (ushort)steamVirtualPort,
                MaxPeers = maxPeers
            };
            networkSession = new(networkManager, options);
            networkSession.SetSendingEnabled(sendingEnabled);
            networkSession.Start();
            nextRoutesRefreshAt = 0f;
        }

        private void StopRuntime() {
            if (isStopping) { return; }
            isStopping = true;
            if (playbackEngine != null) { playbackEngine.SetReceivingEnabled(false); }
            if (captureWorker != null) { captureWorker.Dispose(); }
            if (processingWorker != null) { processingWorker.Dispose(); }
            if (networkSession != null) { networkSession.Dispose(); }
            captureWorker = null;
            processingWorker = null;
            playbackEngine = null;
            networkSession = null;
            observedEpoch = 0;
            registeredReceivers.Clear();
            currentReceivers.Clear();
            isStopping = false;
        }

        private void SynchronizeSettings() {
            bool sendingChanged = observedSendingEnabled != sendingEnabled;
            bool receivingChanged = observedReceivingEnabled != receivingEnabled;
            bool microphoneVolumeChanged = observedMicrophoneVolume != microphoneVolume;
            bool deviceChanged = observedOverrideInputDevice != overrideInputDevice || observedInputDevice != inputDevice;
            if (!sendingChanged && !receivingChanged && !microphoneVolumeChanged && !deviceChanged) { return; }
            observedSendingEnabled = sendingEnabled;
            observedReceivingEnabled = receivingEnabled;
            observedMicrophoneVolume = microphoneVolume;
            observedOverrideInputDevice = overrideInputDevice;
            observedInputDevice = inputDevice;
            if (sendingChanged && networkSession != null) { networkSession.SetSendingEnabled(sendingEnabled); }
            if (receivingChanged && playbackEngine != null) { playbackEngine.SetReceivingEnabled(receivingEnabled); }
            if (sendingChanged) { OnSendingEnabled.Dispatch(sendingEnabled); }
            if (receivingChanged) { OnReceivingEnabled.Dispatch(receivingEnabled); }
            if (deviceChanged) {
                if (VoiceParticipant.TryGetLocal(networkManager, out VoiceParticipant participant) && participant.TryGetComponent(out MicrophoneInput microphone)) { ApplyInputPreference(microphone); }
                OnInputDeviceChanged.Dispatch(inputDevice);
            }
            RefreshLocalControls();
            if (sendingChanged || receivingChanged) { PublishRouting(); }
        }

        private void RefreshLocalControls() {
            if (!VoiceParticipant.TryGetLocal(networkManager, out VoiceParticipant participant)) { return; }
            participant.RefreshAudioSettings();
            if (participant.TryGetComponent(out MicrophoneInput microphone)) { microphone.RefreshControl(); }
        }

        private void PublishRouting() {
            if (networkSession == null || playbackEngine == null || networkManager == null || !networkManager.IsListening) { return; }
            participants.Clear();
            activeParticipantOwners.Clear();
            IReadOnlyList<VoiceParticipant> spawned = VoiceParticipant.SpawnedParticipants;
            for (int i = 0; i < spawned.Count; i++) {
                VoiceParticipant participant = spawned[i];
                if (participant == null || !participant.IsSpawned || participant.NetworkManager != networkManager || !participant.isActiveAndEnabled) { continue; }
                participants.Add(participant);
                activeParticipantOwners.Add(participant.OwnerClientId);
            }
            playbackEngine.RetainParticipantGains(activeParticipantOwners);
            currentReceivers.Clear();
            for (int i = 0; i < participants.Count; i++) {
                VoiceParticipant participant = participants[i];
                if (!participant.TryGetComponent(out SpeakerOutput output)) { continue; }
                output.EnsurePlayback();
                if (output.PlaybackOutput == null) { continue; }
                RegisterReceiver(VoiceRouteId.Participant(participant.NetworkObjectId), output.PlaybackOutput);
                RefreshParticipant(participant);
            }
            if (BroadcastRouter.TryGet(networkManager, out BroadcastRouter router)) {
                List<VoicePlaybackOutput> outputs = new();
                IReadOnlyList<ChannelSpeaker> speakers = ChannelSpeaker.ActiveSpeakers;
                for (int i = 0; i < speakers.Count; i++) {
                    ChannelSpeaker speaker = speakers[i];
                    if (speaker != null && speaker.IsSpawned && speaker.NetworkManager == networkManager && speaker.PlaybackOutput != null) { outputs.Add(speaker.PlaybackOutput); }
                }
                RegisterReceiver(VoiceRouteId.Broadcast(router.NetworkObjectId), new BroadcastReceiver(outputs.ToArray()));
            }
            removedReceivers.Clear();
            foreach (VoiceRouteId receiver in registeredReceivers) {
                if (!currentReceivers.Contains(receiver)) { removedReceivers.Add(receiver); }
            }
            for (int i = 0; i < removedReceivers.Count; i++) {
                networkSession.UnregisterReceiver(removedReceivers[i]);
                registeredReceivers.Remove(removedReceivers[i]);
            }
            if (!networkSession.IsAuthority) { return; }
            routes.Clear();
            for (int i = 0; i < participants.Count; i++) { AddParticipantRoute(participants[i], router); }
            IReadOnlyList<AreaMicrophone> microphones = AreaMicrophone.ActiveMicrophones;
            for (int i = 0; i < microphones.Count; i++) {
                AreaMicrophone microphone = microphones[i];
                if (microphone == null || !microphone.IsSpawned || microphone.NetworkManager != networkManager) { continue; }
                recipients.Clear();
                if (router != null) {
                    BroadcastRoute route = new(microphone.Channel, microphone.NetworkObjectId, microphone.NetworkObjectId);
                    AddBroadcastRecipients(router, route);
                }
                bool enabled = microphone.isActiveAndEnabled && microphone.IsOpen && microphone.CapturesWorldAudio && VoiceParticipant.IsClientSendingEnabled(networkManager, microphone.CaptureClientId);
                routes.Add(new(VoiceRouteId.World(microphone.NetworkObjectId), microphone.CaptureClientId, new(ConversationMembership.EVERYONE, 0), enabled, microphone.MaximumFramesPerSecond, recipients.ToArray()));
            }
            networkSession.PublishRoutes(routes.ToArray());
        }

        private void AddParticipantRoute(VoiceParticipant participant, BroadcastRouter router) {
            recipients.Clear();
            for (int i = 0; i < participants.Count; i++) {
                VoiceParticipant listener = participants[i];
                if (listener.OwnerClientId == participant.OwnerClientId && !participant.LocalLoopback || !listener.IsReceivingEnabled || listener.IsDeafened || !participant.Membership.Reaches(listener.Membership) || !participant.NetworkObject.IsNetworkVisibleTo(listener.OwnerClientId)) { continue; }
                VoiceRouteRecipient recipient = new(listener.OwnerClientId, VoiceRouteId.Participant(participant.NetworkObjectId));
                if (!recipients.Contains(recipient)) { recipients.Add(recipient); }
            }
            if (router != null) {
                AreaMicrophone.CollectVoiceCaptures(networkManager, participant.transform.position, participant.OwnerClientId, voiceCaptures);
                for (int i = 0; i < voiceCaptures.Count; i++) {
                    AreaMicrophone microphone = voiceCaptures[i];
                    BroadcastRoute route = new(microphone.Channel, microphone.NetworkObjectId, participant.NetworkObjectId, 0, participant.OwnerClientId);
                    AddBroadcastRecipients(router, route);
                }
            }
            bool enabled = participant.IsSendingEnabled && !participant.IsInputMuted && !participant.IsDeafened;
            routes.Add(new(VoiceRouteId.Participant(participant.NetworkObjectId), participant.OwnerClientId, participant.Membership, enabled, participant.MaximumFramesPerSecond, recipients.ToArray()));
        }

        private void AddBroadcastRecipients(BroadcastRouter router, BroadcastRoute route) {
            IReadOnlyList<ulong> clients = networkManager.ConnectedClientsIds;
            IReadOnlyList<ChannelSpeaker> speakers = ChannelSpeaker.ActiveSpeakers;
            for (int i = 0; i < clients.Count; i++) {
                ulong clientId = clients[i];
                if (!VoiceParticipant.IsClientReceivingEnabled(networkManager, clientId) || !router.NetworkObject.IsNetworkVisibleTo(clientId)) { continue; }
                if (clientId == networkManager.LocalClientId && !networkManager.IsClient || networkManager.CMBServiceConnection && clientId == NetworkManager.ServerClientId) { continue; }
                bool listens = false;
                for (int speakerIndex = 0; speakerIndex < speakers.Count; speakerIndex++) {
                    ChannelSpeaker speaker = speakers[speakerIndex];
                    if (speaker != null && speaker.IsSpawned && speaker.NetworkManager == networkManager && speaker.Channel == route.Channel && speaker.NetworkObject.IsNetworkVisibleTo(clientId)) { listens = true; break; }
                }
                if (!listens) { continue; }
                VoiceRouteRecipient recipient = new(clientId, VoiceRouteId.Broadcast(router.NetworkObjectId), route);
                if (!recipients.Contains(recipient)) { recipients.Add(recipient); }
            }
        }

        private void RegisterReceiver(VoiceRouteId route, IVoicePacketReceiver receiver) {
            networkSession.RegisterReceiver(route, receiver);
            registeredReceivers.Add(route);
            currentReceivers.Add(route);
        }

        private void CacheReferences() {
            if (networkManager == null) { TryGetComponent(out networkManager); }
        }

    }

}
