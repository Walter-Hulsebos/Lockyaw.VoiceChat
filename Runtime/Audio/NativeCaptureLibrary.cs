using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Lockyaw.VoiceChat {

    internal sealed class NativeCaptureLibrary : IDisposable {

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetAbi();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int CreateContext(out IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int RefreshDevices(IntPtr context, out int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GetDevice(IntPtr context, int index, [Out] byte[] identity, int identityCapacity, [Out] byte[] name, int nameCapacity, out int isDefault);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int StartCapture(IntPtr context, byte[] identity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ReadSamples(IntPtr context, [Out] float[] destination, int capacity, out ulong droppedSamples, out ulong discontinuities);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GetContextValue(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ExecuteContextCommand(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SetContextValue(IntPtr context, int value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int WaitForContext(IntPtr context, int timeoutMilliseconds);

        internal const int SIGNAL_ABI = 2;

        internal int Abi { get; private set; }
        internal CreateContext Create { get; private set; }
        internal RefreshDevices Refresh { get; private set; }
        internal GetDevice Device { get; private set; }
        internal StartCapture Start { get; private set; }
        internal ReadSamples Read { get; private set; }
        internal GetContextValue State { get; private set; }
        internal GetContextValue Queued { get; private set; }
        internal ExecuteContextCommand Stop { get; private set; }
        internal ExecuteContextCommand Destroy { get; private set; }
        // ABI 2 only; null with an ABI 1 library.
        internal SetContextValue SetPeriod { get; private set; }
        internal GetContextValue Period { get; private set; }
        internal WaitForContext Wait { get; private set; }
        internal ExecuteContextCommand Signal { get; private set; }

        private IntPtr handle;

        private NativeCaptureLibrary(IntPtr handle) {
            this.handle = handle;
        }

        public void Dispose() {
            if (handle == IntPtr.Zero) { return; }
            CloseLibrary(handle);
            handle = IntPtr.Zero;
        }

        internal static bool TryLoad(string path, out NativeCaptureLibrary library) {
            library = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }
            NativeCaptureLibrary candidate = null;
            try {
                IntPtr loadedHandle = OpenLibrary(path);
                if (loadedHandle == IntPtr.Zero) { return false; }
                candidate = new(loadedHandle);
                GetAbi getAbi = candidate.GetFunction<GetAbi>("voice_capture_abi");
                int abi = getAbi();
                if (abi != 1 && abi != SIGNAL_ABI) {
                    candidate.Dispose();
                    return false;
                }
                candidate.Abi = abi;
                candidate.Create = candidate.GetFunction<CreateContext>("voice_capture_create");
                candidate.Refresh = candidate.GetFunction<RefreshDevices>("voice_capture_refresh");
                candidate.Device = candidate.GetFunction<GetDevice>("voice_capture_device");
                candidate.Start = candidate.GetFunction<StartCapture>("voice_capture_start");
                candidate.Read = candidate.GetFunction<ReadSamples>("voice_capture_read");
                candidate.State = candidate.GetFunction<GetContextValue>("voice_capture_state");
                candidate.Queued = candidate.GetFunction<GetContextValue>("voice_capture_queued");
                candidate.Stop = candidate.GetFunction<ExecuteContextCommand>("voice_capture_stop");
                candidate.Destroy = candidate.GetFunction<ExecuteContextCommand>("voice_capture_destroy");
                if (abi >= SIGNAL_ABI) {
                    candidate.SetPeriod = candidate.GetFunction<SetContextValue>("voice_capture_set_period");
                    candidate.Period = candidate.GetFunction<GetContextValue>("voice_capture_period");
                    candidate.Wait = candidate.GetFunction<WaitForContext>("voice_capture_wait");
                    candidate.Signal = candidate.GetFunction<ExecuteContextCommand>("voice_capture_signal");
                }
                library = candidate;
                return true;
            }
            catch (Exception) {
                if (candidate != null) { candidate.Dispose(); }
                return false;
            }
        }

        private TDelegate GetFunction<TDelegate>(string name) where TDelegate : Delegate {
            IntPtr address = GetSymbol(handle, name);
            if (address == IntPtr.Zero) { throw new EntryPointNotFoundException(name); }
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path, IntPtr file, uint flags);

        [DllImport("kernel32", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetSymbol(IntPtr module, string name);

        [DllImport("kernel32", EntryPoint = "FreeLibrary")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseLibrary(IntPtr module);

        private static IntPtr OpenLibrary(string path) => LoadLibrary(path, IntPtr.Zero, 0x00001100);
#elif UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
        private static extern IntPtr LoadLibrary(string path, int mode);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym")]
        private static extern IntPtr GetSymbol(IntPtr module, string name);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlclose")]
        private static extern int CloseLibrary(IntPtr module);

        private static IntPtr OpenLibrary(string path) => LoadLibrary(path, 2);
#elif UNITY_EDITOR_LINUX || (UNITY_STANDALONE_LINUX && !UNITY_EDITOR)
        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        private static extern IntPtr LoadLibrary(string path, int mode);

        [DllImport("libdl.so.2", EntryPoint = "dlsym")]
        private static extern IntPtr GetSymbol(IntPtr module, string name);

        [DllImport("libdl.so.2", EntryPoint = "dlclose")]
        private static extern int CloseLibrary(IntPtr module);

        private static IntPtr OpenLibrary(string path) => LoadLibrary(path, 2);
#else
        private static IntPtr OpenLibrary(string path) => IntPtr.Zero;

        private static IntPtr GetSymbol(IntPtr module, string name) => IntPtr.Zero;

        private static void CloseLibrary(IntPtr module) { }
#endif

    }

}
