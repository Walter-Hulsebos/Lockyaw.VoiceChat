#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    public static class VoiceCarrierProviders {

        private static readonly List<IVoiceCarrierProvider> providers = new();

        public static void Register(IVoiceCarrierProvider provider) {
            if (provider == null) { throw new ArgumentNullException(nameof(provider)); }
            for (int i = 0; i < providers.Count; i++) {
                if (providers[i].Mode == provider.Mode) { providers[i] = provider; return; }
            }
            providers.Add(provider);
        }

        public static bool TryGet(VoiceDeliveryMode mode, NetworkManager networkManager, out IVoiceCarrierProvider provider) {
            provider = null;
            for (int i = 0; i < providers.Count; i++) {
                IVoiceCarrierProvider candidate = providers[i];
                if (mode != VoiceDeliveryMode.Automatic && candidate.Mode != mode || !candidate.CanUse(networkManager)) { continue; }
                if (provider != null) { provider = null; return false; }
                provider = candidate;
            }
            return provider != null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            providers.Clear();
        }

    }

}
