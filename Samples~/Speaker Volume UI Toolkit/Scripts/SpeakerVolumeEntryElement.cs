using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lockyaw.VoiceChat.SpeakerVolumeToolkitSample {

    public sealed class SpeakerVolumeEntryElement : VisualElement, IDisposable {

        public SpeakerIdentity Identity => identity;
        public VoiceParticipant Participant => participant;

        private readonly VisualElement entryRoot;
        private readonly VisualElement speakingIndicator;
        private readonly Label speakerNameLabel;
        private readonly Label volumeLabel;
        private readonly Slider volumeSlider;
        private SpeakerIdentity identity;
        private VoiceParticipant participant;
        private SpeakerOutput speakerOutput;

        public SpeakerVolumeEntryElement(VisualTreeAsset speakerTemplate) {
            AddToClassList("speaker-volume-entry-host");
            speakerTemplate.CloneTree(this);

            entryRoot = this.Q<VisualElement>("speaker-volume-entry");
            speakingIndicator = this.Q<VisualElement>("speaker-speaking-indicator");
            speakerNameLabel = this.Q<Label>("speaker-name");
            volumeLabel = this.Q<Label>("speaker-volume-value");
            volumeSlider = this.Q<Slider>("speaker-volume-slider");
            if (entryRoot == null || speakingIndicator == null || speakerNameLabel == null || volumeLabel == null || volumeSlider == null) {
                Debug.LogError("The assigned speaker template is not the Speaker Volume Entry layout.");
                return;
            }

            volumeSlider.RegisterValueChangedCallback(HandleVolumeChanged);
        }

        public void Bind(VoiceParticipant newParticipant) {
            Unbind();
            participant = newParticipant;
            if (participant == null) { return; }

            participant.TryGetComponent(out identity);
            participant.TryGetComponent(out speakerOutput);
            participant.OnSpeaking.Listen(HandleSpeakingChanged);
            if (identity != null) {
                identity.OnProfileChanged.Listen(HandleProfileChanged);
            }
            RefreshVisuals();
        }

        public void RefreshVisuals() {
            if (participant == null || entryRoot == null) { return; }

            speakerNameLabel.text = GetSpeakerName();
            bool isSpeaking = identity == null ? participant.IsSpeaking : identity.IsSpeaking;
            entryRoot.EnableInClassList("speaker-volume-entry--speaking", isSpeaking);
            float voiceLevel = identity == null ? 1f : identity.VoiceLevel;
            speakingIndicator.style.opacity = isSpeaking ? Mathf.Lerp(.55f, 1f, voiceLevel) : .35f;

            if (speakerOutput == null) {
                volumeSlider.SetEnabled(false);
                volumeLabel.text = "Unavailable";
                return;
            }

            float localVolume = speakerOutput.LocalVolume;
            volumeSlider.SetEnabled(true);
            volumeSlider.SetValueWithoutNotify(localVolume);
            volumeLabel.text = $"{Mathf.RoundToInt(localVolume * 100f)}%";
        }

        public void Dispose() {
            Unbind();
            if (volumeSlider != null) {
                volumeSlider.UnregisterValueChangedCallback(HandleVolumeChanged);
            }
        }

        private void HandleVolumeChanged(ChangeEvent<float> e) {
            if (speakerOutput == null) { return; }
            speakerOutput.SetLocalVolume(e.newValue);
            volumeLabel.text = $"{Mathf.RoundToInt(speakerOutput.LocalVolume * 100f)}%";
        }

        private void HandleProfileChanged(SpeakerProfile profile) {
            if (speakerNameLabel != null) {
                speakerNameLabel.text = GetSpeakerName();
            }
        }

        private void HandleSpeakingChanged(bool isSpeaking) {
            if (entryRoot == null) { return; }
            entryRoot.EnableInClassList("speaker-volume-entry--speaking", isSpeaking);
        }

        private void Unbind() {
            if (identity != null) {
                identity.OnProfileChanged.Unlisten(HandleProfileChanged);
            }
            if (participant != null) {
                participant.OnSpeaking.Unlisten(HandleSpeakingChanged);
            }
            identity = null;
            participant = null;
            speakerOutput = null;
        }

        private string GetSpeakerName() {
            if (identity != null && !string.IsNullOrWhiteSpace(identity.DisplayName)) { return identity.DisplayName; }
            return participant == null ? "Unknown Player" : $"Player {participant.OwnerClientId}";
        }

    }

}
