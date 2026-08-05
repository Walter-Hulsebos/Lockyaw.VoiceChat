using UnityEditor;

namespace Lockyaw.VoiceChat.Editor {

    internal sealed class VoiceSceneCandidate {

        internal SceneAsset SceneAsset { get; }
        internal string ScenePath { get; }
        internal string DisplayName { get; }
        internal string Reason { get; }
        internal int Score { get; }
        internal bool IsInBuildSettings { get; }
        internal bool IsEnabledInBuild { get; }

        internal VoiceSceneCandidate(SceneAsset sceneAsset, string scenePath, string displayName, string reason, int score, bool isInBuildSettings, bool isEnabledInBuild) {
            SceneAsset = sceneAsset;
            ScenePath = scenePath;
            DisplayName = displayName;
            Reason = reason;
            Score = score;
            IsInBuildSettings = isInBuildSettings;
            IsEnabledInBuild = isEnabledInBuild;
        }

    }

}
