using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat.Editor {

    internal static class VoiceProjectScanner {

        private static readonly ObjectPool<Dictionary<string, bool>> buildSceneStatePool = new(
            CreateBuildSceneStateDictionary,
            null,
            ClearBuildSceneStates,
            null,
            true,
            1,
            8);
        private static readonly ObjectPool<HashSet<string>> pathSetPool = new(
            CreatePathSet,
            null,
            ClearPathSet,
            null,
            true,
            4,
            32);
        private static readonly string[] sceneNameKeywords = { "game", "main", "network", "play", "core", "lobby" };

        private const int LOADED_MANAGER_SCENE_SCORE = 200;
        private const int SELECTED_MANAGER_PREFAB_SCORE = 100;
        private const int OTHER_MANAGER_PREFAB_SCORE = 45;
        private const int SELECTED_PLAYER_PREFAB_SCORE = 90;
        private const int OTHER_PLAYER_PREFAB_SCORE = 35;
        private const int ENABLED_BUILD_SCENE_SCORE = 50;
        private const int DISABLED_BUILD_SCENE_SCORE = 20;
        private const int SCENE_NAME_SCORE = 5;

        internal static void ScanNetworkManagers(List<VoiceNetworkManagerCandidate> candidates) {
            candidates.Clear();
            AddLoadedNetworkManagers(candidates);
            AddPrefabNetworkManagers(candidates);
        }

        internal static void ScanScenes(IReadOnlyList<VoiceNetworkManagerCandidate> managerCandidates, int selectedManagerIndex, GameObject selectedPlayerPrefab, List<VoiceSceneCandidate> sceneCandidates) {
            sceneCandidates.Clear();

            Dictionary<string, bool> buildSceneStates = buildSceneStatePool.Get();
            HashSet<string> scenePaths = pathSetPool.Get();
            try {
                PopulateBuildSceneStates(buildSceneStates);
                PopulateScenePaths(buildSceneStates, scenePaths);
                VoiceNetworkManagerCandidate selectedManager = GetSelectedManager(managerCandidates, selectedManagerIndex);

                foreach (string scenePath in scenePaths) {
                    VoiceSceneCandidate candidate = ScoreScene(scenePath, buildSceneStates, managerCandidates, selectedManager, selectedPlayerPrefab);
                    sceneCandidates.Add(candidate);
                }

                sceneCandidates.Sort(CompareScenes);
            }
            finally {
                pathSetPool.Release(scenePaths);
                buildSceneStatePool.Release(buildSceneStates);
            }
        }

        private static void AddLoadedNetworkManagers(List<VoiceNetworkManagerCandidate> candidates) {
            NetworkManager[] managers = Object.FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < managers.Length; i++) {
                NetworkManager manager = managers[i];
                if (manager == null || EditorUtility.IsPersistent(manager)) { continue; }

                string scenePath = manager.gameObject.scene.path;
                string sceneName = string.IsNullOrEmpty(scenePath) ? "Unsaved Scene" : Path.GetFileNameWithoutExtension(scenePath);
                string displayName = $"Loaded · {sceneName} · {manager.name}";
                candidates.Add(new VoiceNetworkManagerCandidate(manager, GetPlayerPrefab(manager), scenePath, displayName, true));
            }
        }

        private static void AddPrefabNetworkManagers(List<VoiceNetworkManagerCandidate> candidates) {
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            List<NetworkManager> managers = ListPool<NetworkManager>.Get();

            try {
                for (int i = 0; i < prefabGuids.Length; i++) {
                    string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab == null) { continue; }

                    managers.Clear();
                    prefab.GetComponentsInChildren(true, managers);
                    for (int managerIndex = 0; managerIndex < managers.Count; managerIndex++) {
                        NetworkManager manager = managers[managerIndex];
                        string displayName = $"Prefab · {prefab.name} · {manager.name}";
                        candidates.Add(new VoiceNetworkManagerCandidate(manager, GetPlayerPrefab(manager), prefabPath, displayName, false));
                    }
                }
            }
            finally {
                ListPool<NetworkManager>.Release(managers);
            }
        }

        private static VoiceSceneCandidate ScoreScene(string scenePath, IReadOnlyDictionary<string, bool> buildSceneStates, IReadOnlyList<VoiceNetworkManagerCandidate> managerCandidates, VoiceNetworkManagerCandidate selectedManager, GameObject selectedPlayerPrefab) {
            int score = 0;
            List<string> reasons = ListPool<string>.Get();
            HashSet<string> dependencies = pathSetPool.Get();
            try {
                bool isInBuildSettings = buildSceneStates.TryGetValue(scenePath, out bool isEnabledInBuild);

                if (isInBuildSettings) {
                    score += isEnabledInBuild ? ENABLED_BUILD_SCENE_SCORE : DISABLED_BUILD_SCENE_SCORE;
                    reasons.Add(isEnabledInBuild ? "enabled in Build Settings" : "listed in Build Settings");
                }

                if (selectedManager != null && selectedManager.IsLoadedScene && PathsMatch(scenePath, selectedManager.SourcePath)) {
                    score += LOADED_MANAGER_SCENE_SCORE;
                    reasons.Add("contains the selected loaded NetworkManager");
                }

                dependencies.UnionWith(AssetDatabase.GetDependencies(scenePath, true));
                AddManagerDependencyScores(dependencies, managerCandidates, selectedManager, reasons, ref score);
                AddPlayerDependencyScores(dependencies, managerCandidates, selectedPlayerPrefab, reasons, ref score);

                string sceneName = Path.GetFileNameWithoutExtension(scenePath);
                AddNameScore(sceneName, reasons, ref score);
                if (reasons.Count == 0) {
                    reasons.Add("scene asset found in the project");
                }

                SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
                string displayName = $"{sceneName} · score {score}";
                return new VoiceSceneCandidate(sceneAsset, scenePath, displayName, string.Join(", ", reasons), score, isInBuildSettings, isEnabledInBuild);
            }
            finally {
                pathSetPool.Release(dependencies);
                ListPool<string>.Release(reasons);
            }
        }

        private static void AddManagerDependencyScores(HashSet<string> dependencies, IReadOnlyList<VoiceNetworkManagerCandidate> managerCandidates, VoiceNetworkManagerCandidate selectedManager, List<string> reasons, ref int score) {
            HashSet<string> scoredManagerPaths = pathSetPool.Get();

            try {
                for (int i = 0; i < managerCandidates.Count; i++) {
                    VoiceNetworkManagerCandidate managerCandidate = managerCandidates[i];
                    if (managerCandidate.IsLoadedScene || string.IsNullOrEmpty(managerCandidate.SourcePath)) { continue; }
                    if (scoredManagerPaths.Contains(managerCandidate.SourcePath)) { continue; }
                    if (!dependencies.Contains(managerCandidate.SourcePath)) { continue; }

                    bool isSelectedManagerPath = selectedManager != null && PathsMatch(managerCandidate.SourcePath, selectedManager.SourcePath);
                    if (isSelectedManagerPath) {
                        score += SELECTED_MANAGER_PREFAB_SCORE;
                        AddReason(reasons, "depends on the selected NetworkManager prefab");
                    } else {
                        score += OTHER_MANAGER_PREFAB_SCORE;
                        AddReason(reasons, "depends on a NetworkManager prefab");
                    }

                    scoredManagerPaths.Add(managerCandidate.SourcePath);
                }
            }
            finally {
                pathSetPool.Release(scoredManagerPaths);
            }
        }

        private static void AddPlayerDependencyScores(HashSet<string> dependencies, IReadOnlyList<VoiceNetworkManagerCandidate> managerCandidates, GameObject selectedPlayerPrefab, List<string> reasons, ref int score) {
            string selectedPlayerPath = selectedPlayerPrefab == null ? string.Empty : AssetDatabase.GetAssetPath(selectedPlayerPrefab);
            HashSet<string> scoredPlayerPaths = pathSetPool.Get();

            try {
                if (!string.IsNullOrEmpty(selectedPlayerPath) && dependencies.Contains(selectedPlayerPath)) {
                    score += SELECTED_PLAYER_PREFAB_SCORE;
                    reasons.Add("depends on the selected player prefab");
                    scoredPlayerPaths.Add(selectedPlayerPath);
                }

                for (int i = 0; i < managerCandidates.Count; i++) {
                    GameObject playerPrefab = managerCandidates[i].PlayerPrefab;
                    if (playerPrefab == null) { continue; }

                    string playerPath = AssetDatabase.GetAssetPath(playerPrefab);
                    if (string.IsNullOrEmpty(playerPath) || scoredPlayerPaths.Contains(playerPath) || !dependencies.Contains(playerPath)) { continue; }

                    score += OTHER_PLAYER_PREFAB_SCORE;
                    AddReason(reasons, "depends on a configured player prefab");
                    scoredPlayerPaths.Add(playerPath);
                }
            }
            finally {
                pathSetPool.Release(scoredPlayerPaths);
            }
        }

        private static void AddNameScore(string sceneName, List<string> reasons, ref int score) {
            for (int i = 0; i < sceneNameKeywords.Length; i++) {
                if (sceneName.IndexOf(sceneNameKeywords[i], StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                score += SCENE_NAME_SCORE;
                reasons.Add("name suggests a playable scene");
                return;
            }
        }

        private static void AddReason(List<string> reasons, string reason) {
            if (reasons.Contains(reason)) { return; }
            reasons.Add(reason);
        }

        private static void PopulateBuildSceneStates(Dictionary<string, bool> buildSceneStates) {
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

            for (int i = 0; i < buildScenes.Length; i++) {
                EditorBuildSettingsScene buildScene = buildScenes[i];
                if (string.IsNullOrEmpty(buildScene.path)) { continue; }
                buildSceneStates[buildScene.path] = buildScene.enabled;
            }
        }

        private static void PopulateScenePaths(IReadOnlyDictionary<string, bool> buildSceneStates, HashSet<string> scenePaths) {
            scenePaths.UnionWith(buildSceneStates.Keys);
            string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");

            for (int i = 0; i < sceneGuids.Length; i++) {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuids[i]);
                if (string.IsNullOrEmpty(scenePath)) { continue; }
                scenePaths.Add(scenePath);
            }
        }

        private static void ClearBuildSceneStates(Dictionary<string, bool> buildSceneStates) {
            buildSceneStates.Clear();
        }

        private static void ClearPathSet(HashSet<string> paths) {
            paths.Clear();
        }

        private static VoiceNetworkManagerCandidate GetSelectedManager(IReadOnlyList<VoiceNetworkManagerCandidate> managerCandidates, int selectedManagerIndex) {
            if (selectedManagerIndex < 0 || selectedManagerIndex >= managerCandidates.Count) { return null; }
            return managerCandidates[selectedManagerIndex];
        }

        private static GameObject GetPlayerPrefab(NetworkManager manager) {
            if (manager.NetworkConfig == null) { return null; }
            return manager.NetworkConfig.PlayerPrefab;
        }

        private static int CompareScenes(VoiceSceneCandidate left, VoiceSceneCandidate right) {
            int scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0) { return scoreComparison; }
            return string.Compare(left.ScenePath, right.ScenePath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsMatch(string leftPath, string rightPath) => string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, bool> CreateBuildSceneStateDictionary() => new(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> CreatePathSet() => new(StringComparer.OrdinalIgnoreCase);

    }

}
