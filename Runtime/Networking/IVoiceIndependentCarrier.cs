namespace Lockyaw.VoiceChat {

    public interface IVoiceIndependentCarrier : IVoicePacketCarrier {

        ushort BoundPort { get; }
        string AdvertisedAddress { get; }
        void BindPeer(ulong peerId, byte[] secret);
        void RemovePeer(ulong peerId);
        void PumpMainThread();

    }

}
