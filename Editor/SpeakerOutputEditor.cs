using UnityEditor;
using UnityEngine;

namespace Lockyaw.VoiceChat.Editor {

    [CustomEditor(typeof(SpeakerOutput))]
    internal sealed class SpeakerOutputEditor : UnityEditor.Editor {

        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            SpeakerOutput speaker = target as SpeakerOutput;
            if (!Application.isPlaying || speaker == null) { return; }

            VoicePlaybackDiagnostics diagnostics = speaker.GetDiagnostics();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live Playback Quality", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Output Format", $"{diagnostics.OutputSampleRate} Hz");
            EditorGUILayout.LabelField("DSP Buffer", $"{diagnostics.DspBufferSamples} samples × {diagnostics.DspBufferCount}");
            EditorGUILayout.LabelField("Audio Callback", $"{diagnostics.AudioCallbackSamples} samples");
            EditorGUILayout.LabelField("Buffered Audio", $"{diagnostics.BufferedMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Received Frames", diagnostics.ReceivedFrames.ToString());
            EditorGUILayout.LabelField("Missing Frames", diagnostics.MissingFrames.ToString());
            EditorGUILayout.LabelField("Intentional Silence", $"{diagnostics.IntentionalSilenceMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Late Frames", diagnostics.LateFrames.ToString());
            EditorGUILayout.LabelField("Reordered Frames", diagnostics.ReorderedFrames.ToString());
            EditorGUILayout.LabelField("Duplicate Frames", diagnostics.DuplicateFrames.ToString());
            EditorGUILayout.LabelField("Finalized Gaps", diagnostics.FinalizedGaps.ToString());
            EditorGUILayout.LabelField("Queue Pressure Events", diagnostics.QueuePressureEvents.ToString());
            EditorGUILayout.LabelField("Rejected Sequence Advances", diagnostics.RejectedSequenceAdvances.ToString());
            EditorGUILayout.LabelField("FEC Decode Attempts", diagnostics.ForwardErrorCorrectionAttempts.ToString());
            EditorGUILayout.LabelField("Pending Encoded Frames", diagnostics.PendingEncodedFrames.ToString());
            EditorGUILayout.LabelField("Peak Pending Frames", diagnostics.MaximumPendingEncodedFrames.ToString());
            EditorGUILayout.LabelField("Concealed Frames", diagnostics.ConcealedFrames.ToString());
            EditorGUILayout.LabelField("Decoder Resets", diagnostics.DecoderResets.ToString());
            EditorGUILayout.LabelField("Prebuffer Wait Callbacks", diagnostics.PrebufferWaits.ToString());
            EditorGUILayout.LabelField("Rebuffer Events", diagnostics.RebufferEvents.ToString());
            EditorGUILayout.LabelField("Concurrent Read Aborts", diagnostics.ConcurrentReadAborts.ToString());
            EditorGUILayout.LabelField("Partial Reads", diagnostics.PartialReads.ToString());
            EditorGUILayout.LabelField("Overflowed Audio", $"{diagnostics.OverflowedMilliseconds:F1} ms");
            if (GUILayout.Button("Reset Playback Measurements")) {
                speaker.ResetDiagnostics();
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

    }

}
