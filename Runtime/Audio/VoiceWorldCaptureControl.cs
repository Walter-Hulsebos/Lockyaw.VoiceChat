namespace Lockyaw.VoiceChat {

    internal sealed class VoiceWorldCaptureControl {

        internal readonly bool Enabled;
        internal readonly ulong SourceClientId;
        internal readonly VoiceWorldSource[] Sources;

        internal VoiceWorldCaptureControl(bool enabled, ulong sourceClientId, VoiceWorldSource[] sources) {
            Enabled = enabled;
            SourceClientId = sourceClientId;
            Sources = sources;
        }

    }

}
