using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    [CustomPropertyDrawer(typeof(VoiceDeviceId))]
    internal sealed class VoiceDeviceIdDrawer : PropertyDrawer {

        private static readonly GUIContent refreshLabel = new("Refresh", "Refresh available microphones without starting capture.");

        private readonly MicrophoneDropdownOptions options = new();
        private IReadOnlyList<VoiceDeviceDescriptor> devices = Array.Empty<VoiceDeviceDescriptor>();
        private GUIContent[] labels = Array.Empty<GUIContent>();
        private double nextRefreshAt;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            SerializedProperty valueProperty = property.FindPropertyRelative("value");
            if (valueProperty == null) { return; }
            if (EditorApplication.timeSinceStartup >= nextRefreshAt) { RefreshDevices(); }
            Rect refreshPosition = new(position.xMax - 58f, position.y, 58f, position.height);
            Rect selectionPosition = new(position.x, position.y, position.width - 62f, position.height);
            if (GUI.Button(refreshPosition, refreshLabel, EditorStyles.miniButton)) { RefreshDevices(); }
            if (options.Refresh(devices, VoiceDeviceId.FromValue(valueProperty.stringValue))) {
                labels = new GUIContent[options.Labels.Count];
                for (int i = 0; i < labels.Length; i++) { labels[i] = new(options.Labels[i]); }
            }
            bool previousMixedValue = EditorGUI.showMixedValue;
            using (EditorGUI.PropertyScope propertyScope = new(position, label, property)) {
                EditorGUI.showMixedValue = valueProperty.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int selectedIndex = EditorGUI.Popup(selectionPosition, propertyScope.content, options.SelectedIndex, labels);
                if (EditorGUI.EndChangeCheck() && options.TryGetDeviceId(selectedIndex, out VoiceDeviceId selectedDevice)) {
                    valueProperty.stringValue = selectedDevice.Value;
                }
            }
            EditorGUI.showMixedValue = previousMixedValue;
        }

        private void RefreshDevices() {
            nextRefreshAt = EditorApplication.timeSinceStartup + 2d;
            MicrophoneDevices.Refresh();
            devices = MicrophoneDevices.GetAvailableDevices();
        }

    }

}
