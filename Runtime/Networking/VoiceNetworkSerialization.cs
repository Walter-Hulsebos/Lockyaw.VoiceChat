using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat {

    internal static class VoiceNetworkSerialization {

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Initialize() {
            UserNetworkVariableSerialization<ConversationMembership>.WriteValue = WriteMembership;
            UserNetworkVariableSerialization<ConversationMembership>.ReadValue = ReadMembership;
            UserNetworkVariableSerialization<ConversationMembership>.DuplicateValue = DuplicateMembership;
        }

        private static void WriteMembership(FastBufferWriter writer, in ConversationMembership value) {
            writer.WriteValueSafe(value.Group);
            writer.WriteValueSafe(value.Revision);
        }

        private static void ReadMembership(FastBufferReader reader, out ConversationMembership value) {
            reader.ReadValueSafe(out int group);
            reader.ReadValueSafe(out ushort revision);
            value = new ConversationMembership(group, revision);
        }

        private static void DuplicateMembership(in ConversationMembership value, ref ConversationMembership duplicatedValue) {
            duplicatedValue = value;
        }

    }

}
