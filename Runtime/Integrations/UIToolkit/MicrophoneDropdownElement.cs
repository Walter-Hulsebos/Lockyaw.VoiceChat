using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Lockyaw.VoiceChat.UIToolkit {

    [UxmlElement]
    public sealed partial class MicrophoneDropdownElement : DropdownField {

        private readonly MicrophoneDropdownOptions options = new();
        private MicrophoneInput microphoneInput;
        private IVisualElementScheduledItem refreshSchedule;
        private bool isRefreshing;

        public MicrophoneDropdownElement() {
            label = "Microphone";
            SetEnabled(false);
            RegisterCallback<AttachToPanelEvent>(HandleAttachedToPanel);
            RegisterCallback<DetachFromPanelEvent>(HandleDetachedFromPanel);
            RegisterCallback<ChangeEvent<string>>(HandleDeviceChanged);
        }

        public void Bind(MicrophoneInput microphoneInput) {
            this.microphoneInput = microphoneInput;
            RefreshDevices();
        }

        public void RefreshDevices() {
            ResolveMicrophone();
            string requestedDeviceName = microphoneInput == null ? "" : microphoneInput.RequestedDeviceName;
            bool haveOptionsChanged = options.Refresh(MicrophoneDevices.GetAvailableDevices(), requestedDeviceName);
            SetEnabled(options.HasDevices && microphoneInput != null && (!microphoneInput.IsSpawned || microphoneInput.IsOwner));
            if (!haveOptionsChanged) { return; }

            isRefreshing = true;
            try {
                choices = new List<string>(options.Labels);
                SetValueWithoutNotify(choices[options.SelectedIndex]);
            }
            finally {
                isRefreshing = false;
            }
        }

        private void HandleAttachedToPanel(AttachToPanelEvent e) {
            RefreshDevices();
            refreshSchedule = schedule.Execute(RefreshDevices).Every(1000);
        }

        private void HandleDetachedFromPanel(DetachFromPanelEvent e) {
            if (refreshSchedule == null) { return; }
            refreshSchedule.Pause();
            refreshSchedule = null;
        }

        private void HandleDeviceChanged(ChangeEvent<string> e) {
            if (isRefreshing || microphoneInput == null || !enabledInHierarchy || !options.TryGetDeviceName(index, out string deviceName)) { return; }
            microphoneInput.SetDevice(deviceName);
            RefreshDevices();
        }

        private void ResolveMicrophone() {
            if (microphoneInput == null && VoiceParticipant.TryGetLocal(out VoiceParticipant participant)) {
                participant.TryGetComponent(out microphoneInput);
            }
        }

    }

}
