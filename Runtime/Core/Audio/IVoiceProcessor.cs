using System;

namespace Lockyaw.VoiceChat {

    internal interface IVoiceProcessor : IDisposable {

        void Process(double currentTime);

    }

}
