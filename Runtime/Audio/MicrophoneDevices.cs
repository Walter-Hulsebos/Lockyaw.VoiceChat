using System;
using System.Collections.Generic;

namespace Lockyaw.VoiceChat {

    public static class MicrophoneDevices {

        public static string[] GetAvailableDevices() => GetAvailableDevices(UnityMicrophoneApi.Instance);

        internal static string[] GetAvailableDevices(IMicrophoneApi microphoneApi) {
            try {
                string[] deviceNames = microphoneApi.GetDevices();
                if (deviceNames == null || deviceNames.Length == 0) { return Array.Empty<string>(); }

                List<string> availableDevices = new(deviceNames.Length);
                for (int i = 0; i < deviceNames.Length; i++) {
                    string deviceName = deviceNames[i];
                    if (string.IsNullOrWhiteSpace(deviceName) || availableDevices.Contains(deviceName)) { continue; }
                    availableDevices.Add(deviceName);
                }
                return availableDevices.ToArray();
            }
            catch (Exception) {
                return Array.Empty<string>();
            }
        }

        internal static bool ContainsDevice(IMicrophoneApi microphoneApi, string deviceName) {
            if (string.IsNullOrWhiteSpace(deviceName)) { return false; }
            return Array.IndexOf(GetAvailableDevices(microphoneApi), deviceName) >= 0;
        }

        internal static string ResolveDeviceName(string requestedDeviceName, IReadOnlyList<string> availableDevices) {
            if (availableDevices == null || availableDevices.Count == 0) { return ""; }
            if (string.IsNullOrWhiteSpace(requestedDeviceName)) { return availableDevices[0]; }

            for (int i = 0; i < availableDevices.Count; i++) {
                if (availableDevices[i] == requestedDeviceName) { return requestedDeviceName; }
            }
            return "";
        }

    }

}
