using System;

namespace Lockyaw.VoiceChat {

    internal interface IVoiceCaptureSource : IDisposable {

        VoiceDeviceDescriptor Device { get; }
        int SampleRate { get; }
        long DroppedSamples { get; }
        long DiscontinuityCount { get; }
        int QueuedSamples { get; }
        VoiceCaptureState State { get; }

        VoiceCaptureState Start(VoiceDeviceId deviceId);
        void Stop();
        int Read(float[] destination, int maximumSamples);

    }

}
