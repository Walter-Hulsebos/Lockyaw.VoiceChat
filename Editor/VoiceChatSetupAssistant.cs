using System;
using System.Collections.Generic;
using UnityEditor;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    internal sealed class VoiceChatSetupAssistant : EditorWindow {

        [SerializeField] private int selectedManagerIndex = -1;
        [SerializeField] private int selectedSceneIndex = -1;
        [SerializeField] private GameObject selectedPlayerPrefab;
        [SerializeField] private Vector2 scrollPosition;

        private const float MIN_WINDOW_WIDTH = 470f;
        private const float MIN_WINDOW_HEIGHT = 580f;
        private const string STEAM_NETWORKING_SOCKETS_TRANSPORT_TYPE_NAME = "Netcode.Transports.SteamNetworkingSocketsTransport";

        private readonly List<VoiceNetworkManagerCandidate> managerCandidates = new();
        private readonly List<VoiceSceneCandidate> sceneCandidates = new();

        private AndroidMicrophonePermissionSupport.PermissionStatus androidPermissionStatus;
        private string statusMessage = string.Empty;
        private MessageType statusMessageType = MessageType.Info;

        private void OnEnable() {
            titleContent = new GUIContent("Voice Chat Setup");
            minSize = new Vector2(MIN_WINDOW_WIDTH, MIN_WINDOW_HEIGHT);
            RefreshCandidates();
        }

        private void OnGUI() {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField("Voice Chat Setup Assistant", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox("Choose the detected network setup, confirm the player prefab, then configure it. Discovery never changes scenes, prefabs, or project settings.", MessageType.Info);
            DrawDetectedProjectIssue();

            EditorGUILayout.Space();
            DrawNetworkManagerSection();
            EditorGUILayout.Space();
            DrawSceneSection();
            EditorGUILayout.Space();
            DrawPlayerSection();
            EditorGUILayout.Space();
            DrawSteamAudioSection();
            EditorGUILayout.Space();
            DrawStatus();

            EditorGUILayout.EndScrollView();
        }

        [MenuItem("Tools/Lockyaw/Voice Chat/Setup Assistant")]
        private static void OpenWindow() {
            GetWindow<VoiceChatSetupAssistant>();
        }

        private void HandleUseSteamAudioClicked() {
            bool confirmed = EditorUtility.DisplayDialog(
                "Use Steam Audio Spatializer",
                "This changes the spatializer plug-in in the project audio settings.",
                "Use Steam Audio",
                "Cancel"
            );
            if (!confirmed) { return; }

            SetStatus(SteamAudioSupport.SelectSpatializer());
        }

        private void HandleConfigurePlayerClicked() {
            VoiceSetupResult prefabResult = VoicePrefabConfigurator.ConfigurePlayerPrefab(selectedPlayerPrefab);
            if (!prefabResult.Succeeded) {
                SetStatus(prefabResult);
                return;
            }

            VoiceNetworkManagerCandidate managerCandidate = GetSelectedManagerCandidate();
            if (managerCandidate == null || managerCandidate.PlayerPrefab == selectedPlayerPrefab) {
                SetStatus(prefabResult);
                return;
            }

            VoiceSetupResult assignmentResult = VoiceNetworkSetupConfigurator.AssignPlayerPrefab(managerCandidate, selectedPlayerPrefab);
            if (!assignmentResult.Succeeded) {
                SetStatus(VoiceSetupResult.Failure($"{prefabResult.Message} {assignmentResult.Message}"));
                return;
            }

            RefreshCandidates();
            SetStatus(VoiceSetupResult.Success($"{prefabResult.Message} {assignmentResult.Message}"));
        }

        private void RefreshCandidates() {
            GameObject previousPlayerPrefab = selectedPlayerPrefab;
            VoiceProjectScanner.ScanNetworkManagers(managerCandidates);
            selectedManagerIndex = ClampIndex(selectedManagerIndex, managerCandidates.Count);

            if (previousPlayerPrefab != null) {
                selectedPlayerPrefab = previousPlayerPrefab;
            } else {
                ApplySelectedManagerPrefab();
            }

            RefreshSceneCandidates();
            androidPermissionStatus = AndroidMicrophonePermissionSupport.GetStatus();
            Repaint();
        }

        private void RefreshSceneCandidates() {
            VoiceProjectScanner.ScanScenes(managerCandidates, selectedManagerIndex, selectedPlayerPrefab, sceneCandidates);
            selectedSceneIndex = ClampIndex(selectedSceneIndex, sceneCandidates.Count);
        }

        private void ApplySelectedManagerPrefab() {
            if (selectedManagerIndex < 0 || selectedManagerIndex >= managerCandidates.Count) { return; }

            selectedPlayerPrefab = managerCandidates[selectedManagerIndex].PlayerPrefab;
        }

        private void DrawNetworkManagerSection() {
            EditorGUILayout.LabelField("1. Network Setup", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"Found {managerCandidates.Count} NetworkManager candidate{GetPluralSuffix(managerCandidates.Count)}.");
                if (GUILayout.Button("Refresh", GUILayout.Width(90f))) {
                    RefreshCandidates();
                }
            }

            if (managerCandidates.Count == 0) {
                EditorGUILayout.HelpBox("No loaded or prefab NetworkManager was found. You can still select a player prefab below.", MessageType.Warning);
                return;
            }

            string[] managerNames = GetManagerNames();
            int newManagerIndex = EditorGUILayout.Popup("Network Manager", selectedManagerIndex, managerNames);
            if (newManagerIndex != selectedManagerIndex) {
                selectedManagerIndex = newManagerIndex;
                ApplySelectedManagerPrefab();
                RefreshSceneCandidates();
            }

            VoiceNetworkManagerCandidate managerCandidate = managerCandidates[selectedManagerIndex];
            NetworkTransport networkTransport = managerCandidate.Manager.NetworkConfig.NetworkTransport;
            using (new EditorGUI.DisabledScope(true)) {
                EditorGUILayout.ObjectField("Detected Component", managerCandidate.Manager, typeof(NetworkManager), true);
                EditorGUILayout.TextField("Network Topology", GetTopologyName(managerCandidate.Manager.NetworkConfig.NetworkTopology));
                EditorGUILayout.ObjectField("Network Transport", networkTransport, typeof(NetworkTransport), true);
            }

            EditorGUILayout.SelectableLabel(managerCandidate.SourcePath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.HelpBox(GetTopologySummary(managerCandidate.Manager.NetworkConfig.NetworkTopology), MessageType.None);
            EditorGUILayout.HelpBox(GetTransportSummary(networkTransport), networkTransport == null ? MessageType.Warning : MessageType.None);

            if (managerCandidate.PlayerPrefab == null) {
                EditorGUILayout.HelpBox("This NetworkManager has no configured player prefab. Select one below.", MessageType.Warning);
            }
        }

        private void DrawSceneSection() {
            EditorGUILayout.LabelField("2. Likely Integration Scene", EditorStyles.boldLabel);

            if (sceneCandidates.Count == 0) {
                EditorGUILayout.HelpBox("No scene assets were found.", MessageType.Warning);
                return;
            }

            string[] sceneNames = GetSceneNames();
            selectedSceneIndex = EditorGUILayout.Popup("Scene", selectedSceneIndex, sceneNames);
            VoiceSceneCandidate sceneCandidate = sceneCandidates[selectedSceneIndex];

            using (new EditorGUI.DisabledScope(true)) {
                EditorGUILayout.ObjectField("Scene Asset", sceneCandidate.SceneAsset, typeof(SceneAsset), false);
            }

            EditorGUILayout.HelpBox(sceneCandidate.Reason, MessageType.None);

            if (sceneCandidate.SceneAsset != null && GUILayout.Button("Show Scene in Project")) {
                Selection.activeObject = sceneCandidate.SceneAsset;
                EditorGUIUtility.PingObject(sceneCandidate.SceneAsset);
            }
        }

        private void DrawPlayerSection() {
            EditorGUILayout.LabelField("3. Player Prefab", EditorStyles.boldLabel);

            GameObject newPlayerPrefab = (GameObject)EditorGUILayout.ObjectField("Player Prefab", selectedPlayerPrefab, typeof(GameObject), false);
            if (newPlayerPrefab != selectedPlayerPrefab) {
                selectedPlayerPrefab = newPlayerPrefab;
                RefreshSceneCandidates();
            }

            VoiceNetworkManagerCandidate managerCandidate = GetSelectedManagerCandidate();
            bool needsNetworkAssignment = managerCandidate != null && managerCandidate.PlayerPrefab != selectedPlayerPrefab;
            string buttonLabel = needsNetworkAssignment ? "Configure and Assign Player Prefab" : "Configure Player Prefab";

            EditorGUILayout.HelpBox("Configuration adds identity, conversation, microphone, and speaker components, then assigns a dedicated AudioSource at the humanoid head when available. When a NetworkManager is selected, this also assigns the configured prefab as its player.", MessageType.Info);

            using (new EditorGUI.DisabledScope(selectedPlayerPrefab == null)) {
                if (GUILayout.Button(buttonLabel, GUILayout.Height(28f))) {
                    HandleConfigurePlayerClicked();
                }
            }
        }

        private void DrawSteamAudioSection() {
            EditorGUILayout.LabelField("4. Steam Audio (Optional)", EditorStyles.boldLabel);

            bool isSpatializerAvailable = SteamAudioSupport.IsSpatializerAvailable();
            bool isSpatializerSelected = SteamAudioSupport.IsSpatializerSelected();
            Type steamAudioSourceType = SteamAudioSupport.GetSteamAudioSourceType();

            EditorGUILayout.LabelField("Current Spatializer", SteamAudioSupport.GetSelectedSpatializerName());

            if (!isSpatializerAvailable) {
                EditorGUILayout.HelpBox("Steam Audio was not detected for the current editor platform. The configured AudioSource still supports Unity spatializers.", MessageType.Info);
            } else if (isSpatializerSelected) {
                EditorGUILayout.HelpBox("Steam Audio is the active project spatializer.", MessageType.Info);
            } else if (GUILayout.Button("Use Steam Audio Spatializer")) {
                HandleUseSteamAudioClicked();
            }

            if (steamAudioSourceType == null) {
                EditorGUILayout.HelpBox("Steam Audio Source is not installed. Baseline HRTF spatialization only needs Steam Audio selected above.", MessageType.None);
                return;
            }

            EditorGUILayout.HelpBox("Steam Audio Source is optional and enables its additional source controls on the dedicated voice playback object.", MessageType.None);

            using (new EditorGUI.DisabledScope(selectedPlayerPrefab == null || !isSpatializerSelected)) {
                if (GUILayout.Button("Add Steam Audio Source to Player Prefab")) {
                    SetStatus(VoicePrefabConfigurator.AddSteamAudioSource(selectedPlayerPrefab));
                }
            }
        }

        private void DrawStatus() {
            if (string.IsNullOrEmpty(statusMessage)) { return; }
            EditorGUILayout.HelpBox(statusMessage, statusMessageType);
        }

        private void DrawDetectedProjectIssue() {
            if (androidPermissionStatus.MessageType != MessageType.Error) { return; }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(androidPermissionStatus.Message, androidPermissionStatus.MessageType);
        }

        private void SetStatus(VoiceSetupResult result) {
            statusMessage = result.Message;
            statusMessageType = result.Succeeded ? MessageType.Info : MessageType.Error;
            Repaint();
        }

        private VoiceNetworkManagerCandidate GetSelectedManagerCandidate() {
            if (selectedManagerIndex < 0 || selectedManagerIndex >= managerCandidates.Count) { return null; }
            return managerCandidates[selectedManagerIndex];
        }

        private string[] GetManagerNames() {
            string[] managerNames = new string[managerCandidates.Count];

            for (int i = 0; i < managerCandidates.Count; i++) {
                managerNames[i] = managerCandidates[i].DisplayName;
            }

            return managerNames;
        }

        private string[] GetSceneNames() {
            string[] sceneNames = new string[sceneCandidates.Count];

            for (int i = 0; i < sceneCandidates.Count; i++) {
                sceneNames[i] = sceneCandidates[i].DisplayName;
            }

            return sceneNames;
        }

        private static string GetTopologyName(NetworkTopologyTypes topology) {
            return topology == NetworkTopologyTypes.DistributedAuthority
                ? "Distributed Authority"
                : "Client-Server";
        }

        private static string GetTopologySummary(NetworkTopologyTypes topology) {
            return topology == NetworkTopologyTypes.DistributedAuthority
                ? "Each voice participant's owning client validates and relays its frames to the other observers."
                : "The server validates voice frames and relays them to observing clients. This is the primary setup.";
        }

        private static string GetTransportSummary(NetworkTransport networkTransport) {
            if (networkTransport == null) {
                return "Assign an NGO NetworkTransport before starting a network session.";
            }

            Type transportType = networkTransport.GetType();
            if (transportType.FullName == STEAM_NETWORKING_SOCKETS_TRANSPORT_TYPE_NAME) {
                return "Steam Networking Sockets detected. Initialize Steamworks before starting NGO, and set the server Steam ID on clients.";
            }

            return $"Voice traffic will use {transportType.Name} through NGO.";
        }

        private static int ClampIndex(int index, int count) {
            if (count == 0) { return -1; }
            return Mathf.Clamp(index, 0, count - 1);
        }

        private static string GetPluralSuffix(int count) => count == 1 ? string.Empty : "s";

    }

}
