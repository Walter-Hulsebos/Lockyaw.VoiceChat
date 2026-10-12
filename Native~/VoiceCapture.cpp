#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MA_NO_GENERATION
#define MA_NO_DSOUND
#define MA_NO_WINMM
#define MA_NO_JACK
#define MA_NO_SNDIO
#define MA_NO_AUDIO4
#define MA_NO_OSS
#define MA_NO_AAUDIO
#define MA_NO_OPENSL
#define MA_NO_WEBAUDIO
#define MA_NO_CUSTOM
#define MA_NO_NULL
#include "third-party/miniaudio.h"
#include <atomic>
#include <new>
#include <string>
#include <cstring>

#if defined(_WIN32)
#define VOICE_EXPORT extern "C" __declspec(dllexport)
#else
#define VOICE_EXPORT extern "C" __attribute__((visibility("default")))
#endif

#if defined(__APPLE__)
#include <dispatch/dispatch.h>
#elif !defined(_WIN32)
#include <cerrno>
#include <ctime>
#include <semaphore.h>
#endif

static const ma_uint32 DEFAULT_PERIOD_FRAMES = 480;

// Wakes the consumer when the device delivers samples. Signalling never blocks the device thread.
struct CaptureSignal {
#if defined(_WIN32)
    HANDLE handle = nullptr;

    bool Initialize() {
        handle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        return handle != nullptr;
    }

    void Uninitialize() {
        if (handle != nullptr) { CloseHandle(handle); }
        handle = nullptr;
    }

    void Signal() { SetEvent(handle); }

    bool Wait(int timeoutMilliseconds) { return WaitForSingleObject(handle, static_cast<DWORD>(timeoutMilliseconds)) == WAIT_OBJECT_0; }
#elif defined(__APPLE__)
    dispatch_semaphore_t semaphore = nullptr;

    bool Initialize() {
        semaphore = dispatch_semaphore_create(0);
        return semaphore != nullptr;
    }

    void Uninitialize() {
        if (semaphore != nullptr) { dispatch_release(semaphore); }
        semaphore = nullptr;
    }

    void Signal() { dispatch_semaphore_signal(semaphore); }

    bool Wait(int timeoutMilliseconds) {
        if (dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, static_cast<int64_t>(timeoutMilliseconds) * 1000000)) != 0) { return false; }
        while (dispatch_semaphore_wait(semaphore, DISPATCH_TIME_NOW) == 0) { }
        return true;
    }
#else
    sem_t semaphore{};
    bool initialized = false;

    bool Initialize() {
        initialized = sem_init(&semaphore, 0, 0) == 0;
        return initialized;
    }

    void Uninitialize() {
        if (initialized) { sem_destroy(&semaphore); }
        initialized = false;
    }

    void Signal() { sem_post(&semaphore); }

    bool Wait(int timeoutMilliseconds) {
        timespec deadline{};
        clock_gettime(CLOCK_REALTIME, &deadline);
        deadline.tv_sec += timeoutMilliseconds / 1000;
        deadline.tv_nsec += static_cast<long>(timeoutMilliseconds % 1000) * 1000000L;
        if (deadline.tv_nsec >= 1000000000L) {
            deadline.tv_sec++;
            deadline.tv_nsec -= 1000000000L;
        }
        int result;
        do { result = sem_timedwait(&semaphore, &deadline); } while (result != 0 && errno == EINTR);
        if (result != 0) { return false; }
        while (sem_trywait(&semaphore) == 0) { }
        return true;
    }
#endif
};

enum VoiceCaptureState {
    Disabled = 0,
    NoDevice = 1,
    PermissionDenied = 2,
    Opening = 3,
    Running = 4,
    Disconnected = 5,
    BackendUnavailable = 6
};

struct VoiceCaptureContext {
    ma_context context{};
    ma_device device{};
    ma_pcm_rb samples{};
    CaptureSignal dataReady{};
    ma_device_info* devices = nullptr;
    ma_uint32 deviceCount = 0;
    ma_uint32 periodFrames = DEFAULT_PERIOD_FRAMES;
    std::atomic<int> state{Disabled};
    std::atomic<ma_uint64> droppedSamples{0};
    std::atomic<ma_uint64> discontinuities{0};
    bool contextInitialized = false;
    bool samplesInitialized = false;
    bool signalInitialized = false;
    bool deviceInitialized = false;
};

static int GetFailureState(ma_result result) {
    if (result == MA_ACCESS_DENIED) { return PermissionDenied; }
    if (result == MA_NO_DEVICE || result == MA_INVALID_DEVICE_CONFIG) { return NoDevice; }
    return BackendUnavailable;
}

static std::string GetDeviceIdentity(const ma_context& context, const ma_device_id& deviceId) {
    switch (context.backend) {
#if defined(MA_SUPPORT_WASAPI)
        case ma_backend_wasapi: {
            char converted[256]{};
            int convertedLength = WideCharToMultiByte(CP_UTF8, 0, deviceId.wasapi, -1, converted, sizeof(converted), nullptr, nullptr);
            return convertedLength > 0 ? std::string("wasapi:") + converted : std::string();
        }
#endif
        case ma_backend_coreaudio: return std::string("coreaudio:") + deviceId.coreaudio;
        case ma_backend_pulseaudio: return std::string("pulseaudio:") + deviceId.pulse;
        case ma_backend_alsa: return std::string("alsa:") + deviceId.alsa;
        default: return std::string();
    }
}

static void HandleCaptureData(ma_device* device, void*, const void* input, ma_uint32 frameCount) {
    VoiceCaptureContext* capture = static_cast<VoiceCaptureContext*>(device->pUserData);
    if (input == nullptr || capture->state.load(std::memory_order_relaxed) != Running) { return; }
    const float* source = static_cast<const float*>(input);
    ma_uint32 writtenFrames = 0;
    while (writtenFrames < frameCount) {
        ma_uint32 writableFrames = frameCount - writtenFrames;
        void* destination = nullptr;
        if (ma_pcm_rb_acquire_write(&capture->samples, &writableFrames, &destination) != MA_SUCCESS || writableFrames == 0) { break; }
        std::memcpy(destination, source + writtenFrames, writableFrames * sizeof(float));
        ma_pcm_rb_commit_write(&capture->samples, writableFrames);
        writtenFrames += writableFrames;
    }
    if (writtenFrames < frameCount) {
        capture->droppedSamples.fetch_add(frameCount - writtenFrames, std::memory_order_relaxed);
        capture->discontinuities.fetch_add(1, std::memory_order_relaxed);
    }
    if (writtenFrames > 0) { capture->dataReady.Signal(); }
}

static void HandleDeviceNotification(const ma_device_notification* notification) {
    VoiceCaptureContext* capture = static_cast<VoiceCaptureContext*>(notification->pDevice->pUserData);
    switch (notification->type) {
        case ma_device_notification_type_started:
            capture->state.store(Running, std::memory_order_release);
            break;
        case ma_device_notification_type_stopped:
        case ma_device_notification_type_interruption_began:
            capture->state.store(Disconnected, std::memory_order_release);
            capture->discontinuities.fetch_add(1, std::memory_order_relaxed);
            break;
        case ma_device_notification_type_rerouted:
            capture->discontinuities.fetch_add(1, std::memory_order_relaxed);
            break;
        default:
            break;
    }
}

VOICE_EXPORT int voice_capture_abi() { return 2; }

VOICE_EXPORT int voice_capture_create(VoiceCaptureContext** output) {
    if (output == nullptr) { return BackendUnavailable; }
    *output = nullptr;
    VoiceCaptureContext* capture = new (std::nothrow) VoiceCaptureContext();
    if (capture == nullptr) { return BackendUnavailable; }
#if defined(_WIN32)
    ma_backend backends[] = { ma_backend_wasapi };
#elif defined(__APPLE__)
    ma_backend backends[] = { ma_backend_coreaudio };
#elif defined(__linux__)
    ma_backend backends[] = { ma_backend_pulseaudio, ma_backend_alsa };
#else
    delete capture;
    return BackendUnavailable;
#endif
    ma_context_config configuration = ma_context_config_init();
    ma_result result = ma_context_init(backends, sizeof(backends) / sizeof(backends[0]), &configuration, &capture->context);
    if (result != MA_SUCCESS) {
        delete capture;
        return GetFailureState(result);
    }
    capture->contextInitialized = true;
    result = ma_pcm_rb_init(ma_format_f32, 1, 48000, nullptr, nullptr, &capture->samples);
    if (result != MA_SUCCESS) {
        ma_context_uninit(&capture->context);
        delete capture;
        return BackendUnavailable;
    }
    capture->samplesInitialized = true;
    if (!capture->dataReady.Initialize()) {
        ma_pcm_rb_uninit(&capture->samples);
        ma_context_uninit(&capture->context);
        delete capture;
        return BackendUnavailable;
    }
    capture->signalInitialized = true;
    *output = capture;
    return Disabled;
}

VOICE_EXPORT int voice_capture_refresh(VoiceCaptureContext* capture, int* count) {
    if (capture == nullptr || count == nullptr) { return BackendUnavailable; }
    *count = 0;
    ma_result result = ma_context_get_devices(&capture->context, nullptr, nullptr, &capture->devices, &capture->deviceCount);
    if (result != MA_SUCCESS) { return GetFailureState(result); }
    *count = static_cast<int>(capture->deviceCount);
    return capture->deviceCount == 0 ? NoDevice : Disabled;
}

VOICE_EXPORT int voice_capture_device(VoiceCaptureContext* capture, int index, char* identity, int identityCapacity, char* name, int nameCapacity, int* isDefault) {
    if (capture == nullptr || index < 0 || static_cast<ma_uint32>(index) >= capture->deviceCount || identity == nullptr || name == nullptr || isDefault == nullptr) { return NoDevice; }
    const ma_device_info& device = capture->devices[index];
    std::string deviceIdentity = GetDeviceIdentity(capture->context, device.id);
    size_t nameLength = std::strlen(device.name);
    if (deviceIdentity.empty() || identityCapacity <= static_cast<int>(deviceIdentity.length()) || nameCapacity <= static_cast<int>(nameLength)) { return BackendUnavailable; }
    std::memcpy(identity, deviceIdentity.c_str(), deviceIdentity.length() + 1);
    std::memcpy(name, device.name, nameLength + 1);
    *isDefault = device.isDefault != MA_FALSE ? 1 : 0;
    return Disabled;
}

VOICE_EXPORT void voice_capture_stop(VoiceCaptureContext* capture) {
    if (capture == nullptr) { return; }
    if (capture->deviceInitialized) {
        ma_device_uninit(&capture->device);
        capture->deviceInitialized = false;
    }
    ma_pcm_rb_reset(&capture->samples);
    capture->state.store(Disabled, std::memory_order_release);
}

VOICE_EXPORT int voice_capture_start(VoiceCaptureContext* capture, const char* identity) {
    if (capture == nullptr || identity == nullptr) { return BackendUnavailable; }
    voice_capture_stop(capture);
    int deviceCount = 0;
    int refreshState = voice_capture_refresh(capture, &deviceCount);
    if (refreshState != Disabled) {
        capture->state.store(refreshState, std::memory_order_release);
        return refreshState;
    }
    const ma_device_id* selectedDevice = nullptr;
    if (identity[0] != '\0') {
        for (int i = 0; i < deviceCount; i++) {
            if (GetDeviceIdentity(capture->context, capture->devices[i].id) != identity) { continue; }
            selectedDevice = &capture->devices[i].id;
            break;
        }
        if (selectedDevice == nullptr) {
            capture->state.store(NoDevice, std::memory_order_release);
            return NoDevice;
        }
    }
    ma_device_config configuration = ma_device_config_init(ma_device_type_capture);
    configuration.capture.pDeviceID = selectedDevice;
    configuration.capture.format = ma_format_f32;
    configuration.capture.channels = 1;
    configuration.capture.shareMode = ma_share_mode_shared;
    configuration.sampleRate = 48000;
    configuration.periodSizeInFrames = capture->periodFrames;
    configuration.performanceProfile = ma_performance_profile_low_latency;
    configuration.wasapi.noAutoConvertSRC = MA_TRUE;
    configuration.dataCallback = HandleCaptureData;
    configuration.notificationCallback = HandleDeviceNotification;
    configuration.pUserData = capture;
    capture->state.store(Opening, std::memory_order_release);
    ma_result result = ma_device_init(&capture->context, &configuration, &capture->device);
    if (result != MA_SUCCESS) {
        int failureState = GetFailureState(result);
        capture->state.store(failureState, std::memory_order_release);
        return failureState;
    }
    capture->deviceInitialized = true;
    result = ma_device_start(&capture->device);
    if (result != MA_SUCCESS) {
        voice_capture_stop(capture);
        int failureState = GetFailureState(result);
        capture->state.store(failureState, std::memory_order_release);
        return failureState;
    }
    capture->state.store(Running, std::memory_order_release);
    return Running;
}

VOICE_EXPORT int voice_capture_read(VoiceCaptureContext* capture, float* destination, int capacity, ma_uint64* droppedSamples, ma_uint64* discontinuities) {
    if (capture == nullptr || destination == nullptr || capacity <= 0 || droppedSamples == nullptr || discontinuities == nullptr) { return 0; }
    ma_uint32 available = ma_pcm_rb_available_read(&capture->samples);
    ma_uint32 retained = static_cast<ma_uint32>(capacity) > 4800 ? static_cast<ma_uint32>(capacity) : 4800;
    if (available > retained) {
        ma_uint32 discarded = available - retained;
        ma_pcm_rb_seek_read(&capture->samples, discarded);
        capture->droppedSamples.fetch_add(discarded, std::memory_order_relaxed);
        capture->discontinuities.fetch_add(1, std::memory_order_relaxed);
    }
    ma_uint32 totalRead = 0;
    while (totalRead < static_cast<ma_uint32>(capacity)) {
        ma_uint32 readable = static_cast<ma_uint32>(capacity) - totalRead;
        void* source = nullptr;
        if (ma_pcm_rb_acquire_read(&capture->samples, &readable, &source) != MA_SUCCESS || readable == 0) { break; }
        std::memcpy(destination + totalRead, source, readable * sizeof(float));
        ma_pcm_rb_commit_read(&capture->samples, readable);
        totalRead += readable;
    }
    *droppedSamples = capture->droppedSamples.load(std::memory_order_acquire);
    *discontinuities = capture->discontinuities.load(std::memory_order_acquire);
    return static_cast<int>(totalRead);
}

VOICE_EXPORT int voice_capture_state(VoiceCaptureContext* capture) {
    return capture == nullptr ? BackendUnavailable : capture->state.load(std::memory_order_acquire);
}

VOICE_EXPORT int voice_capture_queued(VoiceCaptureContext* capture) {
    return capture == nullptr ? 0 : static_cast<int>(ma_pcm_rb_available_read(&capture->samples));
}

VOICE_EXPORT void voice_capture_destroy(VoiceCaptureContext* capture) {
    if (capture == nullptr) { return; }
    voice_capture_stop(capture);
    if (capture->signalInitialized) { capture->dataReady.Uninitialize(); }
    if (capture->samplesInitialized) { ma_pcm_rb_uninit(&capture->samples); }
    if (capture->contextInitialized) { ma_context_uninit(&capture->context); }
    delete capture;
}

// ABI 2: device-driven wakeups and a requested device period.

VOICE_EXPORT void voice_capture_set_period(VoiceCaptureContext* capture, int periodFrames) {
    if (capture == nullptr) { return; }
    capture->periodFrames = periodFrames >= 48 && periodFrames <= 4800 ? static_cast<ma_uint32>(periodFrames) : DEFAULT_PERIOD_FRAMES;
}

// The granted device period, converted to 48 kHz frames. Zero when no device is open.
VOICE_EXPORT int voice_capture_period(VoiceCaptureContext* capture) {
    if (capture == nullptr || !capture->deviceInitialized || capture->device.capture.internalSampleRate == 0) { return 0; }
    ma_uint64 frames = static_cast<ma_uint64>(capture->device.capture.internalPeriodSizeInFrames) * 48000 / capture->device.capture.internalSampleRate;
    return static_cast<int>(frames);
}

VOICE_EXPORT int voice_capture_wait(VoiceCaptureContext* capture, int timeoutMilliseconds) {
    if (capture == nullptr || !capture->signalInitialized || timeoutMilliseconds < 0) { return 0; }
    return capture->dataReady.Wait(timeoutMilliseconds) ? 1 : 0;
}

VOICE_EXPORT void voice_capture_signal(VoiceCaptureContext* capture) {
    if (capture != nullptr && capture->signalInitialized) { capture->dataReady.Signal(); }
}
