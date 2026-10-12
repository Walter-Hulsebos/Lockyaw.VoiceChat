#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System;
using System.Collections.Generic;
using System.Text;

namespace Lockyaw.VoiceChat {

    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    public static class MicrophoneDevices {

        private static readonly object refreshLock = new();

        public static VoiceCaptureState State { get; private set; } = VoiceCaptureState.Disabled;

        private static IReadOnlyList<VoiceDeviceDescriptor> devices = Array.Empty<VoiceDeviceDescriptor>();
        private static bool hasRefreshed;

        public static bool Refresh() {
            lock (refreshLock) {
                VoiceCaptureState previousState = State;
                IReadOnlyList<VoiceDeviceDescriptor> refreshedDevices = Enumerate(out VoiceCaptureState refreshedState);
                State = refreshedState;
                bool changed = !hasRefreshed || State != previousState || !HasSameDevices(refreshedDevices);
                hasRefreshed = true;
                if (changed) { devices = refreshedDevices; }
                return changed;
            }
        }

        public static IReadOnlyList<VoiceDeviceDescriptor> GetAvailableDevices() {
            if (!hasRefreshed) { Refresh(); }
            return devices;
        }

        internal static void ResetCache() {
            lock (refreshLock) {
                devices = Array.Empty<VoiceDeviceDescriptor>();
                State = VoiceCaptureState.Disabled;
                hasRefreshed = false;
            }
        }

        internal static string ReadUtf8(byte[] bytes) {
            int length = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
        }

        private static IReadOnlyList<VoiceDeviceDescriptor> Enumerate(out VoiceCaptureState state) {
            state = VoiceCaptureState.BackendUnavailable;
            if (!NativeCaptureLibrary.TryLoad(NativeCapturePlatform.LibraryPath, out NativeCaptureLibrary library)) { return Array.Empty<VoiceDeviceDescriptor>(); }
            using (library) {
                state = (VoiceCaptureState)library.Create(out IntPtr context);
                if (context == IntPtr.Zero) { return Array.Empty<VoiceDeviceDescriptor>(); }
                try {
                    state = (VoiceCaptureState)library.Refresh(context, out int count);
                    if (count == 0) { return Array.Empty<VoiceDeviceDescriptor>(); }
                    List<VoiceDeviceDescriptor> available = new(Math.Min(count, 256));
                    byte[] identity = new byte[512];
                    byte[] name = new byte[1024];
                    for (int i = 0; i < count && i < 256; i++) {
                        if (library.Device(context, i, identity, identity.Length, name, name.Length, out int isDefault) != 0) { continue; }
                        available.Add(new(VoiceDeviceId.FromValue(ReadUtf8(identity)), ReadUtf8(name), isDefault != 0));
                    }
                    state = available.Count == 0 ? VoiceCaptureState.NoDevice : VoiceCaptureState.Disabled;
                    return available.AsReadOnly();
                }
                finally {
                    library.Destroy(context);
                }
            }
        }

        private static bool HasSameDevices(IReadOnlyList<VoiceDeviceDescriptor> refreshedDevices) {
            if (devices.Count != refreshedDevices.Count) { return false; }
            for (int i = 0; i < devices.Count; i++) {
                VoiceDeviceDescriptor previousDevice = devices[i];
                VoiceDeviceDescriptor currentDevice = refreshedDevices[i];
                if (previousDevice.Id != currentDevice.Id || previousDevice.DisplayName != currentDevice.DisplayName || previousDevice.IsDefault != currentDevice.IsDefault) { return false; }
            }
            return true;
        }

    }

}
