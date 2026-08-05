using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Pool;

namespace Lockyaw.VoiceChat.SpeakerVolumeSample {

    [AddComponentMenu("Lockyaw Voice Chat/Speaker Volume UI/Speaker Volume Panel")]
    [DisallowMultipleComponent]
    public sealed class SpeakerVolumePanel : MonoBehaviour {

        [Header("References")]
        [SerializeField] private RectTransform rowsParent;
        [SerializeField] private SpeakerVolumeRow rowPrefab;

        [Header("Players")]
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private bool includeLocalPlayer;
        [SerializeField, Min(.1f)] private float refreshInterval = .25f;

        public int RowCount => speakerRows.Count;

        private readonly Dictionary<VoiceParticipant, SpeakerVolumeRow> speakerRows = new();
        private readonly List<VoiceParticipant> discoveredParticipants = new();
        private readonly List<VoiceParticipant> removedParticipants = new();
        private ObjectPool<SpeakerVolumeRow> rowPool;
        private float nextRefreshAt;

        private void Awake() {
            ResolveReferences();
            EnsureRowPool();
        }

        private void OnDestroy() {
            ClearRows();
            if (rowPool != null) {
                rowPool.Clear();
                rowPool = null;
            }
        }

        private void OnEnable() {
            nextRefreshAt = 0f;
            RefreshParticipants();
        }

        private void OnDisable() {
            ClearRows();
        }

        private void Update() {
            if (Time.unscaledTime < nextRefreshAt) { return; }
            RefreshParticipants();
        }

        private void Reset() {
            rowsParent = transform as RectTransform;
            rowPrefab = GetComponentInChildren<SpeakerVolumeRow>(true);
            networkManager = FindFirstObjectByType<NetworkManager>();
            includeLocalPlayer = false;
            refreshInterval = .25f;
        }

        public void RefreshParticipants() {
            ResolveReferences();
            nextRefreshAt = Time.unscaledTime + refreshInterval;
            if (rowPrefab == null || rowsParent == null) { return; }
            EnsureRowPool();

            FindParticipants();
            RemoveMissingRows();
            AddMissingRows();
            SortRows();
        }

        public void ClearRows() {
            foreach (KeyValuePair<VoiceParticipant, SpeakerVolumeRow> speakerRow in speakerRows) {
                if (speakerRow.Value != null) {
                    ReleaseRow(speakerRow.Value);
                }
            }
            speakerRows.Clear();
            discoveredParticipants.Clear();
            removedParticipants.Clear();
        }

        private void ResolveReferences() {
            if (rowsParent == null) {
                rowsParent = transform as RectTransform;
            }
            if (networkManager == null && VoiceParticipant.TryGetLocal(out VoiceParticipant localParticipant)) {
                networkManager = localParticipant.NetworkManager;
            }
        }

        private void FindParticipants() {
            discoveredParticipants.Clear();
            IReadOnlyList<VoiceParticipant> participants = VoiceParticipant.SpawnedParticipants;
            for (int i = 0; i < participants.Count; i++) {
                VoiceParticipant participant = participants[i];
                if (!CanListParticipant(participant)) { continue; }
                discoveredParticipants.Add(participant);
            }
            discoveredParticipants.Sort(CompareParticipants);
        }

        private void RemoveMissingRows() {
            removedParticipants.Clear();
            HashSet<VoiceParticipant> discoveredParticipantSet = HashSetPool<VoiceParticipant>.Get();
            try {
                for (int i = 0; i < discoveredParticipants.Count; i++) {
                    discoveredParticipantSet.Add(discoveredParticipants[i]);
                }

                foreach (KeyValuePair<VoiceParticipant, SpeakerVolumeRow> speakerRow in speakerRows) {
                    if (speakerRow.Key == null || !discoveredParticipantSet.Contains(speakerRow.Key)) {
                        removedParticipants.Add(speakerRow.Key);
                    }
                }

                for (int i = 0; i < removedParticipants.Count; i++) {
                    VoiceParticipant participant = removedParticipants[i];
                    if (!speakerRows.TryGetValue(participant, out SpeakerVolumeRow row)) { continue; }
                    speakerRows.Remove(participant);
                    if (row != null) {
                        ReleaseRow(row);
                    }
                }
            }
            finally {
                HashSetPool<VoiceParticipant>.Release(discoveredParticipantSet);
            }
        }

        private void AddMissingRows() {
            for (int i = 0; i < discoveredParticipants.Count; i++) {
                VoiceParticipant participant = discoveredParticipants[i];
                if (speakerRows.ContainsKey(participant)) { continue; }

                SpeakerVolumeRow row = rowPool.Get();
                row.Bind(participant);
                speakerRows.Add(participant, row);
            }
        }

        private void SortRows() {
            for (int i = 0; i < discoveredParticipants.Count; i++) {
                VoiceParticipant participant = discoveredParticipants[i];
                if (speakerRows.TryGetValue(participant, out SpeakerVolumeRow row) && row != null) {
                    row.transform.SetAsLastSibling();
                }
            }
        }

        private void EnsureRowPool() {
            if (rowPool != null || rowPrefab == null || rowsParent == null) { return; }

            rowPool = new(
                CreateRow,
                ActivateRow,
                DeactivateRow,
                DestroyRow,
                true,
                8,
                128);
        }

        private SpeakerVolumeRow CreateRow() => Instantiate(rowPrefab, rowsParent);

        private void ActivateRow(SpeakerVolumeRow row) {
            row.transform.SetParent(rowsParent, false);
            row.gameObject.SetActive(true);
        }

        private void DeactivateRow(SpeakerVolumeRow row) {
            row.Unbind();
            row.gameObject.SetActive(false);
        }

        private void DestroyRow(SpeakerVolumeRow row) {
            if (row != null) {
                Destroy(row.gameObject);
            }
        }

        private void ReleaseRow(SpeakerVolumeRow row) {
            if (rowPool == null) {
                DestroyRow(row);
                return;
            }
            rowPool.Release(row);
        }

        private bool CanListParticipant(VoiceParticipant participant) {
            if (participant == null || !participant.IsSpawned || !participant.isActiveAndEnabled) { return false; }
            if (networkManager != null && participant.NetworkManager != networkManager) { return false; }
            if (!includeLocalPlayer && participant.IsOwner) { return false; }
            return participant.TryGetComponent(out SpeakerOutput _);
        }

        private static int CompareParticipants(VoiceParticipant firstParticipant, VoiceParticipant secondParticipant) {
            return firstParticipant.OwnerClientId.CompareTo(secondParticipant.OwnerClientId);
        }

    }

}
