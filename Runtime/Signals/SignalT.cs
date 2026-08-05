using System;

namespace Lockyaw.VoiceChat {

    public sealed class Signal<TValue> {

        private event Action<TValue> listeners;

        public void Listen(Action<TValue> listener) {
            if (listener == null) { return; }
            listeners += listener;
        }

        public void Unlisten(Action<TValue> listener) {
            if (listener == null) { return; }
            listeners -= listener;
        }

        public void Dispatch(TValue value) {
            Action<TValue> listenersSnapshot = listeners;
            if (listenersSnapshot == null) { return; }
            listenersSnapshot.Invoke(value);
        }

        public void Clear() {
            listeners = null;
        }

    }

}
