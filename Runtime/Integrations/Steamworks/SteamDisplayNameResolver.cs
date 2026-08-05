using System;
using Steamworks;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    public sealed class SteamDisplayNameResolver : ISpeakerNameResolver {

        private static readonly SteamDisplayNameResolver defaultResolver = new();

        public bool TryResolveDisplayName(SpeakerIdentity identity, out string displayName) {
            displayName = string.Empty;

            try {
                if (!SteamAPI.IsSteamRunning()) { return false; }
                displayName = SteamFriends.GetPersonaName();
                return !string.IsNullOrWhiteSpace(displayName);
            }
            catch (InvalidOperationException) {
                return false;
            }
            catch (DllNotFoundException) {
                return false;
            }
            catch (EntryPointNotFoundException) {
                return false;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterDefaultResolver() {
            if (SpeakerIdentity.DefaultResolver != null) { return; }
            SpeakerIdentity.DefaultResolver = defaultResolver;
        }

    }

}
