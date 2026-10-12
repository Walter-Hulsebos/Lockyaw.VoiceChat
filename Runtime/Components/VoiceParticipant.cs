#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Participant")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    public sealed class VoiceParticipant : NetworkBehaviour {

        [SerializeField, HideInInspector] private MicrophoneInput microphoneInput;
        [SerializeField, HideInInspector] private SpeakerOutput speakerOutput;
        [SerializeField, HideInInspector] private ConversationGroup conversationGroup;

        [Header("Local Defaults")]
        [SerializeField] private bool startInputMuted;
        [SerializeField] private bool startDeafened;
        [SerializeField] private bool localLoopback;

        [Header("Relay Validation")]
        [SerializeField, Range(20, 120), Tooltip("Audio the host relays per second, counted in 10 ms frames. 100 is real time at any frame length.")] private int maxFramesPerSecond = 110;

        public static readonly Signal<VoiceParticipant> OnParticipantJoin = new();
        public static readonly Signal<VoiceParticipant> OnParticipantLeave = new();
        public readonly Signal<ParticipantState> OnStateChanged = new();
        public readonly Signal<bool> OnInputMuted = new();
        public readonly Signal<bool> OnDeafened = new();
        public readonly Signal<bool> OnSpeaking = new();
        public readonly Signal<bool> OnPlaybackMuted = new();
        public readonly Signal<float> OnPlaybackVolume = new();
        public static IReadOnlyList<VoiceParticipant> SpawnedParticipants => readonlySpawnedParticipants;
        public NetworkVariable<ParticipantState> State => participantState;
        public ParticipantState SyncedState => participantState.Value;
        public bool IsInputMuted => participantState.Value.IsInputMuted;
        public bool IsDeafened => participantState.Value.IsDeafened;
        public bool IsSpeaking => participantState.Value.IsSpeaking;
        public bool IsSendingEnabled => participantState.Value.IsSendingEnabled;
        public bool IsReceivingEnabled => participantState.Value.IsReceivingEnabled;
        public float VoiceLevel => participantState.Value.Level / (float)byte.MaxValue;
        public bool IsPlaybackMuted => isPlaybackMuted;
        public float PlaybackVolume => playbackVolume;
        public SpeakerIdentity Identity => GetIdentity();
        public bool LocalLoopback => localLoopback;
        public ConversationMembership Membership => conversationGroup == null ? new(ConversationMembership.EVERYONE, 0) : conversationGroup.Membership.Value;
        internal int MaximumFramesPerSecond => maxFramesPerSecond;

        private static readonly List<VoiceParticipant> spawnedParticipants = new(16);
        private static readonly ReadOnlyCollection<VoiceParticipant> readonlySpawnedParticipants = spawnedParticipants.AsReadOnly();
        private readonly NetworkVariable<ParticipantState> participantState = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private VoiceSession session;
        private bool isPlaybackMuted;
        private float playbackVolume = 1f;
        private SpeakerIdentity speakerIdentity;

        private void Awake() {
            if (Application.isPlaying) { VoiceComponentSetup.EnsureParticipant(gameObject); }
            CacheReferences();
        }

        public override void OnDestroy() {
            DetachSession();
            UnregisterParticipant();
            participantState.OnValueChanged -= HandleStateChanged;
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) { RegisterParticipant(); }
        }

        private void OnDisable() {
            if (microphoneInput != null) { microphoneInput.RefreshControl(); }
            UnregisterParticipant();
        }

        private void Reset() {
            VoiceComponentSetup.RequestParticipantSetup(this);
            CacheReferences();
            maxFramesPerSecond = 110;
        }

        private void OnValidate() {
            CacheReferences();
            maxFramesPerSecond = Mathf.Clamp(maxFramesPerSecond, 20, 120);
        }

        public override void OnNetworkSpawn() {
            CacheReferences();
            session = VoiceSession.GetOrCreate(NetworkManager);
            session.OnSendingEnabled.Listen(HandleSendingEnabledChanged);
            session.OnReceivingEnabled.Listen(HandleReceivingEnabledChanged);
            RegisterParticipant();
            if (IsClient && IsOwner) { BeginLocalOwnership(); }
            participantState.OnValueChanged += HandleStateChanged;
            OnStateChanged.Dispatch(participantState.Value);
            session.RefreshParticipant(this);
        }

        public override void OnNetworkDespawn() {
            participantState.OnValueChanged -= HandleStateChanged;
            UnregisterParticipant();
            DetachSession();
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            if (IsClient) { BeginLocalOwnership(); }
        }

        public override void OnLostOwnership() {
            if (microphoneInput != null) { microphoneInput.RefreshControl(); }
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
            if (session != null) { session.RefreshParticipant(this); }
            if (isMuted && speakerOutput != null) { speakerOutput.ResetPlayback(); }
            OnPlaybackMuted.Dispatch(isMuted);
        }

        public void TogglePlaybackMuted() {
            SetPlaybackMuted(!isPlaybackMuted);
        }

        public void SetPlaybackVolume(float volume) {
            if (float.IsNaN(volume)) { return; }
            float normalizedVolume = Mathf.Clamp01(volume);
            if (playbackVolume == normalizedVolume) { return; }
            playbackVolume = normalizedVolume;
            if (session != null) { session.RefreshParticipant(this); }
            OnPlaybackVolume.Dispatch(playbackVolume);
        }

        public void SetTransmissionHeld(bool isHeld) {
            if (microphoneInput != null) { microphoneInput.SetTransmissionHeld(isHeld); }
        }

        public void BeginTransmission() {
            SetTransmissionHeld(true);
        }

        public void EndTransmission() {
            SetTransmissionHeld(false);
        }

        public void SetLocalLoopback(bool isEnabled) {
            localLoopback = isEnabled;
            if (session != null) { session.RefreshParticipant(this); }
        }

        internal void SetLocalActivity(bool isSpeaking, byte level) {
            if (!CanWriteLocalState()) { return; }
            SetState(participantState.Value.WithActivity(isSpeaking && session != null && session.IsSendingEnabled, level));
        }

        internal void RefreshAudioSettings() {
            if (!CanWriteLocalState() || session == null) { return; }
            ParticipantState state = participantState.Value.WithSendingEnabled(session.IsSendingEnabled).WithReceivingEnabled(session.IsReceivingEnabled);
            SetState(session.IsSendingEnabled ? state : state.WithActivity(false, 0));
        }

        internal void SetConversationGroup(ConversationGroup group) {
            conversationGroup = group;
            if (microphoneInput != null) { microphoneInput.RefreshControl(); }
            if (session != null) { session.RefreshParticipant(this); }
        }

        internal void RemoveConversationGroup(ConversationGroup group) {
            if (conversationGroup != group) { return; }
            SetConversationGroup(null);
        }

        internal void CacheReferences() {
            if (microphoneInput == null) { TryGetComponent(out microphoneInput); }
            if (speakerOutput == null) { TryGetComponent(out speakerOutput); }
            if (conversationGroup == null) { TryGetComponent(out conversationGroup); }
        }

        public static bool TryGet(NetworkManager manager, ulong clientId, out VoiceParticipant participant) {
            for (int i = 0; i < spawnedParticipants.Count; i++) {
                VoiceParticipant candidate = spawnedParticipants[i];
                if (candidate != null && candidate.IsSpawned && candidate.NetworkManager == manager && candidate.OwnerClientId == clientId && candidate.isActiveAndEnabled) {
                    participant = candidate;
                    return true;
                }
            }
            participant = null;
            return false;
        }

        public static bool TryGetLocal(NetworkManager manager, out VoiceParticipant participant) {
            participant = null;
            return manager != null && manager.IsClient && TryGet(manager, manager.LocalClientId, out participant);
        }

        internal static bool IsClientSendingEnabled(NetworkManager manager, ulong clientId) {
            if (manager == null) { return false; }
            if (clientId == manager.LocalClientId && VoiceSession.TryGet(manager, out VoiceSession currentSession)) { return currentSession.IsSendingEnabled; }
            return !TryGet(manager, clientId, out VoiceParticipant participant) || participant.IsSendingEnabled;
        }

        internal static bool IsClientReceivingEnabled(NetworkManager manager, ulong clientId) {
            if (manager == null) { return false; }
            if (clientId == manager.LocalClientId && VoiceSession.TryGet(manager, out VoiceSession currentSession) && !currentSession.IsReceivingEnabled) { return false; }
            return !TryGet(manager, clientId, out VoiceParticipant participant) || participant.IsReceivingEnabled && !participant.IsDeafened;
        }

        protected override void OnOwnershipChanged(ulong previousOwnerClientId, ulong currentOwnerClientId) {
            if (speakerOutput != null) { speakerOutput.RetirePlaybackStream(previousOwnerClientId); }
            if (previousOwnerClientId != currentOwnerClientId) {
                SetPlaybackVolume(1f);
                SetPlaybackMuted(false);
            }
            if (session != null) { session.RefreshParticipant(this); }
            base.OnOwnershipChanged(previousOwnerClientId, currentOwnerClientId);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            spawnedParticipants.Clear();
            OnParticipantJoin.Clear();
            OnParticipantLeave.Clear();
        }

        private void HandleSendingEnabledChanged(bool isEnabled) {
            RefreshAudioSettings();
        }

        private void HandleReceivingEnabledChanged(bool isEnabled) {
            RefreshAudioSettings();
        }

        private void HandleStateChanged(ParticipantState previous, ParticipantState current) {
            OnStateChanged.Dispatch(current);
            if (previous.IsInputMuted != current.IsInputMuted) { OnInputMuted.Dispatch(current.IsInputMuted); }
            if (previous.IsDeafened != current.IsDeafened) { OnDeafened.Dispatch(current.IsDeafened); }
            if (previous.IsSpeaking != current.IsSpeaking) { OnSpeaking.Dispatch(current.IsSpeaking); }
            if (microphoneInput != null) { microphoneInput.RefreshControl(); }
            if (session != null) { session.RefreshParticipant(this); }
        }

        private void SetState(ParticipantState state) {
            if (participantState.Value != state) { participantState.Value = state; }
            if (microphoneInput != null) { microphoneInput.RefreshControl(); }
        }

        private void BeginLocalOwnership() {
            if (session == null) { session = VoiceSession.GetOrCreate(NetworkManager); }
            SetState(participantState.Value.WithInputMuted(startInputMuted).WithDeafened(startDeafened).WithSendingEnabled(session.IsSendingEnabled).WithReceivingEnabled(session.IsReceivingEnabled).WithActivity(false, 0));
            session.ApplyInputPreference(microphoneInput);
        }

        private void DetachSession() {
            if (session == null) { return; }
            session.OnSendingEnabled.Unlisten(HandleSendingEnabledChanged);
            session.OnReceivingEnabled.Unlisten(HandleReceivingEnabledChanged);
            session = null;
        }

        private void RegisterParticipant() {
            if (spawnedParticipants.Contains(this)) { return; }
            spawnedParticipants.Add(this);
            OnParticipantJoin.Dispatch(this);
        }

        private void UnregisterParticipant() {
            if (spawnedParticipants.Remove(this)) { OnParticipantLeave.Dispatch(this); }
        }

        private SpeakerIdentity GetIdentity() {
            if (speakerIdentity == null) { TryGetComponent(out speakerIdentity); }
            return speakerIdentity;
        }

        private bool CanWriteLocalState() => IsSpawned && IsClient && IsOwner && isActiveAndEnabled;

    }

}
