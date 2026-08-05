using System;

namespace Lockyaw.VoiceChat {

    [Flags]
    public enum VoiceStateFlags : byte {
        None = 0,
        InputMuted = 1 << 0,
        Deafened = 1 << 1,
        Speaking = 1 << 2
    }

}
