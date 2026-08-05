using UnityEditor;

namespace Lockyaw.VoiceChat.Editor {

    internal sealed class SampleImportPostprocessor : AssetPostprocessor {

        private static void OnPostprocessAllAssets(
            string[] importedAssetPaths,
            string[] deletedAssetPaths,
            string[] movedAssetPaths,
            string[] movedFromAssetPaths
        ) {
            SampleDependencyInstaller.HandleAssetsImported(importedAssetPaths);
        }

    }

}
