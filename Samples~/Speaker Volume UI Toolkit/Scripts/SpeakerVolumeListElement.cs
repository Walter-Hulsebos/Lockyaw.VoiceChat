using System;
using System.Collections.Generic;
using UnityEngine.Pool;
using UnityEngine.UIElements;

namespace Lockyaw.VoiceChat.SpeakerVolumeToolkitSample {

    public sealed class SpeakerVolumeListElement : VisualElement, IDisposable {

        public int Count => speakerEntries.Count;

        private readonly Dictionary<ulong, SpeakerVolumeEntryElement> speakerEntries = new();
        private readonly List<VoiceParticipant> visibleSpeakers = new();
        private readonly VisualTreeAsset speakerTemplate;
        private readonly ObjectPool<SpeakerVolumeEntryElement> speakerEntryPool;

        public SpeakerVolumeListElement(VisualTreeAsset speakerTemplate) {
            this.speakerTemplate = speakerTemplate;
            speakerEntryPool = new(
                CreateEntry,
                null,
                ReleaseEntry,
                DestroyEntry,
                true,
                8,
                128);
            AddToClassList("speaker-volume-list");
        }

        public void SynchronizeSpeakers(bool includeLocalSpeaker) {
            CollectVisibleSpeakers(includeLocalSpeaker);
            HashSet<ulong> activeSpeakerIds = HashSetPool<ulong>.Get();

            try {
                for (int i = 0; i < visibleSpeakers.Count; i++) {
                    VoiceParticipant participant = visibleSpeakers[i];
                    ulong speakerId = participant.NetworkObjectId;
                    activeSpeakerIds.Add(speakerId);

                    if (!speakerEntries.TryGetValue(speakerId, out SpeakerVolumeEntryElement entry)) {
                        entry = speakerEntryPool.Get();
                        entry.Bind(participant);
                        speakerEntries.Add(speakerId, entry);
                    }
                    else if (entry.Participant != participant) {
                        entry.Bind(participant);
                    }
                }

                RemoveStaleSpeakers(activeSpeakerIds);
                ApplySpeakerOrder();
                RefreshVisuals();
            }
            finally {
                HashSetPool<ulong>.Release(activeSpeakerIds);
            }
        }

        public void RefreshVisuals() {
            foreach (KeyValuePair<ulong, SpeakerVolumeEntryElement> speakerEntry in speakerEntries) {
                speakerEntry.Value.RefreshVisuals();
            }
        }

        public void Dispose() {
            foreach (KeyValuePair<ulong, SpeakerVolumeEntryElement> speakerEntry in speakerEntries) {
                speakerEntryPool.Release(speakerEntry.Value);
            }
            speakerEntries.Clear();
            visibleSpeakers.Clear();
            speakerEntryPool.Clear();
            Clear();
        }

        private void CollectVisibleSpeakers(bool includeLocalSpeaker) {
            visibleSpeakers.Clear();
            VoiceParticipant.TryGetLocal(out VoiceParticipant localParticipant);
            IReadOnlyList<VoiceParticipant> participants = VoiceParticipant.SpawnedParticipants;
            for (int i = 0; i < participants.Count; i++) {
                VoiceParticipant participant = participants[i];
                if (participant == null || !participant.IsSpawned || (!includeLocalSpeaker && participant.IsOwner)) { continue; }
                if (localParticipant != null && participant.NetworkManager != localParticipant.NetworkManager) { continue; }
                if (!participant.TryGetComponent(out SpeakerOutput _)) { continue; }
                visibleSpeakers.Add(participant);
            }

            SortVisibleSpeakers();
        }

        private void SortVisibleSpeakers() {
            for (int i = 1; i < visibleSpeakers.Count; i++) {
                VoiceParticipant currParticipant = visibleSpeakers[i];
                ulong currOwnerClientId = currParticipant.OwnerClientId;
                int insertionIndex = i;
                while (insertionIndex > 0 && visibleSpeakers[insertionIndex - 1].OwnerClientId > currOwnerClientId) {
                    visibleSpeakers[insertionIndex] = visibleSpeakers[insertionIndex - 1];
                    insertionIndex--;
                }
                visibleSpeakers[insertionIndex] = currParticipant;
            }
        }

        private void ApplySpeakerOrder() {
            for (int i = 0; i < visibleSpeakers.Count; i++) {
                ulong speakerId = visibleSpeakers[i].NetworkObjectId;
                SpeakerVolumeEntryElement entry = speakerEntries[speakerId];
                if (IndexOf(entry) == i) { continue; }
                Insert(i, entry);
            }
        }

        private void RemoveStaleSpeakers(HashSet<ulong> activeSpeakerIds) {
            List<ulong> staleSpeakerIds = ListPool<ulong>.Get();
            try {
                foreach (KeyValuePair<ulong, SpeakerVolumeEntryElement> speakerEntry in speakerEntries) {
                    if (!activeSpeakerIds.Contains(speakerEntry.Key)) {
                        staleSpeakerIds.Add(speakerEntry.Key);
                    }
                }

                for (int i = 0; i < staleSpeakerIds.Count; i++) {
                    ulong speakerId = staleSpeakerIds[i];
                    SpeakerVolumeEntryElement entry = speakerEntries[speakerId];
                    speakerEntries.Remove(speakerId);
                    speakerEntryPool.Release(entry);
                }
            }
            finally {
                ListPool<ulong>.Release(staleSpeakerIds);
            }
        }

        private SpeakerVolumeEntryElement CreateEntry() => new(speakerTemplate);

        private static void ReleaseEntry(SpeakerVolumeEntryElement entry) {
            entry.Bind(null);
            entry.RemoveFromHierarchy();
        }

        private static void DestroyEntry(SpeakerVolumeEntryElement entry) {
            entry.Dispose();
        }

    }

}
