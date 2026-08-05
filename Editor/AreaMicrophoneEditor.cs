using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(AreaMicrophone))]
    [CanEditMultipleObjects]
    internal sealed class AreaMicrophoneEditor : UnityEditor.Editor {

        private const string CREATE_ROUTER_UNDO_NAME = "Create Voice Chat Broadcast Router";

        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            AreaMicrophone microphone = target as AreaMicrophone;
            if (microphone == null || EditorUtility.IsPersistent(microphone) || HasRouter(microphone)) { return; }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("This scene needs one Broadcast Router to carry frequency traffic between network observers.", MessageType.Warning);
            if (GUILayout.Button("Create Broadcast Router")) {
                CreateRouter(microphone);
            }
        }

        private static void CreateRouter(AreaMicrophone microphone) {
            GameObject routerObject = new("Broadcast Routing");
            SceneManager.MoveGameObjectToScene(routerObject, microphone.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(routerObject, CREATE_ROUTER_UNDO_NAME);

            NetworkObject networkObject = Undo.AddComponent<NetworkObject>(routerObject);
            networkObject.SpawnWithObservers = true;
            Undo.AddComponent<BroadcastRouter>(routerObject);

            EditorUtility.SetDirty(networkObject);
            EditorSceneManager.MarkSceneDirty(microphone.gameObject.scene);
            Selection.activeGameObject = routerObject;
        }

        private static bool HasRouter(AreaMicrophone microphone) {
            BroadcastRouter[] routers = Object.FindObjectsByType<BroadcastRouter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < routers.Length; i++) {
                if (routers[i] != null && routers[i].gameObject.scene == microphone.gameObject.scene) { return true; }
            }
            return false;
        }

    }

}
