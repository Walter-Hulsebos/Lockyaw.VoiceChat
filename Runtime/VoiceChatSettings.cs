using UnityEngine;

namespace Lockyaw.VoiceChat {

    public static class VoiceChatSettings {

        public static readonly Signal<bool> OnSendingEnabled = new();
        public static readonly Signal<bool> OnReceivingEnabled = new();
        public static bool IsSendingEnabled { get; private set; } = true;
        public static bool IsReceivingEnabled { get; private set; } = true;

        public static void SetSendingEnabled(bool isEnabled) {
            if (IsSendingEnabled == isEnabled) { return; }
            IsSendingEnabled = isEnabled;
            OnSendingEnabled.Dispatch(isEnabled);
        }

        public static void SetReceivingEnabled(bool isEnabled) {
            if (IsReceivingEnabled == isEnabled) { return; }
            IsReceivingEnabled = isEnabled;
            OnReceivingEnabled.Dispatch(isEnabled);
        }

        public static void SetAudioEnabled(bool isEnabled) {
            SetSendingEnabled(isEnabled);
            SetReceivingEnabled(isEnabled);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            IsSendingEnabled = true;
            IsReceivingEnabled = true;
            OnSendingEnabled.Clear();
            OnReceivingEnabled.Clear();
        }

    }

}
