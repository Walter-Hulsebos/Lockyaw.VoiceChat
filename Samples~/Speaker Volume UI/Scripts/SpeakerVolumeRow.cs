using UnityEngine;
using UnityEngine.UI;

namespace Lockyaw.VoiceChat.SpeakerVolumeSample {

    [AddComponentMenu("Lockyaw Voice Chat/Speaker Volume UI/Speaker Volume Row")]
    [DisallowMultipleComponent]
    public sealed class SpeakerVolumeRow : MonoBehaviour {

        [Header("References")]
        [SerializeField] private Text speakerNameText;
        [SerializeField] private Slider volumeSlider;
        [SerializeField] private Image speakingIndicator;

        [Header("Speaking Indicator")]
        [SerializeField] private Color idleColor = new(.25f, .25f, .28f, 1f);
        [SerializeField] private Color speakingColor = new(.2f, .85f, .4f, 1f);

        public VoiceParticipant BoundParticipant { get; private set; }
        public SpeakerIdentity BoundIdentity { get; private set; }
        public SpeakerOutput BoundSpeakerOutput { get; private set; }

        private void Awake() {
            ConfigureVolumeSlider();
            if (volumeSlider != null) {
                volumeSlider.onValueChanged.AddListener(HandleVolumeChanged);
            }
        }

        private void OnDestroy() {
            if (volumeSlider != null) {
                volumeSlider.onValueChanged.RemoveListener(HandleVolumeChanged);
            }
            Unbind();
        }

        private void Reset() {
            speakerNameText = GetComponentInChildren<Text>(true);
            volumeSlider = GetComponentInChildren<Slider>(true);
            speakingIndicator = FindSpeakingIndicator();
            idleColor = new Color(.25f, .25f, .28f, 1f);
            speakingColor = new Color(.2f, .85f, .4f, 1f);
            ConfigureVolumeSlider();
        }

        public void Bind(VoiceParticipant participant) {
            Unbind();
            BoundParticipant = participant;
            if (BoundParticipant == null) {
                RefreshView();
                return;
            }

            BoundParticipant.TryGetComponent(out SpeakerIdentity boundIdentity);
            BoundParticipant.TryGetComponent(out SpeakerOutput boundSpeakerOutput);
            BoundIdentity = boundIdentity;
            BoundSpeakerOutput = boundSpeakerOutput;
            BoundParticipant.OnSpeaking.Listen(HandleSpeakingChanged);
            if (BoundIdentity != null) {
                BoundIdentity.OnProfileChanged.Listen(HandleProfileChanged);
            }

            if (volumeSlider != null) {
                bool hasOutput = BoundSpeakerOutput != null;
                volumeSlider.interactable = hasOutput;
                volumeSlider.SetValueWithoutNotify(hasOutput ? BoundSpeakerOutput.LocalVolume : 0f);
            }
            RefreshView();
        }

        public void Unbind() {
            if (BoundParticipant != null) {
                BoundParticipant.OnSpeaking.Unlisten(HandleSpeakingChanged);
            }
            if (BoundIdentity != null) {
                BoundIdentity.OnProfileChanged.Unlisten(HandleProfileChanged);
            }

            BoundParticipant = null;
            BoundIdentity = null;
            BoundSpeakerOutput = null;
        }

        public void SetLocalVolume(float volume) {
            float normalizedVolume = Mathf.Clamp01(volume);
            if (volumeSlider != null) {
                volumeSlider.SetValueWithoutNotify(normalizedVolume);
            }
            if (BoundSpeakerOutput != null) {
                BoundSpeakerOutput.SetLocalVolume(normalizedVolume);
            }
        }

        public void RefreshView() {
            if (speakerNameText != null) {
                speakerNameText.text = GetDisplayName();
            }
            if (speakingIndicator != null) {
                speakingIndicator.color = GetIsSpeaking() ? speakingColor : idleColor;
            }
        }

        private void HandleVolumeChanged(float volume) {
            if (BoundSpeakerOutput != null) {
                BoundSpeakerOutput.SetLocalVolume(volume);
            }
        }

        private void HandleSpeakingChanged(bool isSpeaking) {
            if (speakingIndicator != null) {
                speakingIndicator.color = isSpeaking ? speakingColor : idleColor;
            }
        }

        private void HandleProfileChanged(SpeakerProfile profile) {
            if (speakerNameText != null) {
                speakerNameText.text = profile.IsValid ? profile.ToString() : GetDisplayName();
            }
        }

        private void ConfigureVolumeSlider() {
            if (volumeSlider == null) { return; }
            volumeSlider.minValue = 0f;
            volumeSlider.maxValue = 1f;
            volumeSlider.wholeNumbers = false;
        }

        private Image FindSpeakingIndicator() {
            Image[] images = GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++) {
                if (images[i].gameObject.name == "Speaking Indicator") { return images[i]; }
            }
            return null;
        }

        private string GetDisplayName() {
            if (BoundIdentity != null && !string.IsNullOrWhiteSpace(BoundIdentity.DisplayName)) { return BoundIdentity.DisplayName; }
            if (BoundParticipant != null) { return $"Player {BoundParticipant.OwnerClientId}"; }
            return "Unavailable Player";
        }

        private bool GetIsSpeaking() {
            if (BoundIdentity != null) { return BoundIdentity.IsSpeaking; }
            return BoundParticipant != null && BoundParticipant.IsSpeaking;
        }

    }

}
