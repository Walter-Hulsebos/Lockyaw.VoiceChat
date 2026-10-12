using System;

namespace Lockyaw.VoiceChat {

    public unsafe struct VoiceFrame : IEquatable<VoiceFrame> {

        public const int MINIMUM_PAYLOAD_BYTES = 3;
        public const int MAXIMUM_PAYLOAD_BYTES = 480;
        public const ulong UNASSIGNED_SOURCE_CLIENT_ID = ulong.MaxValue;

        public ulong SourceClientId;
        public uint StreamId;
        public uint Sequence;
        public uint SampleTimestamp;
        public ushort SampleCount;
        public byte Level;
        public ConversationMembership Membership;
        // Set on the first frames after the sender resumes transmitting, so receivers can separate pauses from network delay.
        public bool StartsTalkSpurt;
        public int PayloadLength => payloadLength;
        public bool IsSilence => hasValidPayloadLength && payloadLength == 0;
        public bool IsValid => hasValidPayloadLength && Membership.IsValid && IsSupportedSampleCount(SampleCount) &&
                               (IsSilence || payloadLength >= MINIMUM_PAYLOAD_BYTES && payloadLength <= MAXIMUM_PAYLOAD_BYTES);

        private fixed byte payload[MAXIMUM_PAYLOAD_BYTES];
        private ushort payloadLength;
        private bool hasValidPayloadLength;

        public VoiceFrame(uint sequence, uint sampleTimestamp, ushort sampleCount, byte level, ReadOnlySpan<byte> encodedData)
            : this(UNASSIGNED_SOURCE_CLIENT_ID, 0, sequence, sampleTimestamp, sampleCount, level, encodedData, new ConversationMembership(ConversationMembership.EVERYONE, 0)) { }

        public VoiceFrame(uint sequence, uint sampleTimestamp, ushort sampleCount, byte level, ReadOnlySpan<byte> encodedData, ConversationMembership membership)
            : this(UNASSIGNED_SOURCE_CLIENT_ID, 0, sequence, sampleTimestamp, sampleCount, level, encodedData, membership) { }

        public VoiceFrame(uint streamId, uint sequence, uint sampleTimestamp, ushort sampleCount, byte level, ReadOnlySpan<byte> encodedData, ConversationMembership membership)
            : this(UNASSIGNED_SOURCE_CLIENT_ID, streamId, sequence, sampleTimestamp, sampleCount, level, encodedData, membership) { }

        public VoiceFrame(ulong sourceClientId, uint streamId, uint sequence, uint sampleTimestamp, ushort sampleCount, byte level, ReadOnlySpan<byte> encodedData, ConversationMembership membership) {
            this = default;
            SourceClientId = sourceClientId;
            StreamId = streamId;
            Sequence = sequence;
            SampleTimestamp = sampleTimestamp;
            SampleCount = sampleCount;
            Level = level;
            Membership = membership;
            hasValidPayloadLength = encodedData.Length <= MAXIMUM_PAYLOAD_BYTES;
            payloadLength = (ushort)Math.Min(encodedData.Length, MAXIMUM_PAYLOAD_BYTES);
            fixed (byte* destination = payload) {
                encodedData.Slice(0, payloadLength).CopyTo(new Span<byte>(destination, payloadLength));
            }
        }

        public int CopyPayloadTo(byte[] destination) {
            return destination == null ? 0 : CopyPayloadTo(destination.AsSpan());
        }

        public int CopyPayloadTo(Span<byte> destination) {
            int bytesToCopy = Math.Min(destination.Length, payloadLength);
            fixed (byte* source = payload) {
                new ReadOnlySpan<byte>(source, bytesToCopy).CopyTo(destination);
            }
            return bytesToCopy;
        }

        public VoiceFrame WithMembership(ConversationMembership membership) {
            VoiceFrame frame = this;
            frame.Membership = membership;
            return frame;
        }

        public bool Equals(VoiceFrame other) {
            if (SourceClientId != other.SourceClientId || StreamId != other.StreamId || Sequence != other.Sequence ||
                SampleTimestamp != other.SampleTimestamp || SampleCount != other.SampleCount || Level != other.Level ||
                Membership != other.Membership || StartsTalkSpurt != other.StartsTalkSpurt ||
                hasValidPayloadLength != other.hasValidPayloadLength || payloadLength != other.payloadLength) {
                return false;
            }
            fixed (byte* source = payload) {
                for (int i = 0; i < payloadLength; i++) {
                    if (source[i] != other.payload[i]) { return false; }
                }
            }
            return true;
        }

        public override bool Equals(object obj) => obj is VoiceFrame frame && Equals(frame);

        public override int GetHashCode() => HashCode.Combine(SourceClientId, StreamId, Sequence, SampleTimestamp, SampleCount, Level, Membership, payloadLength);

        public static bool IsSupportedSampleCount(int sampleCount) {
            return sampleCount == (int)VoiceFrameLength.FiveMilliseconds ||
                   sampleCount == (int)VoiceFrameLength.TenMilliseconds ||
                   sampleCount == (int)VoiceFrameLength.TwentyMilliseconds ||
                   sampleCount == (int)VoiceFrameLength.FortyMilliseconds;
        }

        public static bool operator ==(VoiceFrame left, VoiceFrame right) => left.Equals(right);

        public static bool operator !=(VoiceFrame left, VoiceFrame right) => !left.Equals(right);

    }

}
