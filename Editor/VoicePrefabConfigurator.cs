using System;
using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    internal static class VoicePrefabConfigurator {

        private const string UNDO_CONFIGURATION_NAME = "Configure Voice Chat Player";
        private const string UNDO_STEAM_AUDIO_NAME = "Add Steam Audio Source";

        internal static VoiceSetupResult ConfigurePlayerPrefab(GameObject playerPrefab) {
            if (!TryGetEditablePrefabPath(playerPrefab, out string prefabPath, out string failureMessage)) {
                return VoiceSetupResult.Failure(failureMessage);
            }

            GameObject prefabRoot = null;
            int undoGroup = BeginUndoGroup(UNDO_CONFIGURATION_NAME);

            try {
                prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                if (prefabRoot == null) {
                    return VoiceSetupResult.Failure("Unity could not load the selected player prefab.");
                }

                Undo.RegisterFullObjectHierarchyUndo(prefabRoot, UNDO_CONFIGURATION_NAME);
                VoiceParticipant participant = VoiceComponentSetup.EnsureParticipant(prefabRoot);
                if (!participant.TryGetComponent(out SpeakerIdentity speakerIdentity)) {
                    speakerIdentity = Undo.AddComponent<SpeakerIdentity>(participant.gameObject);
                }
                if (!participant.TryGetComponent(out ConversationGroup conversationGroup)) {
                    conversationGroup = Undo.AddComponent<ConversationGroup>(participant.gameObject);
                }
                VoiceComponentSetup.EnsureParticipant(prefabRoot);
                VoiceComponentSetup.CompleteConfiguration(speakerIdentity);
                VoiceComponentSetup.CompleteConfiguration(conversationGroup);

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath, out bool savedSuccessfully);
                if (!savedSuccessfully) {
                    return VoiceSetupResult.Failure("Unity could not save the configured player prefab.");
                }

                return VoiceSetupResult.Success($"{playerPrefab.name} is ready for networked voice chat.");
            } catch (Exception exception) {
                Debug.LogException(exception);
                return VoiceSetupResult.Failure($"The player prefab could not be configured: {exception.Message}");
            } finally {
                if (prefabRoot != null) {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        internal static VoiceSetupResult AddSteamAudioSource(GameObject playerPrefab) {
            if (!SteamAudioSupport.IsSpatializerSelected()) {
                return VoiceSetupResult.Failure("Select Steam Audio as the project spatializer first.");
            }

            Type steamAudioSourceType = SteamAudioSupport.GetSteamAudioSourceType();
            if (steamAudioSourceType == null) {
                return VoiceSetupResult.Failure("The Steam Audio Source component is not installed.");
            }

            if (!TryGetEditablePrefabPath(playerPrefab, out string prefabPath, out string failureMessage)) {
                return VoiceSetupResult.Failure(failureMessage);
            }

            GameObject prefabRoot = null;
            int undoGroup = BeginUndoGroup(UNDO_STEAM_AUDIO_NAME);

            try {
                prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                if (prefabRoot == null) {
                    return VoiceSetupResult.Failure("Unity could not load the selected player prefab.");
                }

                if (!prefabRoot.TryGetComponent(out SpeakerOutput speakerOutput) || speakerOutput.AudioSource == null) {
                    return VoiceSetupResult.Failure("Configure the player prefab before adding Steam Audio Source.");
                }
                AudioSource audioSource = speakerOutput.AudioSource;

                Undo.RegisterFullObjectHierarchyUndo(prefabRoot, UNDO_STEAM_AUDIO_NAME);
                bool alreadyHasSteamAudioSource = audioSource.TryGetComponent(steamAudioSourceType, out Component _);
                if (!alreadyHasSteamAudioSource) {
                    Undo.AddComponent(audioSource.gameObject, steamAudioSourceType);
                }

                Undo.RecordObject(speakerOutput, UNDO_STEAM_AUDIO_NAME);
                speakerOutput.SetSpatialization(SpatializationMode.ProjectSpatializer);
                EditorUtility.SetDirty(audioSource);
                EditorUtility.SetDirty(speakerOutput);

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath, out bool savedSuccessfully);
                if (!savedSuccessfully) {
                    return VoiceSetupResult.Failure("Unity could not save the Steam Audio component on the player prefab.");
                }

                return alreadyHasSteamAudioSource
                    ? VoiceSetupResult.Success("The voice playback object already has Steam Audio Source and now uses the project spatializer.")
                    : VoiceSetupResult.Success($"Steam Audio Source was added to {playerPrefab.name}.");
            } catch (Exception exception) {
                Debug.LogException(exception);
                return VoiceSetupResult.Failure($"Steam Audio Source could not be added: {exception.Message}");
            } finally {
                if (prefabRoot != null) {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private static int BeginUndoGroup(string undoName) {
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            return undoGroup;
        }

        private static bool TryGetEditablePrefabPath(GameObject playerPrefab, out string prefabPath, out string failureMessage) {
            prefabPath = string.Empty;
            failureMessage = string.Empty;

            if (playerPrefab == null) {
                failureMessage = "Select a player prefab first.";
                return false;
            }

            prefabPath = AssetDatabase.GetAssetPath(playerPrefab);
            if (string.IsNullOrEmpty(prefabPath) || !PrefabUtility.IsPartOfPrefabAsset(playerPrefab)) {
                failureMessage = "Select a prefab asset rather than a scene object.";
                return false;
            }

            if (PrefabUtility.GetPrefabAssetType(playerPrefab) == PrefabAssetType.Model) {
                failureMessage = "Model prefabs cannot store voice components. Create a prefab or prefab variant from the model first.";
                return false;
            }

            if (!AssetDatabase.IsOpenForEdit(prefabPath, StatusQueryOptions.UseCachedIfPossible)) {
                failureMessage = "The selected prefab is read-only. Make it editable or create a local prefab variant first.";
                return false;
            }

            return true;
        }

    }

}
