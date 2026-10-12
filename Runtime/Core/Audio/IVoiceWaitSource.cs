namespace Lockyaw.VoiceChat {

    // A processor that can block until its own input has data, such as a device-signalled capture.
    internal interface IVoiceWaitSource {

        // Returns false without waiting when the source cannot currently signal new work.
        bool WaitForWork(int timeoutMilliseconds);

        // Releases a pending WaitForWork from another thread.
        void InterruptWait();

    }

}
