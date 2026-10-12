#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System.Collections.Generic;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    internal static class PlaybackSourceRegistry {

        internal const ulong NO_SOURCE_ID = 0;

        private static readonly Dictionary<ulong, int> referenceCounts = new(32);

        internal static ulong Register(AudioSource audioSource) {
            if (audioSource == null) { return NO_SOURCE_ID; }

            ulong audioSourceId = GetSourceId(audioSource);
            if (referenceCounts.TryGetValue(audioSourceId, out int referenceCount)) {
                referenceCounts[audioSourceId] = referenceCount + 1;
            }
            else {
                referenceCounts.Add(audioSourceId, 1);
            }
            return audioSourceId;
        }

        internal static void Unregister(ulong audioSourceId) {
            if (audioSourceId == NO_SOURCE_ID || !referenceCounts.TryGetValue(audioSourceId, out int referenceCount)) { return; }

            if (referenceCount <= 1) {
                referenceCounts.Remove(audioSourceId);
            }
            else {
                referenceCounts[audioSourceId] = referenceCount - 1;
            }
        }

        internal static bool Contains(AudioSource audioSource) {
            return audioSource != null && referenceCounts.ContainsKey(GetSourceId(audioSource));
        }

        internal static ulong GetSourceId(AudioSource audioSource) {
            if (audioSource == null) { return NO_SOURCE_ID; }
#if UNITY_6000_7_OR_NEWER
            return EntityId.ToULong(audioSource.GetEntityId());
#else
            return unchecked((ulong)audioSource.GetInstanceID());
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            referenceCounts.Clear();
        }

    }

}
