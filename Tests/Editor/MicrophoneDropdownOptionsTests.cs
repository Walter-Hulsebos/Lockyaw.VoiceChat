using System;
using NUnit.Framework;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class MicrophoneDropdownOptionsTests {

        [Test]
        public void NoMicrophoneShowsDisabledPlaceholderWithoutLosingRequestedSelection() {
            MicrophoneDropdownOptions options = new();
            Assert.That(options.Refresh(Array.Empty<string>(), "Headset"), Is.True);
            Assert.That(options.HasDevices, Is.False);
            Assert.That(options.Labels, Is.EqualTo(new[] { "No microphone available" }));
            Assert.That(options.SelectedIndex, Is.Zero);
            Assert.That(options.TryGetDeviceName(0, out string _), Is.False);

            options.Refresh(new[] { "Desk", "Headset" }, "Headset");
            Assert.That(options.HasDevices, Is.True);
            Assert.That(options.SelectedIndex, Is.EqualTo(2));
            Assert.That(options.TryGetDeviceName(2, out string deviceName), Is.True);
            Assert.That(deviceName, Is.EqualTo("Headset"));
        }

        [Test]
        public void MissingSelectedDeviceCanBeReplacedByAutomaticOrAnotherDevice() {
            MicrophoneDropdownOptions options = new();
            options.Refresh(new[] { "Desk" }, "Headset");
            Assert.That(options.Labels, Is.EqualTo(new[] { "Automatic", "Headset (unavailable)", "Desk" }));
            Assert.That(options.SelectedIndex, Is.EqualTo(1));
            Assert.That(options.TryGetDeviceName(1, out string _), Is.False);
            Assert.That(options.TryGetDeviceName(0, out string automaticDeviceName), Is.True);
            Assert.That(automaticDeviceName, Is.Empty);
            Assert.That(options.TryGetDeviceName(2, out string explicitDeviceName), Is.True);
            Assert.That(explicitDeviceName, Is.EqualTo("Desk"));
            Assert.That(options.TryGetDeviceName(-1, out string _), Is.False);
            Assert.That(options.TryGetDeviceName(3, out string _), Is.False);
        }

        [Test]
        public void ReorderingDevicesPreservesSelectionByName() {
            MicrophoneDropdownOptions options = new();
            options.Refresh(new[] { "Desk", "Headset" }, "Headset");
            Assert.That(options.SelectedIndex, Is.EqualTo(2));
            options.Refresh(new[] { "Headset", "Desk" }, "Headset");
            Assert.That(options.SelectedIndex, Is.EqualTo(1));
        }

        [Test]
        public void UnchangedDeviceListDoesNotRebuildAnOpenDropdown() {
            MicrophoneDropdownOptions options = new();
            Assert.That(options.Refresh(new[] { "Desk" }, ""), Is.True);
            Assert.That(options.Refresh(new[] { "Desk" }, null), Is.False);
            Assert.That(options.Refresh(new[] { "Desk" }, "Desk"), Is.True);
            Assert.That(options.SelectedIndex, Is.EqualTo(1));
        }

    }

}
