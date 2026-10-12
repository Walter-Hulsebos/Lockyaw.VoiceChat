using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Lockyaw.VoiceChat {

    internal sealed class VoiceProcessingWorker : IDisposable {

        internal static double CurrentTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        internal bool IsRunning => Volatile.Read(ref stopping) == 0;
        internal string LastFailure => Volatile.Read(ref lastFailure);

        private const int MAXIMUM_COMMANDS = 256;
        private const int MAXIMUM_PROCESSORS = 128;
        private const int POLL_MILLISECONDS = 2;
        private const int SIGNAL_WAIT_MILLISECONDS = 20;

        private readonly Queue<Action> commands = new(MAXIMUM_COMMANDS);
        private readonly List<IVoiceProcessor> processors = new(MAXIMUM_PROCESSORS);
        private readonly HashSet<IVoiceProcessor> requestedProcessors = new();
        private readonly HashSet<IVoiceProcessor> pendingDisposals = new();
        private readonly IVoiceProcessor[] requestedSnapshot = new IVoiceProcessor[MAXIMUM_PROCESSORS];
        private readonly IVoiceProcessor[] disposalSnapshot = new IVoiceProcessor[MAXIMUM_PROCESSORS];
        private readonly object commandLock = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly Thread thread;
        private IVoiceWaitSource activeWaitSource;
        private string lastFailure = string.Empty;
        private int stopping;

        internal VoiceProcessingWorker(string name = "Voice Processing", ThreadPriority priority = ThreadPriority.AboveNormal) {
            thread = new Thread(Run) { IsBackground = true, Name = name, Priority = priority };
            thread.Start();
        }

        public void Dispose() {
            lock (commandLock) {
                if (Interlocked.Exchange(ref stopping, 1) == 0) { SignalLocked(); }
            }
            if (Thread.CurrentThread != thread) { thread.Join(); }
        }

        // Requests a processing pass as soon as possible. Safe to call from any thread.
        internal void Wake() {
            lock (commandLock) {
                if (stopping == 0) { SignalLocked(); }
            }
        }

        internal bool Register(IVoiceProcessor processor) {
            if (processor == null) { return false; }
            lock (commandLock) {
                if (stopping != 0 || pendingDisposals.Contains(processor)) { return false; }
                if (requestedProcessors.Contains(processor)) { return true; }
                if (requestedProcessors.Count + pendingDisposals.Count >= MAXIMUM_PROCESSORS) { return false; }
                requestedProcessors.Add(processor);
                SignalLocked();
                return true;
            }
        }

        internal bool Unregister(IVoiceProcessor processor) {
            if (processor == null) { return false; }
            lock (commandLock) {
                if (stopping != 0) { return false; }
                if (pendingDisposals.Contains(processor)) { return true; }
                if (!requestedProcessors.Remove(processor)) { return false; }
                pendingDisposals.Add(processor);
                SignalLocked();
                return true;
            }
        }

        internal bool TryPost(Action command) {
            if (command == null) { return false; }
            lock (commandLock) {
                if (stopping != 0 || commands.Count >= MAXIMUM_COMMANDS) { return false; }
                commands.Enqueue(command);
                SignalLocked();
                return true;
            }
        }

        private void SignalLocked() {
            wake.Set();
            IVoiceWaitSource waitSource = Volatile.Read(ref activeWaitSource);
            if (waitSource != null) { waitSource.InterruptWait(); }
        }

        private void Run() {
            try {
                while (Volatile.Read(ref stopping) == 0) {
                    SynchronizeProcessors();
                    DrainCommands();
                    double currentTime = CurrentTime;
                    for (int i = 0; i < processors.Count; i++) {
                        try { processors[i].Process(currentTime); }
                        catch (Exception exception) { Volatile.Write(ref lastFailure, exception.Message); }
                    }
                    WaitForWork();
                }
            }
            finally {
                SynchronizeProcessors();
                for (int i = 0; i < processors.Count; i++) { DisposeProcessor(processors[i]); }
                processors.Clear();
                lock (commandLock) {
                    Volatile.Write(ref stopping, 1);
                    requestedProcessors.Clear();
                    commands.Clear();
                    wake.Dispose();
                }
            }
        }

        // A single signalling processor, such as device capture, wakes the worker directly; anything else is polled.
        private void WaitForWork() {
            IVoiceWaitSource waitSource = processors.Count == 1 ? processors[0] as IVoiceWaitSource : null;
            if (waitSource == null) {
                wake.WaitOne(POLL_MILLISECONDS);
                return;
            }
            Interlocked.Exchange(ref activeWaitSource, waitSource);
            bool waited = wake.WaitOne(0);
            try {
                if (!waited) { waited = waitSource.WaitForWork(SIGNAL_WAIT_MILLISECONDS); }
            }
            catch (Exception exception) { Volatile.Write(ref lastFailure, exception.Message); }
            finally { Interlocked.Exchange(ref activeWaitSource, null); }
            if (!waited) { wake.WaitOne(POLL_MILLISECONDS); }
        }

        private void SynchronizeProcessors() {
            int requestedCount;
            int disposalCount;
            lock (commandLock) {
                requestedCount = requestedProcessors.Count;
                disposalCount = pendingDisposals.Count;
                requestedProcessors.CopyTo(requestedSnapshot);
                pendingDisposals.CopyTo(disposalSnapshot);
                pendingDisposals.Clear();
            }
            for (int i = 0; i < disposalCount; i++) {
                IVoiceProcessor processor = disposalSnapshot[i];
                disposalSnapshot[i] = null;
                processors.Remove(processor);
                DisposeProcessor(processor);
            }
            for (int i = 0; i < requestedCount; i++) {
                IVoiceProcessor processor = requestedSnapshot[i];
                requestedSnapshot[i] = null;
                if (!processors.Contains(processor)) { processors.Add(processor); }
            }
        }

        private void DrainCommands() {
            for (int i = 0; i < MAXIMUM_COMMANDS; i++) {
                Action command;
                lock (commandLock) {
                    if (commands.Count == 0) { return; }
                    command = commands.Dequeue();
                }
                try { command(); }
                catch (Exception exception) { Volatile.Write(ref lastFailure, exception.Message); }
            }
        }

        private void DisposeProcessor(IVoiceProcessor processor) {
            try { processor.Dispose(); }
            catch (Exception exception) { Volatile.Write(ref lastFailure, exception.Message); }
        }

    }

}
