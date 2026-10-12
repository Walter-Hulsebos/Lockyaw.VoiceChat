using System;
using System.Collections.Generic;

namespace Lockyaw.VoiceChat {

    public sealed class MicrophoneDropdownOptions {

        public IReadOnlyList<string> Labels => labels;
        public int SelectedIndex { get; private set; }
        public bool HasDevices => availableDevices.Count > 0;

        private readonly List<VoiceDeviceDescriptor> availableDevices = new();
        private readonly List<string> labels = new();
        private readonly List<VoiceDeviceId> optionDevices = new();
        private VoiceDeviceId requestedDevice;
        private int unavailableDeviceIndex = -1;
        private bool hasRefreshed;

        public bool Refresh(IReadOnlyList<VoiceDeviceDescriptor> deviceDescriptors, VoiceDeviceId requestedDevice) {
            IReadOnlyList<VoiceDeviceDescriptor> refreshedDevices = deviceDescriptors == null ? Array.Empty<VoiceDeviceDescriptor>() : deviceDescriptors;
            if (hasRefreshed && this.requestedDevice == requestedDevice && HasSameDevices(refreshedDevices)) { return false; }
            hasRefreshed = true;
            this.requestedDevice = requestedDevice;
            availableDevices.Clear();
            for (int i = 0; i < refreshedDevices.Count; i++) { availableDevices.Add(refreshedDevices[i]); }
            labels.Clear();
            optionDevices.Clear();
            unavailableDeviceIndex = -1;
            SelectedIndex = requestedDevice.IsNone ? 1 : 0;
            labels.Add(HasDevices ? "System default" : "System default (no microphone available)");
            optionDevices.Add(VoiceDeviceId.Default);
            labels.Add("None");
            optionDevices.Add(VoiceDeviceId.None);
            for (int i = 0; i < availableDevices.Count; i++) {
                VoiceDeviceDescriptor device = availableDevices[i];
                string label = device.DisplayName;
                int duplicateCount = 0;
                int duplicateIndex = 0;
                for (int otherIndex = 0; otherIndex < availableDevices.Count; otherIndex++) {
                    if (availableDevices[otherIndex].DisplayName != device.DisplayName) { continue; }
                    duplicateCount++;
                    if (otherIndex <= i) { duplicateIndex++; }
                }
                if (duplicateCount > 1) { label = $"{label} ({duplicateIndex})"; }
                if (device.IsDefault) { label = $"{label} (default)"; }
                if (device.Id == requestedDevice) { SelectedIndex = labels.Count; }
                labels.Add(label);
                optionDevices.Add(device.Id);
            }
            if (!requestedDevice.IsDefault && !requestedDevice.IsNone && SelectedIndex == 0) {
                unavailableDeviceIndex = labels.Count;
                SelectedIndex = unavailableDeviceIndex;
                labels.Add("Selected microphone (unavailable)");
                optionDevices.Add(requestedDevice);
            }
            return true;
        }

        public bool TryGetDeviceId(int optionIndex, out VoiceDeviceId deviceId) {
            deviceId = VoiceDeviceId.Default;
            if (optionIndex < 0 || optionIndex >= optionDevices.Count || optionIndex == unavailableDeviceIndex) { return false; }
            deviceId = optionDevices[optionIndex];
            return true;
        }

        private bool HasSameDevices(IReadOnlyList<VoiceDeviceDescriptor> descriptors) {
            if (availableDevices.Count != descriptors.Count) { return false; }
            for (int i = 0; i < availableDevices.Count; i++) {
                VoiceDeviceDescriptor previousDevice = availableDevices[i];
                VoiceDeviceDescriptor currentDevice = descriptors[i];
                if (previousDevice.Id != currentDevice.Id || previousDevice.DisplayName != currentDevice.DisplayName || previousDevice.IsDefault != currentDevice.IsDefault) { return false; }
            }
            return true;
        }

    }

}
