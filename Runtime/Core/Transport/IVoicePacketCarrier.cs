using System;

namespace Lockyaw.VoiceChat {

    public interface IVoicePacketCarrier : IDisposable {

        VoiceTransportCapability Capability { get; }
        VoiceTransportState State { get; }
        string FailureReason { get; }
        bool TrySend(ulong peerId, VoicePacket packet);
        bool TryReceive(out ulong peerId, out VoicePacket packet);
        void Service();

    }

}
