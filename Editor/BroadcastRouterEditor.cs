using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(BroadcastRouter))]
    [CanEditMultipleObjects]
    internal sealed class BroadcastRouterEditor : UnityEditor.Editor {

        private const string MOVE_ROUTER_UNDO_NAME = "Move Broadcast Router To Scene Root";
        private const string ENABLE_OBSERVERS_UNDO_NAME = "Enable Broadcast Router Observers";

        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            BroadcastRouter router = target as BroadcastRouter;
            if (router == null || EditorUtility.IsPersistent(router)) { return; }

            DrawPlacementIssue(router);
            DrawObserverIssue(router);
            DrawDuplicateIssue(router);
        }

        private static void DrawPlacementIssue(BroadcastRouter router) {
            if (router.GetComponentInParent<NetworkManager>() == null) { return; }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("A Broadcast Router cannot be parented below a Network Manager. It must be a scene root.", MessageType.Error);
            if (!GUILayout.Button("Move To Scene Root")) { return; }

            Undo.SetTransformParent(router.transform, null, MOVE_ROUTER_UNDO_NAME);
            EditorSceneManager.MarkSceneDirty(router.gameObject.scene);
        }

        private static void DrawObserverIssue(BroadcastRouter router) {
            if (!router.TryGetComponent(out NetworkObject networkObject) || networkObject.SpawnWithObservers) { return; }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("A Broadcast Router must start with observers so every listening client can receive routed audio.", MessageType.Error);
            if (!GUILayout.Button("Enable Starting Observers")) { return; }

            Undo.RecordObject(networkObject, ENABLE_OBSERVERS_UNDO_NAME);
            networkObject.SpawnWithObservers = true;
            EditorUtility.SetDirty(networkObject);
            EditorSceneManager.MarkSceneDirty(router.gameObject.scene);
        }

        private static void DrawDuplicateIssue(BroadcastRouter router) {
            BroadcastRouter[] routers = Object.FindObjectsByType<BroadcastRouter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int sceneRouterCount = 0;
            for (int i = 0; i < routers.Length; i++) {
                if (routers[i] != null && routers[i].gameObject.scene == router.gameObject.scene) {
                    sceneRouterCount++;
                }
            }
            if (sceneRouterCount <= 1) { return; }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("This scene has more than one Broadcast Router. Keep a single globally observed router for the session.", MessageType.Warning);
        }

    }

}
