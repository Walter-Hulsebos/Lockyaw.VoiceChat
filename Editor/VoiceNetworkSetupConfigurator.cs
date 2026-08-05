using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Pool;

namespace Lockyaw.VoiceChat.Editor {

    internal static class VoiceNetworkSetupConfigurator {

        private const string UNDO_ASSIGNMENT_NAME = "Assign Voice Chat Player Prefab";

        internal static VoiceSetupResult AssignPlayerPrefab(VoiceNetworkManagerCandidate managerCandidate, GameObject playerPrefab) {
            if (managerCandidate == null || managerCandidate.Manager == null) {
                return VoiceSetupResult.Failure("Select a valid NetworkManager first.");
            }
            if (playerPrefab == null) {
                return VoiceSetupResult.Failure("Select a player prefab first.");
            }
            if (managerCandidate.Manager.NetworkConfig == null) {
                return VoiceSetupResult.Failure("The selected NetworkManager has no NetworkConfig.");
            }
            if (managerCandidate.Manager.IsListening) {
                return VoiceSetupResult.Failure("Stop the running network session before changing its player prefab.");
            }
            if (managerCandidate.Manager.NetworkConfig.PlayerPrefab == playerPrefab) {
                return VoiceSetupResult.Success($"{playerPrefab.name} is already assigned as the network player prefab.");
            }

            return managerCandidate.IsLoadedScene
                ? AssignLoadedManager(managerCandidate.Manager, playerPrefab)
                : AssignPrefabManager(managerCandidate, playerPrefab);
        }

        private static VoiceSetupResult AssignLoadedManager(NetworkManager manager, GameObject playerPrefab) {
            Undo.RecordObject(manager, UNDO_ASSIGNMENT_NAME);
            manager.NetworkConfig.PlayerPrefab = playerPrefab;
            EditorUtility.SetDirty(manager);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);

            string sceneName = string.IsNullOrEmpty(manager.gameObject.scene.name) ? "the open scene" : manager.gameObject.scene.name;
            return VoiceSetupResult.Success($"{playerPrefab.name} is now the network player prefab. Save {sceneName} to keep the assignment.");
        }

        private static VoiceSetupResult AssignPrefabManager(VoiceNetworkManagerCandidate managerCandidate, GameObject playerPrefab) {
            string prefabPath = managerCandidate.SourcePath;
            if (string.IsNullOrEmpty(prefabPath) || !AssetDatabase.IsOpenForEdit(prefabPath, StatusQueryOptions.UseCachedIfPossible)) {
                return VoiceSetupResult.Failure("The selected NetworkManager prefab is read-only.");
            }

            NetworkManager sourceManager = managerCandidate.Manager;
            Transform sourceRoot = sourceManager.transform.root;
            string managerPath = AnimationUtility.CalculateTransformPath(sourceManager.transform, sourceRoot);
            int managerIndex = GetManagerIndex(sourceManager);
            GameObject prefabRoot = null;

            try {
                prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                NetworkManager manager = FindManager(prefabRoot, managerPath, managerIndex);
                if (manager == null || manager.NetworkConfig == null) {
                    return VoiceSetupResult.Failure("Unity could not find the selected NetworkManager inside its prefab.");
                }

                Undo.RegisterFullObjectHierarchyUndo(prefabRoot, UNDO_ASSIGNMENT_NAME);
                manager.NetworkConfig.PlayerPrefab = playerPrefab;
                EditorUtility.SetDirty(manager);

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath, out bool savedSuccessfully);
                return savedSuccessfully
                    ? VoiceSetupResult.Success($"{playerPrefab.name} is now the network player prefab in {prefabRoot.name}.")
                    : VoiceSetupResult.Failure("Unity could not save the NetworkManager prefab assignment.");
            }
            catch (Exception exception) {
                Debug.LogException(exception);
                return VoiceSetupResult.Failure($"The network player prefab could not be assigned: {exception.Message}");
            }
            finally {
                if (prefabRoot != null) {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }
        }

        private static NetworkManager FindManager(GameObject prefabRoot, string managerPath, int managerIndex) {
            if (prefabRoot == null || managerIndex < 0) { return null; }

            Transform managerTransform = string.IsNullOrEmpty(managerPath) ? prefabRoot.transform : prefabRoot.transform.Find(managerPath);
            if (managerTransform == null) { return null; }

            List<NetworkManager> managers = ListPool<NetworkManager>.Get();
            try {
                managerTransform.GetComponents(managers);
                return managerIndex < managers.Count ? managers[managerIndex] : null;
            }
            finally {
                ListPool<NetworkManager>.Release(managers);
            }
        }

        private static int GetManagerIndex(NetworkManager sourceManager) {
            List<NetworkManager> managers = ListPool<NetworkManager>.Get();
            try {
                sourceManager.GetComponents(managers);
                return managers.IndexOf(sourceManager);
            }
            finally {
                ListPool<NetworkManager>.Release(managers);
            }
        }

    }

}
