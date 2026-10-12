namespace Lockyaw.VoiceChat {

    public readonly struct VoiceDeviceDescriptor {

        public VoiceDeviceId Id { get; }
        public string DisplayName { get; }
        public bool IsDefault { get; }

        public VoiceDeviceDescriptor(VoiceDeviceId id, string displayName, bool isDefault) {
            Id = id;
            DisplayName = displayName == null ? "" : displayName;
            IsDefault = isDefault;
        }

    }

}
