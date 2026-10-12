using System;

namespace Lockyaw.VoiceChat {

    // Optional carrier capability. A carrier that reports arrivals is serviced on demand instead of being polled.
    public interface IVoicePacketArrivalNotifier {

        // The callback may run on any thread and must only be invoked after the packet can be returned by TryReceive or Service.
        void SetArrivalCallback(Action callback);

    }

}
