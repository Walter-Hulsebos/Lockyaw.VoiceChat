namespace Lockyaw.VoiceChat {

    // Optional capture capability: the device wakes the reader when it delivers samples.
    internal interface IVoiceCaptureSignal {

        bool CanSignal { get; }
        // Granted device period in 48 kHz samples, or zero when unknown.
        int DevicePeriodSamples { get; }

        // Applies to the next Start.
        void SetPreferredPeriod(int samples);
        // Returns false without waiting when the device cannot signal.
        bool WaitForSamples(int timeoutMilliseconds);
        void InterruptWait();

    }

}
