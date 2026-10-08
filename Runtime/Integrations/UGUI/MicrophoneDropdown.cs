using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lockyaw.VoiceChat.UGUI {

    [AddComponentMenu("Lockyaw/Voice Chat/UI/Microphone Dropdown")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Dropdown))]
    public sealed class MicrophoneDropdown : MonoBehaviour {

        [Header("References")]
        [SerializeField] private TMP_Dropdown dropdown;
        [SerializeField] private MicrophoneInput microphoneInput;

        [Header("Devices")]
        [SerializeField, Min(.25f)] private float refreshInterval = 1f;

        private readonly MicrophoneDropdownOptions options = new();
        private float nextRefreshAt;

        private void Awake() {
            CacheDropdown();
        }

        private void OnEnable() {
            CacheDropdown();
            if (dropdown == null) { return; }
            dropdown.onValueChanged.AddListener(HandleDeviceChanged);
            RefreshDevices();
        }

        private void OnDisable() {
            if (dropdown == null) { return; }
            dropdown.onValueChanged.RemoveListener(HandleDeviceChanged);
        }

        private void Update() {
            if (Time.unscaledTime < nextRefreshAt) { return; }
            RefreshDevices();
        }

        private void Reset() {
            CacheDropdown();
        }

        private void OnValidate() {
            CacheDropdown();
            refreshInterval = Mathf.Max(.25f, refreshInterval);
        }

        public void Bind(MicrophoneInput microphoneInput) {
            this.microphoneInput = microphoneInput;
            RefreshDevices();
        }

        public void RefreshDevices() {
            nextRefreshAt = Time.unscaledTime + refreshInterval;
            if (dropdown == null) { return; }
            ResolveMicrophone();

            string requestedDeviceName = microphoneInput == null ? "" : microphoneInput.RequestedDeviceName;
            bool haveOptionsChanged = options.Refresh(MicrophoneDevices.GetAvailableDevices(), requestedDeviceName);
            dropdown.interactable = options.HasDevices && microphoneInput != null && (!microphoneInput.IsSpawned || microphoneInput.IsOwner);
            if (!haveOptionsChanged) { return; }

            dropdown.Hide();
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(options.Labels));
            dropdown.SetValueWithoutNotify(options.SelectedIndex);
            dropdown.RefreshShownValue();
        }

        private void HandleDeviceChanged(int optionIndex) {
            if (microphoneInput == null || !dropdown.interactable || !options.TryGetDeviceName(optionIndex, out string deviceName)) { return; }
            microphoneInput.SetDevice(deviceName);
            RefreshDevices();
        }

        private void CacheDropdown() {
            if (dropdown == null) {
                TryGetComponent(out dropdown);
            }
        }

        private void ResolveMicrophone() {
            if (microphoneInput == null && VoiceParticipant.TryGetLocal(out VoiceParticipant participant)) {
                participant.TryGetComponent(out microphoneInput);
            }
        }

    }

}
