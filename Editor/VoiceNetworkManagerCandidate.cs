using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    internal sealed class VoiceNetworkManagerCandidate {

        internal NetworkManager Manager { get; }
        internal GameObject PlayerPrefab { get; }
        internal string SourcePath { get; }
        internal string DisplayName { get; }
        internal bool IsLoadedScene { get; }

        internal VoiceNetworkManagerCandidate(NetworkManager manager, GameObject playerPrefab, string sourcePath, string displayName, bool isLoadedScene) {
            Manager = manager;
            PlayerPrefab = playerPrefab;
            SourcePath = sourcePath;
            DisplayName = displayName;
            IsLoadedScene = isLoadedScene;
        }

    }

}
