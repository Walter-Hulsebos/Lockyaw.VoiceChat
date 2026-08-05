using UnityEngine;
using UnityEngine.UIElements;

namespace Lockyaw.VoiceChat.SpeakerVolumeToolkitSample {

    [AddComponentMenu("Lockyaw Voice Chat/Speaker Volume UI Toolkit")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UIDocument))]
    public sealed class SpeakerVolumeDocument : MonoBehaviour {

        [Header("References")]
        [SerializeField, HideInInspector] private UIDocument document;
        [SerializeField] private VisualTreeAsset speakerTemplate;

        [Header("Speakers")]
        [SerializeField] private bool includeLocalSpeaker;
        [SerializeField, Min(.05f)] private float refreshInterval = .25f;

        private SpeakerVolumeListElement speakerList;
        private Label emptyStateLabel;
        private float nextRefreshAt;

        private void Awake() {
            CacheComponentReferences();
        }

        private void OnEnable() {
            BuildPanel();
        }

        private void OnDisable() {
            DisposePanel();
        }

        private void Update() {
            if (speakerList == null) { return; }

            speakerList.RefreshVisuals();
            if (Time.unscaledTime < nextRefreshAt) { return; }

            RefreshSpeakers();
        }

        private void Reset() {
            CacheComponentReferences();
            includeLocalSpeaker = false;
            refreshInterval = .25f;
        }

        private void OnValidate() {
            CacheComponentReferences();
            refreshInterval = Mathf.Max(.05f, refreshInterval);
        }

        public void RefreshSpeakers() {
            if (speakerList == null) { return; }

            speakerList.SynchronizeSpeakers(includeLocalSpeaker);
            nextRefreshAt = Time.unscaledTime + refreshInterval;
            RefreshEmptyState();
        }

        private void BuildPanel() {
            DisposePanel();
            if (document == null) {
                Debug.LogError("Speaker Volume UI Toolkit needs a UIDocument.", this);
                return;
            }
            if (speakerTemplate == null) {
                Debug.LogError("Speaker Volume UI Toolkit needs a speaker template.", this);
                return;
            }

            VisualElement root = document.rootVisualElement;
            VisualElement listHost = root.Q<VisualElement>("speaker-volume-list-host");
            emptyStateLabel = root.Q<Label>("speaker-volume-empty-state");
            if (listHost == null || emptyStateLabel == null) {
                Debug.LogError("The assigned visual tree is not the Speaker Volume UI Toolkit layout.", this);
                return;
            }

            speakerList = new SpeakerVolumeListElement(speakerTemplate);
            listHost.Add(speakerList);
            RefreshSpeakers();
        }

        private void DisposePanel() {
            if (speakerList != null) {
                speakerList.Dispose();
                speakerList.RemoveFromHierarchy();
                speakerList = null;
            }
            emptyStateLabel = null;
        }

        private void RefreshEmptyState() {
            if (emptyStateLabel == null || speakerList == null) { return; }
            emptyStateLabel.style.display = speakerList.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void CacheComponentReferences() {
            if (document == null) {
                TryGetComponent(out document);
            }
        }

    }

}
