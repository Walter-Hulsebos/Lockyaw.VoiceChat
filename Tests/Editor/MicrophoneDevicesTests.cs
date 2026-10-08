using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class MicrophoneDevicesTests {

        [Test]
        public void NoDeviceDoesNotCallRecordingOrCaptureApis() {
            FakeMicrophoneApi microphoneApi = new();
            using (MicrophoneReader reader = new("Disconnected microphone", 960, microphoneApi)) {
                Assert.That(reader.Start(), Is.False);
                Assert.That(reader.IsRecording, Is.False);
                AssertNoFrame(reader);
                reader.Stop();
            }

            Assert.That(microphoneApi.StartCount, Is.Zero);
            Assert.That(microphoneApi.EndCount, Is.Zero);
            Assert.That(microphoneApi.RecordingQueryCount, Is.Zero);
            Assert.That(microphoneApi.PositionQueryCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void FailedEnumerationBehavesLikeNoMicrophone() {
            FakeMicrophoneApi microphoneApi = new() {
                EnumerationFailure = new DllNotFoundException("Unavailable capture backend")
            };
            Assert.That(MicrophoneDevices.GetAvailableDevices(microphoneApi), Is.Empty);
            using (MicrophoneReader reader = new("Unavailable microphone", 960, microphoneApi)) {
                Assert.That(reader.Start(), Is.False);
            }
            Assert.That(microphoneApi.StartCount, Is.Zero);
            Assert.That(microphoneApi.RecordingQueryCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DeviceEnumerationRemovesEmptyAndDuplicateNames() {
            FakeMicrophoneApi microphoneApi = new() {
                Devices = new[] { "Headset", "", null, " ", "Headset", "Desk" }
            };
            Assert.That(MicrophoneDevices.GetAvailableDevices(microphoneApi), Is.EqualTo(new[] { "Headset", "Desk" }));
            microphoneApi.Devices = null;
            Assert.That(MicrophoneDevices.GetAvailableDevices(microphoneApi), Is.Empty);
        }

        [Test]
        public void DisappearingDeviceDuringStartupDoesNotThrowOrQueryRecording() {
            FakeMicrophoneApi microphoneApi = new() {
                Devices = new[] { "Headset" },
                StartFailure = new InvalidOperationException("Device disconnected")
            };
            using (MicrophoneReader reader = new("Headset", 960, microphoneApi)) {
                Assert.That(reader.Start(), Is.False);
                Assert.That(reader.IsRecording, Is.False);
            }
            Assert.That(microphoneApi.StartCount, Is.EqualTo(1));
            Assert.That(microphoneApi.EndCount, Is.Zero);
            Assert.That(microphoneApi.RecordingQueryCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void NullCaptureClipDoesNotQueryOrStopADevice() {
            FakeMicrophoneApi microphoneApi = new() { Devices = new[] { "Headset" } };
            using (MicrophoneReader reader = new("Headset", 960, microphoneApi)) {
                Assert.That(reader.Start(), Is.False);
                AssertNoFrame(reader);
            }
            Assert.That(microphoneApi.EndCount, Is.Zero);
            Assert.That(microphoneApi.RecordingQueryCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ExplicitSelectionWaitsForThatDeviceInsteadOfSwitchingMicrophones() {
            Assert.That(MicrophoneDevices.ResolveDeviceName("Headset", new[] { "Desk" }), Is.Empty);
            Assert.That(MicrophoneDevices.ResolveDeviceName("Headset", new[] { "Desk", "Headset" }), Is.EqualTo("Headset"));
            Assert.That(MicrophoneDevices.ResolveDeviceName("", new[] { "Desk", "Headset" }), Is.EqualTo("Desk"));
            Assert.That(MicrophoneDevices.ResolveDeviceName("", Array.Empty<string>()), Is.Empty);
        }

        [UnityTest]
        public IEnumerator UnpluggedCaptureSkipsMissingDeviceCallsAndCanReconnect() {
            yield return new EnterPlayMode();
            AudioClip firstClip = AudioClip.Create("Microphone disconnect test", 48000, 1, 48000, false);
            FakeMicrophoneApi microphoneApi = new() {
                Devices = new[] { "Headset" },
                CaptureClip = firstClip
            };
            using (MicrophoneReader reader = new("Headset", 960, microphoneApi)) {
                Assert.That(reader.Start(), Is.True);
                microphoneApi.Devices = Array.Empty<string>();
                Assert.That(reader.IsRecording, Is.False);
                AssertNoFrame(reader);
                reader.Stop();

                Assert.That(microphoneApi.EndCount, Is.Zero);
                Assert.That(microphoneApi.RecordingQueryCount, Is.Zero);
                Assert.That(microphoneApi.PositionQueryCount, Is.Zero);
                yield return null;
                Assert.That(firstClip == null, Is.True);

                microphoneApi.Devices = new[] { "Headset" };
                microphoneApi.CaptureClip = AudioClip.Create("Microphone reconnect test", 48000, 1, 48000, false);
                Assert.That(reader.Start(), Is.True);
                Assert.That(reader.IsRecording, Is.True);
                microphoneApi.PositionFailure = new InvalidOperationException("Device disconnected during read");
                AssertNoFrame(reader);
                microphoneApi.EndFailure = new InvalidOperationException("Device disconnected during stop");
                Assert.DoesNotThrow(reader.Stop);
            }
            yield return null;
            Assert.That(microphoneApi.CaptureClip == null, Is.True);
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        private static void AssertNoFrame(MicrophoneReader reader) {
            Assert.That(reader.TryReadFrame(out ReadOnlySpan<float> samples), Is.False);
            Assert.That(samples.Length, Is.Zero);
        }

    }

}
