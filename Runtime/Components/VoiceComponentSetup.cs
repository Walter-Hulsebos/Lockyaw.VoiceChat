#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    internal static class VoiceComponentSetup {

#if UNITY_EDITOR
        private readonly struct PendingSetup {

            internal readonly int UndoGroup;
            internal readonly bool ResetAudioSource;

            internal PendingSetup(int undoGroup, bool resetAudioSource) {
                UndoGroup = undoGroup;
                ResetAudioSource = resetAudioSource;
            }

        }
#endif

        private static readonly HashSet<GameObject> configuringObjects = new();
#if UNITY_EDITOR
        private static readonly Dictionary<Component, PendingSetup> pendingSetups = new();
#endif

        internal static void RequestParticipantSetup(Component entry, bool resetAudioSource = false) {
            if (entry == null || configuringObjects.Contains(entry.gameObject)) { return; }
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.IsAssetImportWorkerProcess()) { return; }
            if (!Application.isPlaying) {
                QueueEditorSetup(entry, resetAudioSource);
                return;
            }
#endif
            EnsureParticipantGraph(entry.gameObject, resetAudioSource);
        }

        internal static void RequestChannelSetup(ChannelSpeaker speaker) {
            if (speaker == null) { return; }
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.IsAssetImportWorkerProcess()) { return; }
            if (!Application.isPlaying) {
                QueueEditorSetup(speaker, true);
                return;
            }
#endif
            speaker.EnsureSetup(true);
        }

        internal static VoiceParticipant EnsureParticipant(GameObject target) => EnsureParticipantGraph(target, false);

        internal static AudioSource EnsurePlaybackSource(Component owner, AudioSource assigned, string childName) {
            if (assigned != null) { return assigned; }
            if (owner == null) { return null; }
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.IsAssetImportWorkerProcess()) { return null; }
#endif
            VoiceAudioRenderer[] renderers = owner.GetComponentsInChildren<VoiceAudioRenderer>(true);
            for (int i = 0; i < renderers.Length; i++) {
                VoiceAudioRenderer renderer = renderers[i];
                if (renderer.GetComponentInParent(owner.GetType()) != owner || !renderer.name.StartsWith(childName, System.StringComparison.Ordinal)) { continue; }
                if (!renderer.TryGetComponent(out AudioSource existingSource)) {
                    existingSource = AddComponent<AudioSource>(renderer.gameObject);
                    existingSource.playOnAwake = false;
                    existingSource.loop = true;
                    CompleteConfiguration(existingSource);
                }
                return existingSource;
            }

            RecordConfiguration(owner);
            Transform parent = FindPlaybackParent(owner);
            string availableName = childName;
            int duplicateIndex = 2;
            while (parent.Find(availableName) != null) {
                availableName = $"{childName} ({duplicateIndex})";
                duplicateIndex++;
            }
            GameObject playback = new(availableName);
#if UNITY_EDITOR
            if (!Application.isPlaying) {
                UnityEditor.Undo.RegisterCreatedObjectUndo(playback, "Create voice playback");
                UnityEditor.Undo.SetTransformParent(playback.transform, parent, false, "Parent voice playback");
            }
            else { playback.transform.SetParent(parent, false); }
#else
            playback.transform.SetParent(parent, false);
#endif
            AudioSource source = AddComponent<AudioSource>(playback);
            source.playOnAwake = false;
            source.loop = true;
            CompleteConfiguration(source);
            return source;
        }

        internal static TComponent AddComponent<TComponent>(GameObject target) where TComponent : Component {
#if UNITY_EDITOR
            if (!Application.isPlaying) { return UnityEditor.Undo.AddComponent<TComponent>(target); }
#endif
            return target.AddComponent<TComponent>();
        }

        internal static void RecordConfiguration(Component component) {
#if UNITY_EDITOR
            if (!Application.isPlaying && component != null && !UnityEditor.AssetDatabase.IsAssetImportWorkerProcess()) { UnityEditor.Undo.RecordObject(component, "Configure voice chat"); }
#endif
        }

        internal static void CompleteConfiguration(Component component) {
#if UNITY_EDITOR
            if (Application.isPlaying || component == null || UnityEditor.AssetDatabase.IsAssetImportWorkerProcess()) { return; }
            UnityEditor.EditorUtility.SetDirty(component);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(component)) { UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(component); }
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            configuringObjects.Clear();
#if UNITY_EDITOR
            pendingSetups.Clear();
            UnityEditor.EditorApplication.delayCall -= CompleteEditorSetups;
#endif
        }

#if UNITY_EDITOR
        private static void QueueEditorSetup(Component entry, bool resetAudioSource) {
            if (pendingSetups.TryGetValue(entry, out PendingSetup existing)) {
                pendingSetups[entry] = new(existing.UndoGroup, existing.ResetAudioSource || resetAudioSource);
                return;
            }
            pendingSetups.Add(entry, new(UnityEditor.Undo.GetCurrentGroup(), resetAudioSource));
            if (pendingSetups.Count == 1) { UnityEditor.EditorApplication.delayCall += CompleteEditorSetups; }
        }

        private static void CompleteEditorSetups() {
            KeyValuePair<Component, PendingSetup>[] requests = new KeyValuePair<Component, PendingSetup>[pendingSetups.Count];
            int index = 0;
            foreach (KeyValuePair<Component, PendingSetup> request in pendingSetups) { requests[index++] = request; }
            pendingSetups.Clear();
            for (int i = 0; i < requests.Length; i++) {
                Component entry = requests[i].Key;
                if (entry == null || Application.isPlaying) { continue; }
                PendingSetup request = requests[i].Value;
                if (entry is ChannelSpeaker speaker) { speaker.EnsureSetup(request.ResetAudioSource); }
                else { EnsureParticipantGraph(entry.gameObject, request.ResetAudioSource); }
                UnityEditor.Undo.FlushUndoRecordObjects();
                UnityEditor.Undo.CollapseUndoOperations(request.UndoGroup);
            }
        }
#endif

        private static VoiceParticipant EnsureParticipantGraph(GameObject target, bool resetAudioSource) {
            if (target == null) { return null; }
            target.TryGetComponent(out VoiceParticipant participant);
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.IsAssetImportWorkerProcess()) { return participant; }
#endif
            if (!configuringObjects.Add(target)) { return participant; }
            try {
                if (!target.TryGetComponent(out NetworkObject networkObject)) { AddComponent<NetworkObject>(target); }
                if (participant == null) { participant = AddComponent<VoiceParticipant>(target); }
                if (!target.TryGetComponent(out MicrophoneInput microphone)) { microphone = AddComponent<MicrophoneInput>(target); }
                if (!target.TryGetComponent(out SpeakerOutput speaker)) { speaker = AddComponent<SpeakerOutput>(target); }
                speaker.EnsureSetup(resetAudioSource);
                RecordConfiguration(participant);
                RecordConfiguration(microphone);
                participant.CacheReferences();
                microphone.CacheReferences();
                CompleteConfiguration(participant);
                CompleteConfiguration(microphone);
                CompleteConfiguration(speaker);
                return participant;
            }
            finally {
                configuringObjects.Remove(target);
            }
        }

        private static Transform FindPlaybackParent(Component owner) {
            if (!(owner is SpeakerOutput)) { return owner.transform; }
            Animator[] animators = owner.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++) {
                Animator animator = animators[i];
                if (animator.avatar == null || !animator.avatar.isHuman) { continue; }
                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) { return head; }
            }
            return owner.transform;
        }

    }

}
