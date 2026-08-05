using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Participant")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(MicrophoneInput), typeof(SpeakerOutput))]
    public sealed class VoiceParticipant : NetworkBehaviour {

        [SerializeField, HideInInspector] private MicrophoneInput microphoneInput;
        [SerializeField, HideInInspector] private SpeakerOutput speakerOutput;
        [SerializeField, HideInInspector] private ConversationGroup conversationGroup;

        [Header("Local Defaults")]
        [SerializeField] private bool startInputMuted;
        [SerializeField] private bool startDeafened;
        [SerializeField] private bool localLoopback;

        [Header("Relay Validation")]
        [SerializeField, Range(20, 120)] private int maxFramesPerSecond = 110;

        public readonly Signal<ParticipantState> OnStateChanged = new();
        public readonly Signal<bool> OnInputMuted = new();
        public readonly Signal<bool> OnDeafened = new();
        public readonly Signal<bool> OnSpeaking = new();
        public readonly Signal<bool> OnPlaybackMuted = new();

        public static IReadOnlyList<VoiceParticipant> SpawnedParticipants => readonlySpawnedParticipants;
        public static VoiceParticipant LocalParticipant => localParticipant != null && localParticipant.isActiveAndEnabled ? localParticipant : null;
        public NetworkVariable<ParticipantState> State => participantState;
        public ParticipantState SyncedState => participantState.Value;
        public bool IsInputMuted => participantState.Value.IsInputMuted;
        public bool IsDeafened => participantState.Value.IsDeafened;
        public bool IsSpeaking => participantState.Value.IsSpeaking;
        public float VoiceLevel => participantState.Value.Level / (float)byte.MaxValue;
        public bool IsPlaybackMuted => isPlaybackMuted;
        public bool LocalLoopback => localLoopback;
        public ConversationMembership Membership => GetMembership();

        private static readonly List<VoiceParticipant> spawnedParticipants = new(16);
        private static readonly ReadOnlyCollection<VoiceParticipant> readonlySpawnedParticipants = spawnedParticipants.AsReadOnly();
        private static readonly Dictionary<NetworkManager, VoiceParticipant> localParticipantsByNetworkManager = new();

        private const double AUTHORITY_STREAM_TIMEOUT = 2d;
        private const uint MAXIMUM_SEQUENCE_ADVANCE = 1024;
        private static VoiceParticipant localParticipant;

        private readonly List<AreaMicrophone> broadcastCaptures = new(8);
        private readonly NetworkVariable<ParticipantState> participantState = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        private NativeList<ulong> relayTargets;
        private NetworkManager localParticipantNetworkManager;
        private FrameReplayWindow authorityReplayWindow;
        private double authorityWindowStartedAt;
        private double authorityLastFrameAt;
        private int authorityFramesInWindow;
        private bool hasLocalParticipantNetworkManager;
        private bool isPlaybackMuted;

        private void Awake() {
            relayTargets = new(16, Allocator.Persistent);
            CacheComponentReferences();
        }

        public override void OnDestroy() {
            participantState.OnValueChanged -= HandleStateChanged;
            UnregisterParticipant();
            EndLocalOwnership();
            if (relayTargets.IsCreated) {
                relayTargets.Dispose();
            }
            base.OnDestroy();
        }

        private void Reset() {
            CacheComponentReferences();
            maxFramesPerSecond = 110;
        }

        private void OnValidate() {
            CacheComponentReferences();
        }

        public override void OnNetworkSpawn() {
            RegisterParticipant();
            ResetAuthorityStream();
            if (IsClient && IsOwner) {
                BeginLocalOwnership();
            }
            participantState.OnValueChanged += HandleStateChanged;
            OnStateChanged.Dispatch(participantState.Value);
        }

        public override void OnNetworkDespawn() {
            participantState.OnValueChanged -= HandleStateChanged;
            UnregisterParticipant();
            EndLocalOwnership();
            ResetAuthorityStream();
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            if (IsClient) {
                BeginLocalOwnership();
            }
        }

        public override void OnLostOwnership() {
            EndLocalOwnership();
            base.OnLostOwnership();
        }

        public void SetInputMuted(bool isMuted) {
            if (!CanWriteLocalState()) { return; }
            SetState(participantState.Value.WithInputMuted(isMuted).WithActivity(false, 0));
        }

        public void ToggleInputMuted() {
            SetInputMuted(!IsInputMuted);
        }

        public void SetDeafened(bool isDeafened) {
            if (!CanWriteLocalState()) { return; }
            SetState(participantState.Value.WithDeafened(isDeafened).WithActivity(false, 0));
        }

        public void ToggleDeafened() {
            SetDeafened(!IsDeafened);
        }

        public void SetPlaybackMuted(bool isMuted) {
            if (isPlaybackMuted == isMuted) { return; }
            isPlaybackMuted = isMuted;
            OnPlaybackMuted.Dispatch(isMuted);
            if (isMuted && speakerOutput != null) {
                speakerOutput.ResetPlayback();
            }
        }

        public void TogglePlaybackMuted() {
            SetPlaybackMuted(!isPlaybackMuted);
        }

        public void SetTransmissionHeld(bool isHeld) {
            if (microphoneInput == null) { return; }
            microphoneInput.SetTransmissionHeld(isHeld);
        }

        public void BeginTransmission() {
            SetTransmissionHeld(true);
        }

        public void EndTransmission() {
            SetTransmissionHeld(false);
        }

        public void SetLocalLoopback(bool isEnabled) {
            localLoopback = isEnabled;
        }

        internal void SendFrame(VoiceFrame frame) {
            if (!IsSpawned || !IsClient || !IsOwner || !isActiveAndEnabled || IsInputMuted || IsDeafened || !frame.IsValid) { return; }

            frame.SourceClientId = OwnerClientId;
            frame = frame.WithMembership(GetMembership());
            if (localLoopback && speakerOutput != null) {
                speakerOutput.ReceiveFrame(frame);
            }
            if (HasAuthority) {
                SubmitFrameAtAuthority(frame, NetworkManager.LocalClientId);
            }
            else {
                SubmitFrameRpc(frame);
            }
        }

        internal void SetLocalActivity(bool isSpeaking, byte level) {
            if (!CanWriteLocalState()) { return; }
            SetState(participantState.Value.WithActivity(isSpeaking, level));
        }

        internal void SetConversationGroup(ConversationGroup newConversationGroup) {
            conversationGroup = newConversationGroup;
        }

        internal void RemoveConversationGroup(ConversationGroup removedConversationGroup) {
            if (conversationGroup != removedConversationGroup) { return; }
            conversationGroup = null;
        }

        internal static bool TryGetLocal(NetworkManager networkManager, out VoiceParticipant participant) {
            participant = null;
            if (networkManager == null || !localParticipantsByNetworkManager.TryGetValue(networkManager, out participant)) { return false; }
            if (participant != null && participant.IsSpawned && participant.isActiveAndEnabled) { return true; }

            participant = null;
            return false;
        }

        public static bool TryGetLocal(out VoiceParticipant participant) {
            participant = LocalParticipant;
            return participant != null && participant.IsSpawned;
        }

        protected override void OnOwnershipChanged(ulong previousOwnerClientId, ulong currentOwnerClientId) {
            ResetAuthorityStream();
            if (speakerOutput != null) {
                speakerOutput.RetirePlaybackStream(previousOwnerClientId);
            }
            base.OnOwnershipChanged(previousOwnerClientId, currentOwnerClientId);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            spawnedParticipants.Clear();
            localParticipantsByNetworkManager.Clear();
            localParticipant = null;
        }

        [Rpc(SendTo.Authority, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        private void SubmitFrameRpc(VoiceFrame frame, RpcParams rpcParams = default) {
            SubmitFrameAtAuthority(frame, rpcParams.Receive.SenderClientId);
        }

        private void SubmitFrameAtAuthority(VoiceFrame frame, ulong senderClientId) {
            if (!HasAuthority ||
                !frame.IsValid ||
                frame.Membership != GetMembership() ||
                senderClientId != OwnerClientId ||
                frame.SourceClientId != senderClientId ||
                !AcceptFrameAtAuthority(frame)) {
                return;
            }

            if (TryBuildRelayTargets(frame.Membership)) {
                RpcParams relayParams = default;
                relayParams.Send.Target = RpcTarget.Group(relayTargets, RpcTargetUse.Temp);

                if (NetworkManager.DistributedAuthorityMode) {
                    RelayDistributedAuthorityFrameRpc(frame, relayParams);
                }
                else {
                    RelayClientServerFrameRpc(frame, relayParams);
                }
            }

            RelayBroadcastFrames(frame);
        }

        [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Server)]
        private void RelayClientServerFrameRpc(VoiceFrame frame, RpcParams rpcParams = default) {
            if (!IsClient || IsOwner || NetworkManager.DistributedAuthorityMode || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) { return; }
            DeliverFrame(frame);
        }

        [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        private void RelayDistributedAuthorityFrameRpc(VoiceFrame frame, RpcParams rpcParams = default) {
            if (!IsClient || !NetworkManager.DistributedAuthorityMode || rpcParams.Receive.SenderClientId != OwnerClientId) { return; }
            DeliverFrame(frame);
        }

        private void HandleStateChanged(ParticipantState previousState, ParticipantState currentState) {
            OnStateChanged.Dispatch(currentState);
            if (previousState.IsInputMuted != currentState.IsInputMuted) {
                OnInputMuted.Dispatch(currentState.IsInputMuted);
            }
            if (previousState.IsDeafened != currentState.IsDeafened) {
                OnDeafened.Dispatch(currentState.IsDeafened);
            }
            if (previousState.IsSpeaking != currentState.IsSpeaking) {
                OnSpeaking.Dispatch(currentState.IsSpeaking);
            }
        }

        private void SetState(ParticipantState state) {
            if (participantState.Value == state) { return; }
            participantState.Value = state;
        }

        private void BeginLocalOwnership() {
            localParticipant = this;
            RegisterLocalParticipantForManager();
            ParticipantState initialState = participantState.Value
                .WithInputMuted(startInputMuted)
                .WithDeafened(startDeafened)
                .WithActivity(false, 0);
            SetState(initialState);
        }

        private void EndLocalOwnership() {
            UnregisterLocalParticipantForManager();
            if (localParticipant == this) {
                localParticipant = GetFallbackLocalParticipant();
            }
        }

        private void RegisterLocalParticipantForManager() {
            NetworkManager currentNetworkManager = NetworkManager;
            if (currentNetworkManager == null) { return; }

            UnregisterLocalParticipantForManager();
            localParticipantsByNetworkManager[currentNetworkManager] = this;
            localParticipantNetworkManager = currentNetworkManager;
            hasLocalParticipantNetworkManager = true;
        }

        private void UnregisterLocalParticipantForManager() {
            if (!hasLocalParticipantNetworkManager) { return; }

            if (localParticipantsByNetworkManager.TryGetValue(localParticipantNetworkManager, out VoiceParticipant registeredParticipant) && registeredParticipant == this) {
                localParticipantsByNetworkManager.Remove(localParticipantNetworkManager);
            }
            localParticipantNetworkManager = null;
            hasLocalParticipantNetworkManager = false;
        }

        private void RegisterParticipant() {
            if (spawnedParticipants.Contains(this)) { return; }
            spawnedParticipants.Add(this);
        }

        private void UnregisterParticipant() {
            spawnedParticipants.Remove(this);
        }

        private void DeliverFrame(VoiceFrame frame) {
            if (!frame.IsValid ||
                speakerOutput == null ||
                frame.Membership != GetMembership() ||
                frame.SourceClientId != OwnerClientId) {
                return;
            }

            VoiceParticipant localVoiceParticipant = GetLocalParticipantForManager();
            if (localVoiceParticipant == null) {
                if (!frame.Membership.IsEveryone) { return; }
            } else if (!frame.Membership.Reaches(localVoiceParticipant.GetMembership())) {
                return;
            }
            speakerOutput.ReceiveFrame(frame);
        }

        private void RelayBroadcastFrames(VoiceFrame frame) {
            AreaMicrophone.CollectVoiceCaptures(NetworkManager, transform.position, OwnerClientId, broadcastCaptures);
            for (int i = 0; i < broadcastCaptures.Count; i++) {
                AreaMicrophone microphone = broadcastCaptures[i];
                microphone.SubmitVoiceFrame(NetworkObjectId, OwnerClientId, frame);
            }
        }

        private bool TryBuildRelayTargets(ConversationMembership sourceMembership) {
            if (!relayTargets.IsCreated || NetworkManager == null || NetworkManager.SpawnManager == null) { return false; }
            relayTargets.Clear();

            IReadOnlyList<NetworkObject> playerObjects = NetworkManager.SpawnManager.PlayerObjects;
            for (int i = 0; i < playerObjects.Count; i++) {
                NetworkObject playerObject = playerObjects[i];
                ulong listenerClientId = playerObject.OwnerClientId;
                if (listenerClientId == OwnerClientId || !NetworkObject.IsNetworkVisibleTo(listenerClientId) || ContainsRelayTarget(listenerClientId)) { continue; }

                if (!playerObject.TryGetComponent(out VoiceParticipant listener) || !sourceMembership.Reaches(listener.GetMembership())) { continue; }
                relayTargets.Add(listenerClientId);
            }
            return relayTargets.Length > 0;
        }

        private void ResetAuthorityStream() {
            authorityReplayWindow.Clear();
            authorityWindowStartedAt = 0d;
            authorityLastFrameAt = 0d;
            authorityFramesInWindow = 0;
        }

        private void CacheComponentReferences() {
            if (microphoneInput == null) {
                TryGetComponent(out microphoneInput);
            }
            if (speakerOutput == null) {
                TryGetComponent(out speakerOutput);
            }
            if (conversationGroup == null) {
                TryGetComponent(out conversationGroup);
            }
        }

        private bool AcceptFrameAtAuthority(VoiceFrame frame) {
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

        private ConversationMembership GetMembership() {
            return conversationGroup == null
                ? new(ConversationMembership.EVERYONE, 0)
                : conversationGroup.Membership.Value;
        }

        private VoiceParticipant GetLocalParticipantForManager() {
            return TryGetLocal(NetworkManager, out VoiceParticipant localVoiceParticipant) ? localVoiceParticipant : null;
        }

        private static VoiceParticipant GetFallbackLocalParticipant() {
            foreach (VoiceParticipant participant in localParticipantsByNetworkManager.Values) {
                if (participant != null && participant.IsSpawned && participant.isActiveAndEnabled) { return participant; }
            }
            return null;
        }

        private bool ContainsRelayTarget(ulong clientId) {
            for (int i = 0; i < relayTargets.Length; i++) {
                if (relayTargets[i] == clientId) { return true; }
            }
            return false;
        }

        private bool CanWriteLocalState() => IsSpawned && IsClient && IsOwner && isActiveAndEnabled;

    }

}
