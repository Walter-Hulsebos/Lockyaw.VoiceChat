using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Routing/Conversation Group")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VoiceParticipant))]
    public sealed class ConversationGroup : NetworkBehaviour {

        [SerializeField, HideInInspector] private VoiceParticipant participant;
        [SerializeField, HideInInspector] private SpeakerOutput speakerOutput;

        [Header("Membership")]
        [SerializeField, Min(ConversationMembership.EVERYONE)] private int startingGroup = ConversationMembership.EVERYONE;

        public readonly Signal<ConversationMembership> OnMembershipChanged = new();

        public NetworkVariable<ConversationMembership> Membership => membership;
        public int CurrentGroup => membership.Value.Group;
        public bool CanChangeGroup => IsSpawned && HasAuthority && isActiveAndEnabled;

        private readonly NetworkVariable<ConversationMembership> membership = new(
            new ConversationMembership(ConversationMembership.EVERYONE, 0),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private void Awake() {
            CacheComponentReferences();
            if (participant != null) {
                participant.SetConversationGroup(this);
            }
        }

        public override void OnDestroy() {
            membership.OnValueChanged -= HandleMembershipChanged;
            if (participant != null) {
                participant.RemoveConversationGroup(this);
            }
            base.OnDestroy();
        }

        private void Reset() {
            startingGroup = ConversationMembership.EVERYONE;
            CacheComponentReferences();
            if (participant != null) {
                participant.SetConversationGroup(this);
            }
        }

        private void OnValidate() {
            CacheComponentReferences();
            if (participant != null) {
                participant.SetConversationGroup(this);
            }
            startingGroup = Mathf.Max(ConversationMembership.EVERYONE, startingGroup);
        }

        public override void OnNetworkSpawn() {
            membership.OnValueChanged += HandleMembershipChanged;
            if (HasAuthority) {
                TrySetGroup(startingGroup);
            }
        }

        public override void OnNetworkDespawn() {
            membership.OnValueChanged -= HandleMembershipChanged;
        }

        public bool TrySetGroup(int group) {
            if (!CanChangeGroup || group < ConversationMembership.EVERYONE) { return false; }

            ConversationMembership currentMembership = membership.Value;
            if (currentMembership.Group == group) { return true; }
            membership.Value = currentMembership.WithGroup(group);
            return true;
        }

        private void HandleMembershipChanged(ConversationMembership previousMembership, ConversationMembership currentMembership) {
            if (!currentMembership.IsValid || previousMembership == currentMembership) { return; }

            ClearAffectedPlayback();
            OnMembershipChanged.Dispatch(currentMembership);
        }

        private void ClearAffectedPlayback() {
            if (speakerOutput != null) {
                speakerOutput.ClearPlayback();
            }
            if (!IsClient || !IsOwner || NetworkManager == null || NetworkManager.SpawnManager == null) { return; }

            IReadOnlyList<NetworkObject> playerObjects = NetworkManager.SpawnManager.PlayerObjects;
            for (int i = 0; i < playerObjects.Count; i++) {
                if (playerObjects[i].TryGetComponent(out SpeakerOutput playerSpeaker)) {
                    playerSpeaker.ClearPlayback();
                }
            }
        }

        private void CacheComponentReferences() {
            if (participant == null) {
                TryGetComponent(out participant);
            }
            if (speakerOutput == null) {
                TryGetComponent(out speakerOutput);
            }
        }

    }

}
