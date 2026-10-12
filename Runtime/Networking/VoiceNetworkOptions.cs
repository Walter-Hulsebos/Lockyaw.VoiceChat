namespace Lockyaw.VoiceChat {

    public sealed class VoiceNetworkOptions {

        public VoiceDeliveryMode DeliveryMode = VoiceDeliveryMode.Automatic;
        public string ListenAddress = "0.0.0.0";
        public string AdvertisedAddress = string.Empty;
        public ushort Port = 7778;
        public ushort SteamVirtualPort = 37;
        public int MaxPeers = 128;

    }

}
