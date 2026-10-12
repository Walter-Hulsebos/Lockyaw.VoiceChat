using Netcode.Transports.WebSocket;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    public sealed class WebSocketVoiceProvider : IVoiceCarrierProvider {

        public VoiceDeliveryMode Mode => VoiceDeliveryMode.WebSocketIndependent;

        public IVoiceIndependentCarrier Create(VoiceCarrierConfiguration configuration) => new WebSocketVoiceCarrier(configuration);

        public bool CanUse(NetworkManager networkManager) => networkManager != null && networkManager.NetworkConfig.NetworkTransport is WebSocketTransport;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() {
            VoiceCarrierProviders.Register(new WebSocketVoiceProvider());
        }

    }

}
