using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Broadcast Router")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class BroadcastRouter : NetworkBehaviour {

        private sealed class RelayFrameState {

            internal double LastFrameAt => lastFrameAt;

            private FrameReplayWindow replayWindow;
            private double windowStartedAt;
            private double lastFrameAt;
            private int framesInWindow;

            internal bool TryAccept(VoiceFrame frame, double currentTime) {
                bool allowForwardRebase = currentTime - lastFrameAt >= AUTHORITY_STREAM_TIMEOUT;
                if (currentTime - windowStartedAt >= 1d) {
                    windowStartedAt = currentTime;
                    framesInWindow = 0;
                }

                framesInWindow++;
                if (framesInWindow > MAXIMUM_FRAMES_PER_SECOND) { return false; }
                if (!replayWindow.TryAccept(
                    frame.SourceClientId,
                    frame.StreamId,
                    frame.Sequence,
                    MAXIMUM_SEQUENCE_ADVANCE,
                    allowForwardRebase)) {
                    return false;
                }

                lastFrameAt = currentTime;
                return true;
            }

        }

        private static readonly List<BroadcastRouter> activeRouters = new(4);

        private const double AUTHORITY_STREAM_TIMEOUT = 2d;
        private const int MAXIMUM_FRAMES_PER_SECOND = 110;
        private const int MAXIMUM_RELAY_SOURCES = 256;
        private const uint MAXIMUM_SEQUENCE_ADVANCE = 1024;

        private readonly Dictionary<BroadcastRoute, RelayFrameState> relayFrameStates = new(32);
        private NativeList<ulong> relayTargets;

        private void Awake() {
            relayTargets = new(16, Allocator.Persistent);
        }

        public override void OnDestroy() {
            UnregisterRouter();
            relayFrameStates.Clear();
            if (relayTargets.IsCreated) {
                relayTargets.Dispose();
            }
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) {
                RegisterRouter();
            }
        }

        private void OnDisable() {
            UnregisterRouter();
        }

        private void Reset() {
            if (TryGetComponent(out NetworkObject networkObject)) {
                networkObject.SpawnWithObservers = true;
            }
        }

        public override void OnNetworkSpawn() {
            if (isActiveAndEnabled) {
                RegisterRouter();
            }
        }

        public override void OnNetworkDespawn() {
            UnregisterRouter();
            relayFrameStates.Clear();
            if (relayTargets.IsCreated) {
                relayTargets.Clear();
            }
        }

        internal void SubmitFrame(BroadcastFrame frame) {
            if (!IsSpawned || !isActiveAndEnabled || !frame.IsValid) { return; }

            if (HasAuthority) {
                if (CanAcceptFrame(frame, NetworkManager.LocalClientId) && TryAcceptFrameAtAuthority(frame)) {
                    RelayFrame(frame);
                }
                return;
            }
            SubmitFrameRpc(frame);
        }

        internal static bool TryGet(NetworkManager networkManager, out BroadcastRouter router) {
            router = null;
            if (networkManager == null || networkManager.SpawnManager == null) { return false; }

            for (int i = 0; i < activeRouters.Count; i++) {
                BroadcastRouter candidate = activeRouters[i];
                if (!IsRegisteredWithNetworkManager(candidate, networkManager) ||
                    !candidate.IsSpawned ||
                    !candidate.isActiveAndEnabled) {
                    continue;
                }
                if (router == null || candidate.NetworkObjectId < router.NetworkObjectId) {
                    router = candidate;
                }
            }
            return router != null;
        }

        protected override void OnOwnershipChanged(ulong previousOwnerClientId, ulong currentOwnerClientId) {
            relayFrameStates.Clear();
            base.OnOwnershipChanged(previousOwnerClientId, currentOwnerClientId);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            activeRouters.Clear();
        }

        [Rpc(SendTo.Authority, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
        private void SubmitFrameRpc(BroadcastFrame frame, RpcParams rpcParams = default) {
            if (!HasAuthority ||
                !CanAcceptFrame(frame, rpcParams.Receive.SenderClientId) ||
                !TryAcceptFrameAtAuthority(frame)) {
                return;
            }

            RelayFrame(frame);
        }

        private void RelayFrame(BroadcastFrame frame) {
            BuildRelayTargets(frame.Route.Channel);
            if (relayTargets.Length == 0) { return; }

            RpcParams relayParams = default;
            relayParams.Send.Target = RpcTarget.Group(relayTargets, RpcTargetUse.Temp);
            if (NetworkManager.DistributedAuthorityMode) {
                RelayDistributedAuthorityFrameRpc(frame, relayParams);
            }
            else {
                RelayClientServerFrameRpc(frame, relayParams);
            }
        }

        [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Server)]
        private void RelayClientServerFrameRpc(BroadcastFrame frame, RpcParams rpcParams = default) {
            if (!IsClient || NetworkManager.DistributedAuthorityMode || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) { return; }
            ChannelSpeaker.Dispatch(NetworkManager, frame);
        }

        [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        private void RelayDistributedAuthorityFrameRpc(BroadcastFrame frame, RpcParams rpcParams = default) {
            if (!IsClient || !NetworkManager.DistributedAuthorityMode || rpcParams.Receive.SenderClientId != OwnerClientId) { return; }
            ChannelSpeaker.Dispatch(NetworkManager, frame);
        }

        private void BuildRelayTargets(int channel) {
            relayTargets.Clear();
            ChannelSpeaker.CollectListenerClientIds(NetworkManager, channel, relayTargets);
            for (int i = relayTargets.Length - 1; i >= 0; i--) {
                ulong clientId = relayTargets[i];
                if (!NetworkObject.IsNetworkVisibleTo(clientId) ||
                    NetworkManager.CMBServiceConnection && clientId == NetworkManager.ServerClientId ||
                    clientId == NetworkManager.LocalClientId && !NetworkManager.IsClient) {
                    relayTargets.RemoveAtSwapBack(i);
                }
            }
        }

        private void RegisterRouter() {
            if (!activeRouters.Contains(this)) {
                activeRouters.Add(this);
            }
        }

        private void UnregisterRouter() {
            activeRouters.Remove(this);
        }

        private bool CanAcceptFrame(BroadcastFrame frame, ulong senderClientId) {
            if (!frame.IsValid ||
                !frame.Route.IsDirect ||
                NetworkManager == null ||
                NetworkManager.SpawnManager == null) {
                return false;
            }
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(frame.Route.SourceNetworkObjectId, out NetworkObject sourceObject)) {
                return false;
            }

            if (!sourceObject.TryGetComponent(out AreaMicrophone sourceMicrophone) ||
                sourceMicrophone.Channel != frame.Route.Channel ||
                !sourceMicrophone.IsOpen) {
                return false;
            }
            bool hasValidSender = NetworkManager.DistributedAuthorityMode
                ? senderClientId == sourceObject.OwnerClientId
                : senderClientId == NetworkManager.ServerClientId;
            if (!hasValidSender) { return false; }

            if (frame.Route.OriginNetworkObjectId == frame.Route.SourceNetworkObjectId) {
                return !frame.Route.HasOriginClient &&
                       sourceMicrophone.CapturesWorldAudio &&
                       frame.Frame.SourceClientId == sourceMicrophone.CaptureClientId;
            }
            if (!frame.Route.HasOriginClient || frame.Frame.SourceClientId != frame.Route.OriginClientId) { return false; }
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(frame.Route.OriginNetworkObjectId, out NetworkObject originObject)) {
                return false;
            }
            if (!originObject.TryGetComponent(out VoiceParticipant participant)) { return false; }
            return participant.OwnerClientId == frame.Route.OriginClientId &&
                   sourceMicrophone.ContainsPosition(participant.transform.position) &&
                   sourceMicrophone.CanCaptureVoiceFrom(frame.Route.OriginClientId);
        }

        private bool TryAcceptFrameAtAuthority(BroadcastFrame frame) {
            double currentTime = Time.unscaledTimeAsDouble;
            if (relayFrameStates.TryGetValue(frame.Route, out RelayFrameState relayFrameState)) {
                return relayFrameState.TryAccept(frame.Frame, currentTime);
            }

            relayFrameState = new();
            if (!relayFrameState.TryAccept(frame.Frame, currentTime)) { return false; }
            if (relayFrameStates.Count >= MAXIMUM_RELAY_SOURCES && !TryRemoveOldestRelayFrameState()) { return false; }
            relayFrameStates.Add(frame.Route, relayFrameState);
            return true;
        }

        private bool TryRemoveOldestRelayFrameState() {
            BroadcastRoute oldestRoute = default;
            RelayFrameState oldestState = null;
            foreach (KeyValuePair<BroadcastRoute, RelayFrameState> pair in relayFrameStates) {
                if (oldestState != null && pair.Value.LastFrameAt >= oldestState.LastFrameAt) { continue; }
                oldestRoute = pair.Key;
                oldestState = pair.Value;
            }
            return oldestState != null && relayFrameStates.Remove(oldestRoute);
        }

        private static bool IsRegisteredWithNetworkManager(BroadcastRouter router, NetworkManager networkManager) {
            return router != null &&
                   router.NetworkObject != null &&
                   networkManager != null &&
                   networkManager.SpawnManager != null &&
                   networkManager.SpawnManager.SpawnedObjects.TryGetValue(router.NetworkObjectId, out NetworkObject networkObject) &&
                   networkObject == router.NetworkObject;
        }

    }

}
