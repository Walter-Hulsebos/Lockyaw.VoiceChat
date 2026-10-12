using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Lockyaw.VoiceChat.UIToolkit {

    [UxmlElement]
    public sealed partial class MicrophoneDropdownElement : DropdownField {

        private readonly MicrophoneDropdownOptions options = new();
        private MicrophoneInput microphoneInput;
        private IVisualElementScheduledItem refreshSchedule;
        private bool isRefreshing;
        private bool hasExplicitBinding;

        public MicrophoneDropdownElement() {
            label = "Microphone";
            SetEnabled(false);
            RegisterCallback<AttachToPanelEvent>(HandleAttachedToPanel);
            RegisterCallback<DetachFromPanelEvent>(HandleDetachedFromPanel);
            RegisterCallback<ChangeEvent<string>>(HandleDeviceChanged);
        }

        public void Bind(MicrophoneInput microphoneInput) {
            this.microphoneInput = microphoneInput;
            hasExplicitBinding = microphoneInput != null;
            RefreshDevices();
        }

        public void RefreshDevices() {
            ResolveMicrophone();
            MicrophoneDevices.Refresh();
            VoiceDeviceId requestedDevice = microphoneInput == null ? VoiceDeviceId.Default : microphoneInput.DeviceId;
            bool haveOptionsChanged = options.Refresh(MicrophoneDevices.GetAvailableDevices(), requestedDevice);
            SetEnabled(microphoneInput != null && (!microphoneInput.IsSpawned || microphoneInput.IsOwner));
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
            if (isRefreshing || microphoneInput == null || !enabledInHierarchy || !options.TryGetDeviceId(index, out VoiceDeviceId deviceId)) { return; }
            microphoneInput.SetInputDevice(deviceId);
            RefreshDevices();
        }

        private void ResolveMicrophone() {
            if (hasExplicitBinding) { return; }
            if (microphoneInput != null && microphoneInput.IsSpawned && microphoneInput.IsClient && microphoneInput.IsOwner && microphoneInput.isActiveAndEnabled) { return; }
            microphoneInput = null;
            MicrophoneInput candidate = null;
            IReadOnlyList<VoiceParticipant> participants = VoiceParticipant.SpawnedParticipants;
            for (int i = 0; i < participants.Count; i++) {
                VoiceParticipant participant = participants[i];
                if (participant == null || !participant.IsSpawned || !participant.IsClient || !participant.IsOwner || !participant.TryGetComponent(out MicrophoneInput localMicrophone)) { continue; }
                if (candidate != null) { return; }
                candidate = localMicrophone;
            }
            microphoneInput = candidate;
        }

    }

}
