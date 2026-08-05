using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(MicrophoneInput))]
    internal sealed class MicrophoneInputEditor : UnityEditor.Editor {

        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            MicrophoneInput microphone = target as MicrophoneInput;
            if (!Application.isPlaying || microphone == null) { return; }

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

    }

}
