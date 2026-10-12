namespace Lockyaw.VoiceChat {

    internal static class FrameSequence {

        internal static int Compare(uint left, uint right) => unchecked((int)(left - right));

        internal static uint GetDistance(uint newer, uint older) => unchecked(newer - older);

    }

}
