namespace Lockyaw.VoiceChat {

    public interface ISpeakerNameResolver {

        bool TryResolveDisplayName(SpeakerIdentity identity, out string displayName);

    }

}
