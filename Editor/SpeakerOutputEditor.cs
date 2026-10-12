using UnityEditor;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(SpeakerOutput))]
    internal sealed class SpeakerOutputEditor : UnityEditor.Editor {

        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            if (!EditorApplication.isPlaying) { return; }
            SpeakerOutput speaker = target as SpeakerOutput;
            if (speaker == null) { return; }
            VoicePlaybackDiagnostics diagnostics = speaker.GetDiagnostics();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Audio Callback", diagnostics.AudioCallbackSamples.ToString());
            EditorGUILayout.LabelField("Output Rate", diagnostics.OutputSampleRate.ToString());
            EditorGUILayout.LabelField("Buffered", $"{diagnostics.BufferedMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Target", $"{diagnostics.TargetMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Arrival Jitter", $"{diagnostics.JitterMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Output Buffer", $"{diagnostics.OutputBufferMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Loopback Latency", diagnostics.LoopbackLatencyMilliseconds > 0f ? $"{diagnostics.LoopbackLatencyMilliseconds + diagnostics.OutputBufferMilliseconds:F1} ms" : "Not measured");
            EditorGUILayout.LabelField("Received Frames", diagnostics.ReceivedFrames.ToString());
            EditorGUILayout.LabelField("Ingress Drops", diagnostics.QueueDrops.ToString());
            EditorGUILayout.LabelField("Prebuffer Waits", diagnostics.PrebufferWaits.ToString());
            EditorGUILayout.LabelField("Underruns", diagnostics.RebufferEvents.ToString());
            EditorGUILayout.LabelField("Partial Reads", diagnostics.PartialReads.ToString());
            EditorGUILayout.LabelField("Overflow", $"{diagnostics.OverflowedMilliseconds:F1} ms");
        }

    }

}
