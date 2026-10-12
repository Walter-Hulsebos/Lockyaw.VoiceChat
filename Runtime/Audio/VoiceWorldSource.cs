namespace Lockyaw.VoiceChat {

    internal readonly struct VoiceWorldSource {

        internal readonly WorldAudioHistory History;
        internal readonly long InitialPosition;
        internal readonly float Gain;

        internal VoiceWorldSource(WorldAudioHistory history, long initialPosition, float gain) {
            History = history;
            InitialPosition = initialPosition;
            Gain = gain;
        }

    }

}
