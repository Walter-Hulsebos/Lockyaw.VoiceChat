using System;
using System.IO;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    internal static class AndroidMicrophonePermissionSupport {

        internal readonly struct PermissionStatus {

            internal string Message { get; }
            internal MessageType MessageType { get; }

            internal PermissionStatus(string message, MessageType messageType) {
                Message = message;
                MessageType = messageType;
            }

        }

        private static readonly XNamespace androidNamespace = "http://schemas.android.com/apk/res/android";
        private static readonly XNamespace toolsNamespace = "http://schemas.android.com/tools";

        private const string ANDROID_PERMISSION_NAME = "android.permission.RECORD_AUDIO";
        private const string SKIP_PERMISSION_DIALOG_NAME = "unityplayer.SkipPermissionsDialog";

        internal static PermissionStatus GetStatus() {
            string[] manifestPaths = Directory.GetFiles(Application.dataPath, "AndroidManifest.xml", SearchOption.AllDirectories);

            for (int i = 0; i < manifestPaths.Length; i++) {
                if (RemovesRecordAudioPermission(manifestPaths[i])) {
                    string assetPath = FileUtil.GetProjectRelativePath(manifestPaths[i]);
                    return new PermissionStatus($"{assetPath} removes RECORD_AUDIO. Remove that manifest override so voice capture can be included in Android builds.", MessageType.Error);
                }
            }

            for (int i = 0; i < manifestPaths.Length; i++) {
                if (SkipsUnityPermissionDialog(manifestPaths[i])) {
                    string assetPath = FileUtil.GetProjectRelativePath(manifestPaths[i]);
                    return new PermissionStatus($"{assetPath} disables Unity's automatic permission dialog. Voice Chat will use its explicit Android microphone request instead.", MessageType.Info);
                }
            }

            return new PermissionStatus("Android needs no project-setting change. Unity adds RECORD_AUDIO because the package uses Microphone, and Voice Chat requests the runtime permission before capture.", MessageType.Info);
        }

        private static bool RemovesRecordAudioPermission(string manifestPath) {
            XDocument manifest = LoadManifest(manifestPath);
            if (manifest == null) { return false; }

            foreach (XElement element in manifest.Descendants("uses-permission")) {
                string permissionName = (string)element.Attribute(androidNamespace + "name");
                string toolsAction = (string)element.Attribute(toolsNamespace + "node");
                if (permissionName == ANDROID_PERMISSION_NAME && string.Equals(toolsAction, "remove", StringComparison.OrdinalIgnoreCase)) { return true; }
            }

            return false;
        }

        private static bool SkipsUnityPermissionDialog(string manifestPath) {
            XDocument manifest = LoadManifest(manifestPath);
            if (manifest == null) { return false; }

            foreach (XElement element in manifest.Descendants("meta-data")) {
                string settingName = (string)element.Attribute(androidNamespace + "name");
                string settingValue = (string)element.Attribute(androidNamespace + "value");
                if (settingName == SKIP_PERMISSION_DIALOG_NAME && string.Equals(settingValue, "true", StringComparison.OrdinalIgnoreCase)) { return true; }
            }

            return false;
        }

        private static XDocument LoadManifest(string manifestPath) {
            try {
                return XDocument.Load(manifestPath);
            } catch (Exception exception) {
                Debug.LogWarning($"Could not inspect {manifestPath}: {exception.Message}");
                return null;
            }
        }

    }

}
