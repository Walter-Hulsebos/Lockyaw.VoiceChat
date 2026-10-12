using Netcode.Transports;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    public sealed class SteamVoiceProvider : IVoiceCarrierProvider {

        public VoiceDeliveryMode Mode => VoiceDeliveryMode.SteamIndependent;

        public IVoiceIndependentCarrier Create(VoiceCarrierConfiguration configuration) => new SteamVoiceCarrier(configuration);

        public bool CanUse(NetworkManager networkManager) => networkManager != null && networkManager.NetworkConfig.NetworkTransport is SteamNetworkingSocketsTransport;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() {
            VoiceCarrierProviders.Register(new SteamVoiceProvider());
        }

    }

}
