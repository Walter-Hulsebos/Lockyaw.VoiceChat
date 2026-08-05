using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Identity")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(VoiceParticipant))]
    public sealed class SpeakerIdentity : NetworkBehaviour {

        [SerializeField, HideInInspector] private VoiceParticipant participant;

        [Header("Display Name")]
        [SerializeField] private MonoBehaviour nameResolver;
        [SerializeField] private string fallbackDisplayName = "Player";

        public static IReadOnlyList<SpeakerIdentity> Instances => readonlyIdentities;
        public static ISpeakerNameResolver DefaultResolver {
            get => defaultResolver;
            set {
                if (defaultResolver == value) { return; }
                defaultResolver = value;
                for (int i = 0; i < identities.Count; i++) {
                    identities[i].ScheduleNameResolution();
                }
            }
        }

        public readonly Signal<SpeakerProfile> OnProfileChanged = new();

        public NetworkVariable<SpeakerProfile> Profile => profile;
        public string DisplayName => cachedDisplayName;
        public VoiceParticipant Participant => GetParticipant();
        public bool IsSpeaking {
            get {
                VoiceParticipant currentParticipant = GetParticipant();
                return currentParticipant != null && currentParticipant.IsSpeaking;
            }
        }
        public float VoiceLevel {
            get {
                VoiceParticipant currentParticipant = GetParticipant();
                return currentParticipant == null ? 0f : currentParticipant.VoiceLevel;
            }
        }

        private static readonly List<SpeakerIdentity> identities = new(16);
        private static readonly ReadOnlyCollection<SpeakerIdentity> readonlyIdentities = identities.AsReadOnly();

        private const float RESOLUTION_RETRY_SECONDS = 1f;

        private static ISpeakerNameResolver defaultResolver;

        private readonly NetworkVariable<SpeakerProfile> profile = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        private ISpeakerNameResolver runtimeResolver;
        private string cachedDisplayName = "";
        private float nextResolutionAt;
        private bool shouldRetryResolution;

        private void Awake() {
            CacheComponentReferences();
            CacheDisplayName(profile.Value);
            ResolveConfiguredResolver();
        }

        public override void OnDestroy() {
            profile.OnValueChanged -= HandleProfileChanged;
            UnregisterIdentity();
            base.OnDestroy();
        }

        private void Update() {
            if (!shouldRetryResolution || Time.unscaledTime < nextResolutionAt) { return; }
            RefreshDisplayName();
        }

        private void Reset() {
            CacheComponentReferences();
            fallbackDisplayName = "Player";
        }

        private void OnValidate() {
            CacheComponentReferences();
            fallbackDisplayName = string.IsNullOrWhiteSpace(fallbackDisplayName) ? "Player" : fallbackDisplayName.Trim();
        }

        public override void OnNetworkSpawn() {
            RegisterIdentity();
            profile.OnValueChanged += HandleProfileChanged;
            CacheDisplayName(profile.Value);
            OnProfileChanged.Dispatch(profile.Value);
            if (IsOwner) {
                BeginLocalOwnership();
            }
        }

        public override void OnNetworkDespawn() {
            profile.OnValueChanged -= HandleProfileChanged;
            shouldRetryResolution = false;
            UnregisterIdentity();
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            BeginLocalOwnership();
        }

        public override void OnLostOwnership() {
            shouldRetryResolution = false;
            base.OnLostOwnership();
        }

        public void SetResolver(ISpeakerNameResolver resolver) {
            runtimeResolver = resolver;
            ScheduleNameResolution();
        }

        public bool SetDisplayName(string displayName) {
            if (!CanRequestProfileChange()) { return false; }
            shouldRetryResolution = false;
            return SubmitDisplayName(displayName);
        }

        public bool RefreshDisplayName() {
            if (!CanRequestProfileChange()) { return false; }

            ISpeakerNameResolver resolver = GetResolver();
            string resolvedDisplayName;
            if (resolver != null && resolver.TryResolveDisplayName(this, out resolvedDisplayName) && SubmitDisplayName(resolvedDisplayName)) {
                shouldRetryResolution = false;
                return true;
            }

            shouldRetryResolution = resolver != null;
            nextResolutionAt = Time.unscaledTime + RESOLUTION_RETRY_SECONDS;
            return false;
        }

        [Rpc(SendTo.Authority, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Owner)]
        private void SubmitProfileRpc(SpeakerProfile requestedProfile, RpcParams rpcParams = default) {
            if (!CanWriteProfile() || rpcParams.Receive.SenderClientId != OwnerClientId || !requestedProfile.IsValid) { return; }
            ApplyDisplayName(requestedProfile.DisplayName.ToString());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            identities.Clear();
            defaultResolver = null;
        }

        private void HandleProfileChanged(SpeakerProfile previousProfile, SpeakerProfile currentProfile) {
            if (previousProfile == currentProfile) { return; }
            CacheDisplayName(currentProfile);
            OnProfileChanged.Dispatch(currentProfile);
        }

        private void BeginLocalOwnership() {
            if (!CanRequestProfileChange()) { return; }
            SubmitDisplayName(GetFallbackDisplayName());
            shouldRetryResolution = true;
            nextResolutionAt = 0f;
            RefreshDisplayName();
        }

        private void ScheduleNameResolution() {
            if (!IsSpawned || !IsOwner) { return; }
            shouldRetryResolution = true;
            nextResolutionAt = 0f;
        }

        private void ResolveConfiguredResolver() {
            if (nameResolver == null) { return; }
            runtimeResolver = nameResolver as ISpeakerNameResolver;
            if (runtimeResolver != null) { return; }
            Debug.LogError("Identity name resolver must implement ISpeakerNameResolver.", this);
        }

        private void RegisterIdentity() {
            if (identities.Contains(this)) { return; }
            identities.Add(this);
        }

        private void UnregisterIdentity() {
            identities.Remove(this);
        }

        private void CacheComponentReferences() {
            if (participant == null) {
                TryGetComponent(out participant);
            }
        }

        private void CacheDisplayName(SpeakerProfile currentProfile) {
            cachedDisplayName = currentProfile.DisplayName.ToString();
        }

        private bool SubmitDisplayName(string displayName) {
            SpeakerProfile requestedProfile = profile.Value.WithDisplayName(displayName);
            if (!requestedProfile.IsValid) { return false; }

            if (HasAuthority && CanWriteProfile()) {
                ApplyDisplayName(requestedProfile.DisplayName.ToString());
            }
            else {
                SubmitProfileRpc(requestedProfile);
            }
            return true;
        }

        private void ApplyDisplayName(string displayName) {
            SpeakerProfile requestedProfile = profile.Value.WithDisplayName(displayName);
            if (!requestedProfile.IsValid || profile.Value == requestedProfile) { return; }
            profile.Value = requestedProfile;
        }

        private string GetFallbackDisplayName() {
            string namePrefix = string.IsNullOrWhiteSpace(fallbackDisplayName) ? "Player" : fallbackDisplayName.Trim();
            return $"{namePrefix} {OwnerClientId}";
        }

        private ISpeakerNameResolver GetResolver() => runtimeResolver == null ? defaultResolver : runtimeResolver;

        private VoiceParticipant GetParticipant() {
            CacheComponentReferences();
            return participant;
        }

        private bool CanRequestProfileChange() {
            return IsSpawned &&
                   IsClient &&
                   IsOwner &&
                   isActiveAndEnabled &&
                   NetworkManager != null;
        }

        private bool CanWriteProfile() {
            return IsSpawned &&
                   HasAuthority &&
                   isActiveAndEnabled &&
                   NetworkManager != null &&
                   profile.CanClientWrite(NetworkManager.LocalClientId);
        }

    }

}
