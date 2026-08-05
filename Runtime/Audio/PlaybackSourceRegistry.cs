using System.Collections.Generic;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal static class PlaybackSourceRegistry {

        internal const int NO_SOURCE_ID = 0;

        private static readonly Dictionary<int, int> referenceCounts = new(32);

        internal static int Register(AudioSource audioSource) {
            if (audioSource == null) { return NO_SOURCE_ID; }

            int audioSourceId = audioSource.GetInstanceID();
            if (referenceCounts.TryGetValue(audioSourceId, out int referenceCount)) {
                referenceCounts[audioSourceId] = referenceCount + 1;
            }
            else {
                referenceCounts.Add(audioSourceId, 1);
            }
            return audioSourceId;
        }

        internal static void Unregister(int audioSourceId) {
            if (audioSourceId == NO_SOURCE_ID || !referenceCounts.TryGetValue(audioSourceId, out int referenceCount)) { return; }

            if (referenceCount <= 1) {
                referenceCounts.Remove(audioSourceId);
            }
            else {
                referenceCounts[audioSourceId] = referenceCount - 1;
            }
        }

        internal static bool Contains(AudioSource audioSource) {
            return audioSource != null && referenceCounts.ContainsKey(audioSource.GetInstanceID());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            referenceCounts.Clear();
        }

    }

}
