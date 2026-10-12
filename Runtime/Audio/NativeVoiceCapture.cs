using System;
using System.Text;

namespace Lockyaw.VoiceChat {

    internal sealed class NativeVoiceCapture : IVoiceCaptureSource, IVoiceCaptureSignal {

        internal const int SAMPLE_RATE = 48000;
        public VoiceDeviceDescriptor Device { get; private set; }
        public int SampleRate => SAMPLE_RATE;
        public long DroppedSamples { get; private set; }
        public long DiscontinuityCount { get; private set; }
        public int QueuedSamples => context == IntPtr.Zero ? 0 : library.Queued(context);
        public VoiceCaptureState State => context == IntPtr.Zero || state != VoiceCaptureState.Running ? state : (VoiceCaptureState)library.State(context);
        public bool CanSignal => library != null && library.Wait != null;
        public int DevicePeriodSamples => context == IntPtr.Zero || library.Period == null || state != VoiceCaptureState.Running ? 0 : library.Period(context);

        private readonly string libraryPath;
        // Serializes interrupts from other threads with context destruction on the owning worker.
        private readonly object contextLock = new();
        private NativeCaptureLibrary library;
        private IntPtr context;
        private int preferredPeriodSamples;
        private VoiceCaptureState state = VoiceCaptureState.Disabled;
        private VoiceDeviceId requestedDevice;
        private bool isDisposed;

        internal NativeVoiceCapture() : this(NativeCapturePlatform.LibraryPath) { }

        internal NativeVoiceCapture(string libraryPath) {
            this.libraryPath = libraryPath;
        }

        public void Dispose() {
            if (isDisposed) { return; }
            isDisposed = true;
            lock (contextLock) {
                if (context != IntPtr.Zero) {
                    library.Destroy(context);
                    context = IntPtr.Zero;
                }
            }
            if (library != null) {
                library.Dispose();
                library = null;
            }
            state = VoiceCaptureState.Disabled;
        }

        public VoiceCaptureState Start(VoiceDeviceId deviceId) {
            if (isDisposed) { return VoiceCaptureState.Disabled; }
            Stop();
            requestedDevice = deviceId;
            if (deviceId.IsNone) { return state; }
            if (deviceId.Value.IndexOf('\0') >= 0) {
                state = VoiceCaptureState.NoDevice;
                return state;
            }
            if (library == null && !NativeCaptureLibrary.TryLoad(libraryPath, out library)) {
                state = VoiceCaptureState.BackendUnavailable;
                return state;
            }
            if (context == IntPtr.Zero) {
                state = (VoiceCaptureState)library.Create(out IntPtr createdContext);
                lock (contextLock) { context = createdContext; }
                if (context == IntPtr.Zero) { return state; }
            }
            if (library.SetPeriod != null && preferredPeriodSamples > 0) { library.SetPeriod(context, preferredPeriodSamples); }
            byte[] identity = Encoding.UTF8.GetBytes(deviceId.Value + "\0");
            state = (VoiceCaptureState)library.Start(context, identity);
            if (state == VoiceCaptureState.Running) { ResolveSelectedDevice(deviceId); }
            return state;
        }

        public void Stop() {
            if (context != IntPtr.Zero) { library.Stop(context); }
            state = VoiceCaptureState.Disabled;
            Device = default;
        }

        public void SetPreferredPeriod(int samples) {
            preferredPeriodSamples = Math.Max(0, samples);
        }

        public bool WaitForSamples(int timeoutMilliseconds) {
            if (context == IntPtr.Zero || library.Wait == null || state != VoiceCaptureState.Running) { return false; }
            library.Wait(context, timeoutMilliseconds);
            return true;
        }

        public void InterruptWait() {
            lock (contextLock) {
                if (context != IntPtr.Zero && library != null && library.Signal != null) { library.Signal(context); }
            }
        }

        public int Read(float[] destination, int maximumSamples) {
            if (destination == null) { throw new ArgumentNullException(nameof(destination)); }
            if (maximumSamples < 0 || maximumSamples > destination.Length) { throw new ArgumentOutOfRangeException(nameof(maximumSamples)); }
            if (context == IntPtr.Zero || State != VoiceCaptureState.Running || maximumSamples == 0) { return 0; }
            int readCount = library.Read(context, destination, maximumSamples, out ulong droppedSamples, out ulong discontinuities);
            DroppedSamples = (long)Math.Min(droppedSamples, (ulong)long.MaxValue);
            long previousDiscontinuities = DiscontinuityCount;
            DiscontinuityCount = (long)Math.Min(discontinuities, (ulong)long.MaxValue);
            if (requestedDevice.IsDefault && previousDiscontinuities != DiscontinuityCount) { ResolveSelectedDevice(requestedDevice); }
            return readCount;
        }

        private void ResolveSelectedDevice(VoiceDeviceId deviceId) {
            library.Refresh(context, out int count);
            byte[] identity = new byte[512];
            byte[] name = new byte[1024];
            for (int i = 0; i < count; i++) {
                if (library.Device(context, i, identity, identity.Length, name, name.Length, out int isDefault) != 0) { continue; }
                VoiceDeviceId availableId = VoiceDeviceId.FromValue(MicrophoneDevices.ReadUtf8(identity));
                if (deviceId.IsDefault ? isDefault == 0 : availableId != deviceId) { continue; }
                Device = new(availableId, MicrophoneDevices.ReadUtf8(name), isDefault != 0);
                return;
            }
            Device = new(deviceId, deviceId.IsDefault ? "Default microphone" : deviceId.Value, deviceId.IsDefault);
        }

    }

}
