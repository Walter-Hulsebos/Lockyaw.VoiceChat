namespace Lockyaw.VoiceChat.Editor {

    internal readonly struct VoiceSetupResult {

        internal bool Succeeded { get; }
        internal string Message { get; }

        internal VoiceSetupResult(bool succeeded, string message) {
            Succeeded = succeeded;
            Message = message;
        }

        internal static VoiceSetupResult Success(string message) => new(true, message);

        internal static VoiceSetupResult Failure(string message) => new(false, message);

    }

}
