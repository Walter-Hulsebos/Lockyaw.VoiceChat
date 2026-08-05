using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat.BroadcastingSample {

    [AddComponentMenu("Lockyaw Voice Chat/Broadcast Sample Controls")]
    public sealed class BroadcastSampleControls : MonoBehaviour {

        [SerializeField, HideInInspector] private NetworkManager networkManager;

        [Header("References")]
        [SerializeField] private BroadcastButton announcementButton;

        private static readonly Rect ANNOUNCEMENT_BUTTON_RECT = new(20f, 190f, 260f, 45f);

        private bool isHoldingAnnouncement;

        private void Awake() {
            CacheComponentReferences();
        }

        private void OnDisable() {
            EndAnnouncement();
        }

        private void OnApplicationFocus(bool hasFocus) {
            if (!hasFocus) {
                EndAnnouncement();
            }
        }

        private void OnGUI() {
            GUI.Box(new Rect(10f, 10f, 280f, 245f), "Radios and Public Address");
            if (networkManager == null) {
                GUI.Label(new Rect(20f, 45f, 250f, 25f), "Network Manager is missing.");
                return;
            }

            if (!networkManager.IsListening) {
                if (GUI.Button(new Rect(20f, 45f, 80f, 35f), "Host")) {
                    networkManager.StartHost();
                }
                if (GUI.Button(new Rect(110f, 45f, 80f, 35f), "Client")) {
                    networkManager.StartClient();
                }
                if (GUI.Button(new Rect(200f, 45f, 80f, 35f), "Server")) {
                    networkManager.StartServer();
                }
            } else {
                GUI.Label(new Rect(20f, 45f, 250f, 25f), GetSessionLabel());
                if (GUI.Button(new Rect(20f, 75f, 260f, 30f), "Stop Session")) {
                    EndAnnouncement();
                    networkManager.Shutdown();
                }
            }

            GUI.Label(new Rect(20f, 120f, 250f, 25f), "Channel 7: both green walkie-talkies");
            GUI.Label(new Rect(20f, 145f, 250f, 25f), "Channel 12: isolated red walkie-talkie");
            GUI.Label(new Rect(20f, 168f, 250f, 25f), "Hold below to open PA channel 100");
            Event currentEvent = Event.current;
            bool canBroadcast = networkManager.IsClient && networkManager.IsListening;
            bool shouldBeginAnnouncement = canBroadcast &&
                                               currentEvent.rawType == EventType.MouseDown &&
                                               currentEvent.button == 0 &&
                                               ANNOUNCEMENT_BUTTON_RECT.Contains(currentEvent.mousePosition);
            bool shouldEndAnnouncement = isHoldingAnnouncement &&
                                         currentEvent.rawType == EventType.MouseUp &&
                                         currentEvent.button == 0;
            GUI.enabled = canBroadcast;
            GUI.RepeatButton(ANNOUNCEMENT_BUTTON_RECT, "Hold to Broadcast Announcement");
            GUI.enabled = true;
            if (shouldBeginAnnouncement) {
                BeginAnnouncement();
            }
            else if (shouldEndAnnouncement) {
                EndAnnouncement();
            }

            string stateLabel = announcementButton != null && announcementButton.IsBroadcasting
                ? "PA microphone is live"
                : "PA microphone is closed";
            GUI.Label(new Rect(20f, 235f, 250f, 20f), stateLabel);
        }

        private void Reset() {
            CacheComponentReferences();
        }

        private void OnValidate() {
            CacheComponentReferences();
        }

        public void BeginAnnouncement() {
            if (isHoldingAnnouncement || announcementButton == null || !announcementButton.CanRequestBroadcast) { return; }
            isHoldingAnnouncement = true;
            announcementButton.BeginBroadcast();
        }

        public void EndAnnouncement() {
            if (!isHoldingAnnouncement) { return; }
            isHoldingAnnouncement = false;
            if (announcementButton != null) {
                announcementButton.EndBroadcast();
            }
        }

        private void CacheComponentReferences() {
            if (networkManager == null) {
                TryGetComponent(out networkManager);
            }
        }

        private string GetSessionLabel() {
            if (networkManager.IsHost) { return "Session: Listen Server"; }
            if (networkManager.IsServer) { return "Session: Dedicated Server"; }
            return "Session: Client";
        }

    }

}
