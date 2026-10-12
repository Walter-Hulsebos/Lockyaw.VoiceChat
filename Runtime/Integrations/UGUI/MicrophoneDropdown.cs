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
        private bool hasExplicitBinding;

        private void Awake() {
            hasExplicitBinding = microphoneInput != null;
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
            hasExplicitBinding = microphoneInput != null;
            RefreshDevices();
        }

        public void RefreshDevices() {
            nextRefreshAt = Time.unscaledTime + refreshInterval;
            if (dropdown == null) { return; }
            ResolveMicrophone();

            MicrophoneDevices.Refresh();
            VoiceDeviceId requestedDevice = microphoneInput == null ? VoiceDeviceId.Default : microphoneInput.DeviceId;
            bool haveOptionsChanged = options.Refresh(MicrophoneDevices.GetAvailableDevices(), requestedDevice);
            dropdown.interactable = microphoneInput != null && (!microphoneInput.IsSpawned || microphoneInput.IsOwner);
            if (!haveOptionsChanged) { return; }

            dropdown.Hide();
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(options.Labels));
            dropdown.SetValueWithoutNotify(options.SelectedIndex);
            dropdown.RefreshShownValue();
        }

        private void HandleDeviceChanged(int optionIndex) {
            if (microphoneInput == null || !dropdown.interactable || !options.TryGetDeviceId(optionIndex, out VoiceDeviceId deviceId)) { return; }
            microphoneInput.SetInputDevice(deviceId);
            RefreshDevices();
        }

        private void CacheDropdown() {
            if (dropdown == null) {
                TryGetComponent(out dropdown);
            }
        }

        private void ResolveMicrophone() {
            if (hasExplicitBinding) { return; }
            if (microphoneInput != null && microphoneInput.IsSpawned && microphoneInput.IsClient && microphoneInput.IsOwner && microphoneInput.isActiveAndEnabled) { return; }
            microphoneInput = null;
            MicrophoneInput candidate = null;
            IReadOnlyList<VoiceParticipant> participants = VoiceParticipant.SpawnedParticipants;
            for (int i = 0; i < participants.Count; i++) {
                VoiceParticipant participant = participants[i];
                if (participant == null || !participant.IsSpawned || !participant.IsClient || !participant.IsOwner || !participant.TryGetComponent(out MicrophoneInput localMicrophone)) { continue; }
                if (candidate != null) { return; }
                candidate = localMicrophone;
            }
            microphoneInput = candidate;
        }

    }

}
