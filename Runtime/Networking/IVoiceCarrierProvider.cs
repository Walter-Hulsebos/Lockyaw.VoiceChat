using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public interface IVoiceCarrierProvider {

        VoiceDeliveryMode Mode { get; }
        bool CanUse(NetworkManager networkManager);
        IVoiceIndependentCarrier Create(VoiceCarrierConfiguration configuration);

    }

}
