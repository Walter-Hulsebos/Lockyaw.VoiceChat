using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Broadcast Button")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class BroadcastButton : NetworkBehaviour {

        public readonly Signal<BroadcastControlState> OnStateChanged = new();

        public NetworkVariable<BroadcastControlState> State => state;
        public bool IsBroadcasting => state.Value.IsBroadcasting;
        public ulong OperatorClientId => state.Value.OperatorClientId;
        public bool CanRequestBroadcast => IsSpawned && IsClient && isActiveAndEnabled;
        public bool IsLocalOperator => NetworkManager != null && state.Value.IsOperatedBy(NetworkManager.LocalClientId);

        private readonly NetworkVariable<BroadcastControlState> state = new(
            BroadcastControlState.Idle,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public override void OnDestroy() {
            state.OnValueChanged -= HandleStateChanged;
            StopObservingClientDisconnects();
            base.OnDestroy();
        }

        public override void OnNetworkSpawn() {
            state.OnValueChanged += HandleStateChanged;
            if (NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;
            }
            ReleaseDisconnectedOperator();
            OnStateChanged.Dispatch(state.Value);
        }

        public override void OnNetworkDespawn() {
            state.OnValueChanged -= HandleStateChanged;
            StopObservingClientDisconnects();
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            ReleaseDisconnectedOperator();
        }

        public void BeginBroadcast() {
            if (!CanRequestBroadcast) { return; }
            RequestBroadcastState(true);
        }

        public void EndBroadcast() {
            if (!CanRequestBroadcast) { return; }
            RequestBroadcastState(false);
        }

        [Rpc(SendTo.Authority, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Everyone)]
        private void SetBroadcastingRpc(bool shouldBroadcast, RpcParams rpcParams = default) {
            SetBroadcastingAtAuthority(shouldBroadcast, rpcParams.Receive.SenderClientId);
        }

        private void RequestBroadcastState(bool shouldBroadcast) {
            if (HasAuthority) {
                SetBroadcastingAtAuthority(shouldBroadcast, NetworkManager.LocalClientId);
            }
            else {
                SetBroadcastingRpc(shouldBroadcast);
            }
        }

        private void SetBroadcastingAtAuthority(bool shouldBroadcast, ulong requestingClientId) {
            if (!CanWriteState()) { return; }

            BroadcastControlState currentState = state.Value;
            BroadcastControlState requestedState = shouldBroadcast
                ? currentState.WithBroadcastStarted(requestingClientId)
                : currentState.WithBroadcastEnded(requestingClientId);
            SetState(requestedState);
        }

        private void HandleStateChanged(BroadcastControlState previousState, BroadcastControlState currentState) {
            if (!currentState.IsValid || previousState == currentState) { return; }
            OnStateChanged.Dispatch(currentState);
        }

        private void HandleClientDisconnected(ulong clientId) {
            if (!CanWriteState()) { return; }
            SetState(state.Value.WithBroadcastEnded(clientId));
        }

        private void ReleaseDisconnectedOperator() {
            if (!CanWriteState() || !state.Value.IsBroadcasting || IsClientConnected(state.Value.OperatorClientId)) { return; }
            SetState(state.Value.WithBroadcastEnded(state.Value.OperatorClientId));
        }

        private void SetState(BroadcastControlState requestedState) {
            if (!requestedState.IsValid || state.Value == requestedState) { return; }
            state.Value = requestedState;
        }

        private void StopObservingClientDisconnects() {
            if (NetworkManager == null) { return; }
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
        }

        private bool CanWriteState() {
            return IsSpawned &&
                   HasAuthority &&
                   isActiveAndEnabled &&
                   NetworkManager != null &&
                   state.CanClientWrite(NetworkManager.LocalClientId);
        }

        private bool IsClientConnected(ulong clientId) {
            if (NetworkManager == null) { return false; }

            IReadOnlyList<ulong> connectedClientIds = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < connectedClientIds.Count; i++) {
                if (connectedClientIds[i] == clientId) { return true; }
            }
            return false;
        }

    }

}
