#if UNITY_6000_5_OR_NEWER
using NoAutoStaticsCleanup = Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute;
#endif

using System;
using System.IO;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    #if UNITY_6000_5_OR_NEWER
    [NoAutoStaticsCleanup]
    #endif
    internal static class NativeCapturePlatform {

        internal static string LibraryPath { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        internal static void Initialize() {
            LibraryPath = ResolveLibraryPath();
            MicrophoneDevices.ResetCache();
        }

        private static string ResolveLibraryPath() {
#if UNITY_SERVER || (!UNITY_EDITOR_WIN && !UNITY_EDITOR_OSX && !UNITY_EDITOR_LINUX && !UNITY_STANDALONE_WIN && !UNITY_STANDALONE_OSX && !UNITY_STANDALONE_LINUX)
            return "";
#else
            if (IntPtr.Size != 8) { return ""; }
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            string libraryName = "LockyawVoiceCapture.dll";
            string platformDirectory = "Windows/x86_64";
#elif UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
            string libraryName = "libLockyawVoiceCapture.dylib";
            string platformDirectory = "macOS";
#else
            string libraryName = "libLockyawVoiceCapture.so";
            string platformDirectory = "Linux/x86_64";
#endif
#if UNITY_EDITOR
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NativeCapturePlatform).Assembly);
            if (package != null) {
                string packagePath = Path.Combine(package.resolvedPath, "Runtime", "Plugins", platformDirectory, libraryName);
                if (File.Exists(packagePath)) { return packagePath; }
            }
            string[] pluginGuids = UnityEditor.AssetDatabase.FindAssets("LockyawVoiceCapture");
            foreach (string pluginGuid in pluginGuids) {
                string assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(pluginGuid);
                if (!assetPath.EndsWith(libraryName, StringComparison.Ordinal) || !assetPath.Contains(platformDirectory)) { continue; }
                string pluginPath = Path.GetFullPath(assetPath);
                if (File.Exists(pluginPath)) { return pluginPath; }
            }
#else
            string[] pluginDirectories = {
                Path.Combine(Application.dataPath, "Plugins", "x86_64"),
                Path.Combine(Application.dataPath, "Plugins"),
                Path.Combine(Application.dataPath, "PlugIns")
            };
            foreach (string pluginDirectory in pluginDirectories) {
                string pluginPath = Path.Combine(pluginDirectory, libraryName);
                if (File.Exists(pluginPath)) { return pluginPath; }
            }
#endif
            return "";
#endif
        }

    }

}
