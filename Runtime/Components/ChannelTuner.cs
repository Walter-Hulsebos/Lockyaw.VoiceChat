using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Channel Tuner")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class ChannelTuner : NetworkBehaviour {

        [Header("Channel")]
        [SerializeField, Min(DEFAULT_CHANNEL)] private int startingChannel = DEFAULT_CHANNEL;

        public readonly Signal<int> OnChannelChanged = new();

        public const int DEFAULT_CHANNEL = 0;

        public NetworkVariable<int> Channel => channel;
        public int CurrentChannel => channel.Value;
        public bool CanTune => IsSpawned && IsOwner && isActiveAndEnabled;

        private readonly NetworkVariable<int> channel = new(
            DEFAULT_CHANNEL,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public override void OnDestroy() {
            channel.OnValueChanged -= HandleChannelChanged;
            base.OnDestroy();
        }

        private void Reset() {
            startingChannel = DEFAULT_CHANNEL;
        }

        private void OnValidate() {
            startingChannel = Mathf.Max(DEFAULT_CHANNEL, startingChannel);
        }

        public override void OnNetworkSpawn() {
            if (IsOwner && startingChannel >= DEFAULT_CHANNEL) {
                channel.Value = startingChannel;
            }
            channel.OnValueChanged += HandleChannelChanged;
            OnChannelChanged.Dispatch(channel.Value);
        }

        public override void OnNetworkDespawn() {
            channel.OnValueChanged -= HandleChannelChanged;
        }

        public bool TrySetChannel(int requestedChannel) {
            if (!CanTune || requestedChannel < DEFAULT_CHANNEL) { return false; }
            if (channel.Value == requestedChannel) { return true; }

            channel.Value = requestedChannel;
            return true;
        }

        private void HandleChannelChanged(int previousChannel, int currentChannel) {
            if (currentChannel < DEFAULT_CHANNEL || previousChannel == currentChannel) { return; }
            OnChannelChanged.Dispatch(currentChannel);
        }

    }

}
