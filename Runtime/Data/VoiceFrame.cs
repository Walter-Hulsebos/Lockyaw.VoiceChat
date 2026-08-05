using System;
using Unity.Collections;
using Unity.Netcode;

namespace Lockyaw.VoiceChat {

    public struct VoiceFrame : INetworkSerializable, IEquatable<VoiceFrame> {

        public const int MINIMUM_PAYLOAD_BYTES = 3;
        public const int MAXIMUM_PAYLOAD_BYTES = 480;
        public const ulong UNASSIGNED_SOURCE_CLIENT_ID = ulong.MaxValue;

        private const int BASE_SERIALIZED_BYTES = 27;
        private const int SAMPLE_TIMESTAMP_EXTENSION_BYTES = 8;
        private const uint SAMPLE_TIMESTAMP_EXTENSION_MARKER = 0x4C565431;

        public ulong SourceClientId;
        public uint StreamId;
        public uint Sequence;
        public uint SampleTimestamp;
        public ushort SampleCount;
        public byte Level;
        public ConversationMembership Membership;
        public int PayloadLength => payload.Length;
        public bool IsSilence => hasValidPayloadLength && payload.Length == 0;
        public bool IsValid => hasValidPayloadLength &&
                               Membership.IsValid &&
                               IsSupportedSampleCount(SampleCount) &&
                               (IsSilence || payload.Length >= MINIMUM_PAYLOAD_BYTES && payload.Length <= MAXIMUM_PAYLOAD_BYTES);

        private FixedList512Bytes<byte> payload;
        private bool hasValidPayloadLength;

        public VoiceFrame(uint sequence, uint sampleTimestamp, ushort sampleCount, byte level, ReadOnlySpan<byte> encodedData)
            : this(UNASSIGNED_SOURCE_CLIENT_ID, 0, sequence, sampleTimestamp, sampleCount, level, encodedData, new ConversationMembership(ConversationMembership.EVERYONE, 0)) { }

        public VoiceFrame(uint sequence, uint sampleTimestamp, ushort sampleCount, byte level, ReadOnlySpan<byte> encodedData, ConversationMembership membership)
            : this(UNASSIGNED_SOURCE_CLIENT_ID, 0, sequence, sampleTimestamp, sampleCount, level, encodedData, membership) { }

        public VoiceFrame(
            uint streamId,
            uint sequence,
            uint sampleTimestamp,
            ushort sampleCount,
            byte level,
            ReadOnlySpan<byte> encodedData,
            ConversationMembership membership)
            : this(UNASSIGNED_SOURCE_CLIENT_ID, streamId, sequence, sampleTimestamp, sampleCount, level, encodedData, membership) { }

        public VoiceFrame(
            ulong sourceClientId,
            uint streamId,
            uint sequence,
            uint sampleTimestamp,
            ushort sampleCount,
            byte level,
            ReadOnlySpan<byte> encodedData,
            ConversationMembership membership) {
            SourceClientId = sourceClientId;
            StreamId = streamId;
            Sequence = sequence;
            SampleTimestamp = sampleTimestamp;
            SampleCount = sampleCount;
            Level = level;
            Membership = membership;
            payload = default;
            hasValidPayloadLength = encodedData.Length <= MAXIMUM_PAYLOAD_BYTES;

            int bytesToCopy = Math.Min(encodedData.Length, MAXIMUM_PAYLOAD_BYTES);
            for (int i = 0; i < bytesToCopy; i++) {
                payload.Add(encodedData[i]);
            }
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter {
            if (serializer.IsReader && !serializer.PreCheck(BASE_SERIALIZED_BYTES)) {
                InvalidateSerializedFrame(serializer.GetFastBufferReader());
                return;
            }

            serializer.SerializeValue(ref SourceClientId);
            serializer.SerializeValue(ref StreamId);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref SampleCount);
            serializer.SerializeValue(ref Level);
            serializer.SerializeValue(ref Membership);

            ushort payloadLength = (ushort)payload.Length;
            serializer.SerializeValue(ref payloadLength);

            if (serializer.IsReader) {
                payload.Clear();
                FastBufferReader payloadReader = serializer.GetFastBufferReader();
                if (!payloadReader.TryBeginRead(payloadLength)) {
                    InvalidateSerializedFrame(payloadReader);
                    return;
                }
                if (payloadLength > MAXIMUM_PAYLOAD_BYTES) {
                    payloadReader.Seek(payloadReader.Position + payloadLength);
                    hasValidPayloadLength = false;
                    return;
                }
                hasValidPayloadLength = true;
            }

            for (int i = 0; i < payloadLength; i++) {
                byte value = serializer.IsReader ? (byte)0 : payload[i];
                serializer.SerializeValue(ref value);
                if (serializer.IsReader) {
                    payload.Add(value);
                }
            }

            if (serializer.IsWriter) {
                uint extensionMarker = SAMPLE_TIMESTAMP_EXTENSION_MARKER;
                serializer.SerializeValue(ref extensionMarker);
                serializer.SerializeValue(ref SampleTimestamp);
                return;
            }

            FastBufferReader extensionReader = serializer.GetFastBufferReader();
            int extensionPosition = extensionReader.Position;
            if (!extensionReader.TryBeginRead(SAMPLE_TIMESTAMP_EXTENSION_BYTES)) {
                SampleTimestamp = unchecked(Sequence * SampleCount);
                return;
            }

            uint receivedExtensionMarker = 0;
            serializer.SerializeValue(ref receivedExtensionMarker);
            if (receivedExtensionMarker != SAMPLE_TIMESTAMP_EXTENSION_MARKER) {
                extensionReader.Seek(extensionPosition);
                SampleTimestamp = unchecked(Sequence * SampleCount);
                return;
            }
            serializer.SerializeValue(ref SampleTimestamp);
        }

        public int CopyPayloadTo(byte[] destination) {
            if (destination == null) { return 0; }

            int bytesToCopy = Math.Min(destination.Length, payload.Length);
            for (int i = 0; i < bytesToCopy; i++) {
                destination[i] = payload[i];
            }
            return bytesToCopy;
        }

        public VoiceFrame WithMembership(ConversationMembership membership) {
            VoiceFrame frame = this;
            frame.Membership = membership;
            return frame;
        }

        public bool Equals(VoiceFrame other) {
            return SourceClientId == other.SourceClientId &&
                   StreamId == other.StreamId &&
                   Sequence == other.Sequence &&
                   SampleTimestamp == other.SampleTimestamp &&
                   SampleCount == other.SampleCount &&
                   Level == other.Level &&
                   Membership == other.Membership &&
                   hasValidPayloadLength == other.hasValidPayloadLength &&
                   payload.Equals(other.payload);
        }

        public override bool Equals(object obj) => obj is VoiceFrame frame && Equals(frame);

        public override int GetHashCode() {
            return HashCode.Combine(
                SourceClientId,
                StreamId,
                Sequence,
                SampleTimestamp,
                SampleCount,
                Level,
                Membership,
                HashCode.Combine(hasValidPayloadLength, payload));
        }

        public static bool IsSupportedSampleCount(int sampleCount) {
            return sampleCount == (int)VoiceFrameLength.TenMilliseconds ||
                   sampleCount == (int)VoiceFrameLength.TwentyMilliseconds ||
                   sampleCount == (int)VoiceFrameLength.FortyMilliseconds;
        }

        public static bool operator ==(VoiceFrame left, VoiceFrame right) => left.Equals(right);

        public static bool operator !=(VoiceFrame left, VoiceFrame right) => !left.Equals(right);

        private void InvalidateSerializedFrame(FastBufferReader reader) {
            SourceClientId = UNASSIGNED_SOURCE_CLIENT_ID;
            StreamId = 0;
            Sequence = 0;
            SampleTimestamp = 0;
            SampleCount = 0;
            Level = 0;
            Membership = default;
            payload.Clear();
            hasValidPayloadLength = false;
            reader.Seek(reader.Length);
        }

    }

}
