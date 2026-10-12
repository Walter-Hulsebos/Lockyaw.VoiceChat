using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(MicrophoneInput))]
    internal sealed class MicrophoneInputEditor : UnityEditor.Editor {

        private static readonly GUIContent microphoneLabel = new("Microphone", "System default follows the operating system. A selected microphone is retained when disconnected.");

        private readonly MicrophoneDropdownOptions options = new();
        private SerializedProperty deviceProperty;
        private IReadOnlyList<VoiceDeviceDescriptor> availableDevices = Array.Empty<VoiceDeviceDescriptor>();
        private GUIContent[] deviceLabels = Array.Empty<GUIContent>();
        private double nextDeviceRefreshAt;

        private void OnEnable() {
            deviceProperty = serializedObject.FindProperty("deviceId");
            RefreshDevices();
            EditorApplication.update += HandleEditorUpdated;
        }

        private void OnDisable() {
            EditorApplication.update -= HandleEditorUpdated;
        }

        public override void OnInspectorGUI() {
            MicrophoneInput microphone = target as MicrophoneInput;
            if (microphone == null) { return; }

            serializedObject.Update();
            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren)) {
                enterChildren = false;
                if (property.propertyPath == deviceProperty.propertyPath) {
                    DrawMicrophoneDropdown(property);
                    continue;
                }
                using (new EditorGUI.DisabledScope(property.propertyPath == "m_Script")) {
                    EditorGUILayout.PropertyField(property, true);
                }
            }
            serializedObject.ApplyModifiedProperties();
            if (!Application.isPlaying) { return; }

            VoiceCaptureDiagnostics diagnostics = microphone.GetDiagnostics();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live Capture Quality", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("State", diagnostics.State.ToString());
            EditorGUILayout.LabelField("Device", string.IsNullOrEmpty(diagnostics.DeviceName) ? "Unavailable" : diagnostics.DeviceName);
            EditorGUILayout.LabelField("Format", $"{diagnostics.SampleRate} Hz, {diagnostics.ChannelCount} channel(s)");
            EditorGUILayout.LabelField("Frame", diagnostics.FrameMilliseconds > 0f ? $"{diagnostics.FrameMilliseconds:F1} ms" : "Unavailable");
            EditorGUILayout.LabelField("Device Period", diagnostics.DevicePeriodMilliseconds > 0f ? $"{diagnostics.DevicePeriodMilliseconds:F1} ms" : "Unavailable");
            EditorGUILayout.LabelField("Wake", diagnostics.UsesDeviceSignal ? "Device signal" : "Polling");
            EditorGUILayout.LabelField("Queued Audio", $"{diagnostics.QueuedMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Dropped Stale Audio", $"{diagnostics.DroppedMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Captured Frames", diagnostics.CapturedFrames.ToString());
            EditorGUILayout.LabelField("Encoded Frames", diagnostics.EncodedFrames.ToString());
            EditorGUILayout.LabelField("DTX-Suppressed Frames", diagnostics.SuppressedFrames.ToString());
            EditorGUILayout.LabelField("Submitted Frames", diagnostics.SubmittedFrames.ToString());
            EditorGUILayout.LabelField("Largest Worker Pass", diagnostics.MaximumFramesPerPass.ToString());
            if (GUILayout.Button("Reset Capture Measurements")) { microphone.ResetDiagnostics(); }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        private void HandleEditorUpdated() {
            if (EditorApplication.timeSinceStartup < nextDeviceRefreshAt) { return; }
            RefreshDevices();
        }

        private void RefreshDevices() {
            nextDeviceRefreshAt = EditorApplication.timeSinceStartup + 1d;
            MicrophoneDevices.Refresh();
            availableDevices = MicrophoneDevices.GetAvailableDevices();
            Repaint();
        }

        private void DrawMicrophoneDropdown(SerializedProperty property) {
            SerializedProperty identityProperty = property.FindPropertyRelative("value");
            if (options.Refresh(availableDevices, VoiceDeviceId.FromValue(identityProperty.stringValue))) {
                deviceLabels = new GUIContent[options.Labels.Count];
                for (int i = 0; i < deviceLabels.Length; i++) { deviceLabels[i] = new(options.Labels[i]); }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Device", EditorStyles.boldLabel);
            Rect position = EditorGUILayout.GetControlRect();
            using (EditorGUI.PropertyScope propertyScope = new(position, microphoneLabel, property)) {
                EditorGUI.BeginChangeCheck();
                int selectedIndex = EditorGUI.Popup(position, propertyScope.content, options.SelectedIndex, deviceLabels);
                if (EditorGUI.EndChangeCheck() && options.TryGetDeviceId(selectedIndex, out VoiceDeviceId selectedDevice)) {
                    identityProperty.stringValue = selectedDevice.Value;
                }
            }
        }

    }

}
