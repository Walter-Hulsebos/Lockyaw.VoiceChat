using System;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    internal static class SteamAudioSupport {

        internal const string STEAM_AUDIO_SPATIALIZER = "Steam Audio Spatializer";

        private static readonly string[] steamAudioSourceTypeNames = {
            "SteamAudio.SteamAudioSource, SteamAudioUnity",
            "SteamAudio.SteamAudioSource, SteamAudio"
        };

        internal static VoiceSetupResult SelectSpatializer() {
            if (!IsSpatializerAvailable()) {
                return VoiceSetupResult.Failure("Steam Audio is not available for the current editor platform.");
            }

            try {
                AudioSettings.SetSpatializerPluginName(STEAM_AUDIO_SPATIALIZER);
                return IsSpatializerSelected()
                    ? VoiceSetupResult.Success("Steam Audio is now the project spatializer. Configure or reconfigure the player prefab to apply it to voice playback.")
                    : VoiceSetupResult.Failure("Unity did not accept Steam Audio as the project spatializer.");
            } catch (Exception exception) {
                Debug.LogException(exception);
                return VoiceSetupResult.Failure($"Steam Audio could not be selected: {exception.Message}");
            }
        }

        internal static bool IsSpatializerAvailable() {
            string[] pluginNames = AudioSettings.GetSpatializerPluginNames();
            if (pluginNames == null) { return false; }

            for (int i = 0; i < pluginNames.Length; i++) {
                if (string.Equals(pluginNames[i], STEAM_AUDIO_SPATIALIZER, StringComparison.OrdinalIgnoreCase)) { return true; }
            }

            return false;
        }

        internal static bool IsSpatializerSelected() {
            return string.Equals(AudioSettings.GetSpatializerPluginName(), STEAM_AUDIO_SPATIALIZER, StringComparison.OrdinalIgnoreCase);
        }

        internal static Type GetSteamAudioSourceType() {
            for (int i = 0; i < steamAudioSourceTypeNames.Length; i++) {
                Type componentType = Type.GetType(steamAudioSourceTypeNames[i]);
                if (componentType != null) { return componentType; }
            }

            return null;
        }

        internal static string GetSelectedSpatializerName() {
            string pluginName = AudioSettings.GetSpatializerPluginName();
            return string.IsNullOrEmpty(pluginName) ? "None" : pluginName;
        }

    }

}
