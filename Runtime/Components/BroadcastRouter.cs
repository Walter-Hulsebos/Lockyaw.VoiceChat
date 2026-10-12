#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    [AddComponentMenu("Lockyaw/Voice Chat/Broadcasting/Broadcast Router")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    public sealed class BroadcastRouter : NetworkBehaviour {

        private static readonly List<BroadcastRouter> activeRouters = new(4);

        public override void OnDestroy() {
            activeRouters.Remove(this);
            base.OnDestroy();
        }

        private void OnEnable() {
            if (IsSpawned) { RegisterRouter(); }
        }

        private void OnDisable() {
            activeRouters.Remove(this);
        }

        private void Reset() {
            if (TryGetComponent(out NetworkObject networkObject)) { networkObject.SpawnWithObservers = true; }
        }

        public override void OnNetworkSpawn() {
            VoiceSession.GetOrCreate(NetworkManager);
            if (isActiveAndEnabled) { RegisterRouter(); }
        }

        public override void OnNetworkDespawn() {
            activeRouters.Remove(this);
        }

        internal static bool TryGet(NetworkManager manager, out BroadcastRouter router) {
            router = null;
            for (int i = 0; i < activeRouters.Count; i++) {
                BroadcastRouter candidate = activeRouters[i];
                if (candidate == null || !candidate.IsSpawned || !candidate.isActiveAndEnabled || candidate.NetworkManager != manager) { continue; }
                if (router == null || candidate.NetworkObjectId < router.NetworkObjectId) { router = candidate; }
            }
            return router != null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            activeRouters.Clear();
        }

        private void RegisterRouter() {
            if (!activeRouters.Contains(this)) { activeRouters.Add(this); }
        }

    }

}
