using System;
using System.Collections.Generic;
using UnityEditor;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Pool;

namespace Lockyaw.VoiceChat.Editor {

    internal static class VoicePrefabConfigurator {

        private const string VOICE_PLAYBACK_OBJECT_NAME = "Voice Playback";
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
                VoiceParticipant participant = GetOrAddParticipant(prefabRoot);
                if (!participant.TryGetComponent(out SpeakerIdentity speakerIdentity)) {
                    speakerIdentity = Undo.AddComponent<SpeakerIdentity>(participant.gameObject);
                }
                if (!participant.TryGetComponent(out ConversationGroup conversationGroup)) {
                    conversationGroup = Undo.AddComponent<ConversationGroup>(participant.gameObject);
                }
                participant.TryGetComponent(out NetworkObject networkObject);
                ConfigureStableOwnership(networkObject);
                if (!participant.TryGetComponent(out SpeakerOutput speakerOutput)) {
                    speakerOutput = Undo.AddComponent<SpeakerOutput>(participant.gameObject);
                }

                AudioSource audioSource = GetOrCreateAudioSource(prefabRoot, speakerOutput);
                PositionManagedAudioSource(prefabRoot, audioSource);

                Undo.RecordObject(speakerOutput, UNDO_CONFIGURATION_NAME);
                speakerOutput.SetAudioSource(audioSource);
                speakerOutput.SetSpatialization(GetPreferredSpatialization());

                EditorUtility.SetDirty(audioSource);
                if (networkObject != null) {
                    EditorUtility.SetDirty(networkObject);
                }
                EditorUtility.SetDirty(speakerOutput);
                EditorUtility.SetDirty(speakerIdentity);
                EditorUtility.SetDirty(conversationGroup);
                EditorUtility.SetDirty(participant);

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

        private static VoiceParticipant GetOrAddParticipant(GameObject prefabRoot) {
            return prefabRoot.TryGetComponent(out VoiceParticipant participant)
                ? participant
                : Undo.AddComponent<VoiceParticipant>(prefabRoot);
        }

        private static void ConfigureStableOwnership(NetworkObject networkObject) {
            if (networkObject == null) { return; }

            SerializedObject serializedNetworkObject = new(networkObject);
            SerializedProperty ownershipProperty = serializedNetworkObject.FindProperty("Ownership");
            if (ownershipProperty == null) { return; }

            Undo.RecordObject(networkObject, UNDO_CONFIGURATION_NAME);
            ownershipProperty.intValue = (int)NetworkObject.OwnershipStatus.None;
            serializedNetworkObject.ApplyModifiedProperties();
        }

        private static AudioSource GetOrCreateAudioSource(GameObject prefabRoot, SpeakerOutput speakerOutput) {
            if (speakerOutput.AudioSource != null) { return speakerOutput.AudioSource; }

            Transform playbackTransform = FindPlaybackTransform(prefabRoot.transform);
            if (playbackTransform == null) {
                Transform playbackParent = FindHumanoidHead(prefabRoot);
                GameObject playbackObject = new(VOICE_PLAYBACK_OBJECT_NAME);
                Undo.RegisterCreatedObjectUndo(playbackObject, UNDO_CONFIGURATION_NAME);
                playbackTransform = playbackObject.transform;
                playbackTransform.SetParent(playbackParent, false);
            }

            return playbackTransform.TryGetComponent(out AudioSource audioSource)
                ? audioSource
                : Undo.AddComponent<AudioSource>(playbackTransform.gameObject);
        }

        private static void PositionManagedAudioSource(GameObject prefabRoot, AudioSource audioSource) {
            if (audioSource.gameObject == prefabRoot) { return; }
            if (audioSource.name != VOICE_PLAYBACK_OBJECT_NAME) { return; }

            Transform playbackTransform = audioSource.transform;
            Transform playbackParent = FindHumanoidHead(prefabRoot);
            Undo.RecordObject(audioSource.gameObject, UNDO_CONFIGURATION_NAME);
            Undo.RecordObject(playbackTransform, UNDO_CONFIGURATION_NAME);
            audioSource.gameObject.name = VOICE_PLAYBACK_OBJECT_NAME;
            playbackTransform.SetParent(playbackParent, false);
            playbackTransform.localPosition = Vector3.zero;
            playbackTransform.localRotation = Quaternion.identity;
            playbackTransform.localScale = Vector3.one;
        }

        private static int BeginUndoGroup(string undoName) {
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            return undoGroup;
        }

        private static Transform FindPlaybackTransform(Transform prefabRoot) {
            List<Transform> transforms = ListPool<Transform>.Get();
            try {
                prefabRoot.GetComponentsInChildren(true, transforms);
                for (int i = 0; i < transforms.Count; i++) {
                    if (transforms[i].name == VOICE_PLAYBACK_OBJECT_NAME) { return transforms[i]; }
                }
                return null;
            }
            finally {
                ListPool<Transform>.Release(transforms);
            }
        }

        private static Transform FindHumanoidHead(GameObject prefabRoot) {
            List<Animator> animators = ListPool<Animator>.Get();
            try {
                prefabRoot.GetComponentsInChildren(true, animators);
                for (int i = 0; i < animators.Count; i++) {
                    Animator animator = animators[i];
                    if (animator.avatar == null || !animator.avatar.isHuman) { continue; }

                    Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                    if (head != null) { return head; }
                }
                return prefabRoot.transform;
            }
            finally {
                ListPool<Animator>.Release(animators);
            }
        }

        private static SpatializationMode GetPreferredSpatialization() {
            return string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName())
                ? SpatializationMode.UnityThreeDimensional
                : SpatializationMode.ProjectSpatializer;
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
