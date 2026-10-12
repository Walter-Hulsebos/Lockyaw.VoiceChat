# Native microphone capture

The capture plug-in wraps pinned miniaudio 0.11.23 behind C ABI version 2. Its upstream source hash and MIT-0 license are in `third-party`. Miniaudio is compiled into the plug-in; no separately installed miniaudio or Opus library is required.

The Windows x64 binary is included. The source selects WASAPI on Windows, Core Audio on macOS, and PulseAudio with ALSA fallback on Linux. The Windows path uses shared event-driven capture, requests the period set by `voice_capture_set_period` (10 ms when unset; the managed bridge requests the frame length), enables miniaudio conversion instead of WASAPI automatic conversion so the IAudioClient3 path is eligible, and lets miniaudio fall back to ordinary shared mode when necessary. `voice_capture_period` reports the granted period in 48 kHz frames; many devices grant 10 ms even when a shorter period is requested. The callback format is float32 mono at 48 kHz. Actual hardware period and format can differ from the request.

Only the native capture callback produces samples. One managed processing worker owns each capture context and calls start/read/stop/destroy. Do not call these methods concurrently or free a context before its worker stops. Destroy waits for native capture to stop before freeing its preallocated ring. Enumeration uses a separate context and never starts recording.

The one-second ring drops incoming samples when full. Its consumer discards queued history beyond 100 ms before reading; it owns the read cursor. Drops and discontinuities are counted. The callback performs bounded copies and atomic counter updates, without managed callbacks or allocation, and then signals an auto-reset event (Windows), a dispatch semaphore (macOS) or a POSIX semaphore (Linux). `voice_capture_wait` blocks the consumer until that signal or a timeout; `voice_capture_signal` releases a waiting consumer from another thread. The managed bridge also loads ABI 1 libraries, which have no signal and are polled. Miniaudio performs sample conversion on its native device thread.

The managed bridge loads by an explicit platform-specific path with the OS loader, then validates the ABI and resolves exports. Missing, invalid or unsupported binaries return BackendUnavailable without invoking Unity's third-party plug-in loader. Windows is built with a static C/C++ runtime and its only static DLL dependency is KERNEL32.dll. The WASAPI backend dynamically loads Windows system libraries. Other platforms require their standard audio libraries. Expected device failures are status values; miniaudio debug output is disabled.

## Build

Windows, from PowerShell with Visual Studio C++ x64 build tools installed:

```powershell
./build-windows.ps1
```

The script puts the binary in `Runtime/Plugins/Windows/x86_64` and compiler intermediates under Documents/Codex by default. Override OutputDirectory and BuildDirectory when running CI.

Linux x64, on a Linux build host with CMake and a C++ compiler:

```sh
bash build-unix.sh
```

The script installs the binary and platform-specific Unity metadata. Its first optional argument chooses the external build directory; its second chooses the output directory. The equivalent manual build is:

```sh
cmake -S . -B "$HOME/voice-capture-build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$HOME/voice-capture-build" --config Release
```

Copy `libLockyawVoiceCapture.so` to `Runtime/Plugins/Linux/x86_64`. Set its Unity PluginImporter to Linux Editor/Linux x86_64 only. Test the target distribution's PulseAudio/PipeWire Pulse compatibility and ALSA libraries. ALSA may emit its own diagnostics; zero-device/no-stderr acceptance must be verified on the target host before advertising that configuration.

macOS, on an Apple build host with Xcode command-line tools and CMake:

```sh
bash build-unix.sh
```

The script builds both Intel and Apple Silicon architectures and installs the library and Unity metadata. The equivalent manual build is:

```sh
cmake -S . -B "$HOME/voice-capture-build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"
cmake --build "$HOME/voice-capture-build" --config Release
```

Copy `libLockyawVoiceCapture.dylib` to `Runtime/Plugins/macOS`. Set its Unity PluginImporter to macOS Editor/Standalone with matching CPU architectures. Configure the game's microphone usage description and signing/entitlements, then verify permission denial and capture in the signed player.

The platform selector uses the Editor host, independent of the current player build target. Headless and unsupported platforms do not load capture. Windows players on Proton use the Windows binary. macOS/Linux binaries and hardware validation require the corresponding build/test hosts; a build recipe alone is not a passing platform result.
