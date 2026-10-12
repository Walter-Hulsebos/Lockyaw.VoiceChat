using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public sealed class VoiceCarrierConfiguration {

        public readonly NetworkManager NetworkManager;
        public readonly VoiceNetworkOptions Options;
        public readonly ulong LocalPeerId;
        public readonly ulong AuthorityPeerId;
        public readonly ulong Epoch;
        public readonly string RemoteAddress;
        public readonly ushort RemotePort;
        public bool IsAuthority => LocalPeerId == AuthorityPeerId;

        public VoiceCarrierConfiguration(NetworkManager networkManager, VoiceNetworkOptions options, ulong authorityPeerId, ulong epoch, string remoteAddress, ushort remotePort) {
            NetworkManager = networkManager;
            Options = options;
            LocalPeerId = networkManager.LocalClientId;
            AuthorityPeerId = authorityPeerId;
            Epoch = epoch;
            RemoteAddress = remoteAddress;
            RemotePort = remotePort;
        }

    }

}
