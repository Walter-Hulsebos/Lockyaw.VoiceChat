using System;
using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(MicrophoneInput))]
    internal sealed class MicrophoneInputEditor : UnityEditor.Editor {

        private static readonly GUIContent microphoneLabel = new("Microphone", "Automatic uses the first available microphone. A selected microphone is retained when disconnected.");

        private readonly MicrophoneDropdownOptions options = new();
        private SerializedProperty requestedDeviceNameProperty;
        private string[] availableDevices = Array.Empty<string>();
        private GUIContent[] deviceLabels = Array.Empty<GUIContent>();
        private string lastRequestedDeviceName;
        private double nextDeviceRefreshAt;

        private void OnEnable() {
            requestedDeviceNameProperty = serializedObject.FindProperty("requestedDeviceName");
            lastRequestedDeviceName = requestedDeviceNameProperty.stringValue;
            RefreshDevices();
            EditorApplication.update += HandleEditorUpdated;
            Undo.undoRedoPerformed += HandleUndoRedoPerformed;
        }

        private void OnDisable() {
            EditorApplication.update -= HandleEditorUpdated;
            Undo.undoRedoPerformed -= HandleUndoRedoPerformed;
        }

        public override void OnInspectorGUI() {
            MicrophoneInput microphone = target as MicrophoneInput;
            if (microphone == null) { return; }

            serializedObject.Update();
            string previousDeviceName = microphone.RequestedDeviceName;
            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren)) {
                enterChildren = false;
                if (property.propertyPath == requestedDeviceNameProperty.propertyPath) {
                    DrawMicrophoneDropdown(property);
                    continue;
                }

                using (new EditorGUI.DisabledScope(property.propertyPath == "m_Script")) {
                    EditorGUILayout.PropertyField(property, true);
                }
            }
            serializedObject.ApplyModifiedProperties();
            RefreshLiveCapture(microphone, previousDeviceName);
            if (!Application.isPlaying) { return; }

            VoiceCaptureDiagnostics diagnostics = microphone.GetDiagnostics();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live Capture Quality", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Device", string.IsNullOrEmpty(diagnostics.DeviceName) ? "Unavailable" : diagnostics.DeviceName);
            EditorGUILayout.LabelField("Format", $"{diagnostics.SampleRate} Hz, {diagnostics.ChannelCount} channel(s)");
            EditorGUILayout.LabelField("Queued Audio", $"{diagnostics.QueuedMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Dropped Stale Audio", $"{diagnostics.DroppedMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Captured Frames", diagnostics.CapturedFrames.ToString());
            EditorGUILayout.LabelField("Encoded Frames", diagnostics.EncodedFrames.ToString());
            EditorGUILayout.LabelField("DTX-Suppressed Frames", diagnostics.SuppressedFrames.ToString());
            EditorGUILayout.LabelField("Submitted Frames", diagnostics.SubmittedFrames.ToString());
            EditorGUILayout.LabelField("Largest Update Burst", diagnostics.MaximumFramesPerUpdate.ToString());
            if (GUILayout.Button("Reset Capture Measurements")) {
                microphone.ResetDiagnostics();
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        private void HandleEditorUpdated() {
            if (EditorApplication.timeSinceStartup < nextDeviceRefreshAt) { return; }
            RefreshDevices();
        }

        private void HandleUndoRedoPerformed() {
            MicrophoneInput microphone = target as MicrophoneInput;
            if (microphone == null) { return; }
            RefreshLiveCapture(microphone, lastRequestedDeviceName);
            Repaint();
        }

        private void RefreshDevices() {
            nextDeviceRefreshAt = EditorApplication.timeSinceStartup + 1d;
            availableDevices = MicrophoneDevices.GetAvailableDevices();
            Repaint();
        }

        private void DrawMicrophoneDropdown(SerializedProperty property) {
            if (options.Refresh(availableDevices, property.stringValue)) {
                deviceLabels = new GUIContent[options.Labels.Count];
                for (int i = 0; i < deviceLabels.Length; i++) {
                    deviceLabels[i] = new(options.Labels[i]);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Device", EditorStyles.boldLabel);
            Rect position = EditorGUILayout.GetControlRect();
            using (EditorGUI.PropertyScope propertyScope = new(position, microphoneLabel, property)) {
                using (new EditorGUI.DisabledScope(!options.HasDevices)) {
                    EditorGUI.BeginChangeCheck();
                    int selectedIndex = EditorGUI.Popup(position, propertyScope.content, options.SelectedIndex, deviceLabels);
                    if (EditorGUI.EndChangeCheck() && options.TryGetDeviceName(selectedIndex, out string deviceName)) {
                        property.stringValue = deviceName;
                    }
                }
            }
        }

        private void RefreshLiveCapture(MicrophoneInput microphone, string previousDeviceName) {
            lastRequestedDeviceName = microphone.RequestedDeviceName;
            if (!Application.isPlaying || previousDeviceName == lastRequestedDeviceName || !microphone.IsCapturing) { return; }
            microphone.StartCapture();
        }

    }

}
