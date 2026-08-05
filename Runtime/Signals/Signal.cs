using System;

namespace Lockyaw.VoiceChat {

    public sealed class Signal {

        private event Action listeners;

        public void Listen(Action listener) {
            if (listener == null) { return; }
            listeners += listener;
        }

        public void Unlisten(Action listener) {
            if (listener == null) { return; }
            listeners -= listener;
        }

        public void Dispatch() {
            Action listenersSnapshot = listeners;
            if (listenersSnapshot == null) { return; }
            listenersSnapshot.Invoke();
        }

        public void Clear() {
            listeners = null;
        }

    }

}
