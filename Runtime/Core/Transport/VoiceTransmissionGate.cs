using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceTransmissionGate {

        internal bool IsEnabled => Volatile.Read(ref enabled) != 0;

        private int enabled = 1;

        internal void Cancel() {
            Interlocked.Exchange(ref enabled, 0);
        }

    }

}
