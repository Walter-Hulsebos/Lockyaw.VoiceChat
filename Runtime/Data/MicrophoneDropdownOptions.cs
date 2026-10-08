using System;
using System.Collections.Generic;

namespace Lockyaw.VoiceChat {

    public sealed class MicrophoneDropdownOptions {

        public IReadOnlyList<string> Labels => labels;
        public int SelectedIndex { get; private set; }
        public bool HasDevices => availableDevices.Count > 0;

        private readonly List<string> labels = new();
        private readonly List<string> availableDevices = new();
        private readonly List<string> optionDeviceNames = new();
        private string requestedDeviceName;
        private int unavailableDeviceIndex = -1;
        private bool hasRefreshed;

        public bool Refresh(IReadOnlyList<string> deviceNames, string requestedDeviceName) {
            string normalizedDeviceName = string.IsNullOrWhiteSpace(requestedDeviceName) ? "" : requestedDeviceName;
            IReadOnlyList<string> refreshedDevices = deviceNames == null ? Array.Empty<string>() : deviceNames;
            if (hasRefreshed && this.requestedDeviceName == normalizedDeviceName && HasSameDevices(refreshedDevices)) { return false; }

            hasRefreshed = true;
            this.requestedDeviceName = normalizedDeviceName;
            availableDevices.Clear();
            availableDevices.AddRange(refreshedDevices);
            labels.Clear();
            optionDeviceNames.Clear();
            SelectedIndex = 0;
            unavailableDeviceIndex = -1;
            if (!HasDevices) {
                labels.Add("No microphone available");
                return true;
            }

            labels.Add("Automatic");
            optionDeviceNames.Add("");
            if (normalizedDeviceName.Length > 0 && !availableDevices.Contains(normalizedDeviceName)) {
                unavailableDeviceIndex = labels.Count;
                SelectedIndex = unavailableDeviceIndex;
                labels.Add($"{normalizedDeviceName} (unavailable)");
                optionDeviceNames.Add(normalizedDeviceName);
            }

            for (int i = 0; i < availableDevices.Count; i++) {
                string deviceName = availableDevices[i];
                if (deviceName == normalizedDeviceName) {
                    SelectedIndex = labels.Count;
                }
                labels.Add(deviceName);
                optionDeviceNames.Add(deviceName);
            }
            return true;
        }

        public bool TryGetDeviceName(int optionIndex, out string deviceName) {
            deviceName = "";
            if (!HasDevices || optionIndex < 0 || optionIndex >= optionDeviceNames.Count || optionIndex == unavailableDeviceIndex) { return false; }
            deviceName = optionDeviceNames[optionIndex];
            return true;
        }

        private bool HasSameDevices(IReadOnlyList<string> deviceNames) {
            if (availableDevices.Count != deviceNames.Count) { return false; }
            for (int i = 0; i < availableDevices.Count; i++) {
                if (availableDevices[i] != deviceNames[i]) { return false; }
            }
            return true;
        }

    }

}
