using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Lockyaw.VoiceChat {

    public sealed class VoiceConnectionAuthentication : IDisposable {

        // Magic, version, kind, epoch, peer and sequence. The payload length is the remainder of the message.
        public const int HEADER_BYTES = 30;
        // HMAC-SHA256 truncated to 128 bits.
        public const int AUTHENTICATION_BYTES = 16;
        public const int MAXIMUM_MESSAGE_BYTES = HEADER_BYTES + VoicePacketCodec.MAXIMUM_PACKET_BYTES + AUTHENTICATION_BYTES;
        public const byte HELLO = 1;
        public const byte ACKNOWLEDGMENT = 2;
        public const byte MEDIA = 3;

        private const uint MAGIC = 0x4341594C;
        private readonly HMACSHA256 authentication;
        private readonly ulong epoch;
        private readonly ulong localPeerId;
        private readonly ulong remotePeerId;
        private readonly byte[] hash = new byte[32];
        private ulong sentSequence;
        private ulong receivedSequence;
        private ulong receivedSequences;

        public VoiceConnectionAuthentication(ulong epoch, ulong localPeerId, ulong remotePeerId, byte[] secret) {
            if (secret == null || secret.Length != 32) { throw new ArgumentException("A 32-byte binding secret is required.", nameof(secret)); }
            this.epoch = epoch;
            this.localPeerId = localPeerId;
            this.remotePeerId = remotePeerId;
            authentication = new HMACSHA256(secret);
        }

        public void Dispose() {
            authentication.Dispose();
        }

        public bool TryWrite(byte kind, VoicePacket packet, Span<byte> destination, out int written) {
            written = 0;
            if (kind < HELLO || kind > MEDIA || destination.Length < MAXIMUM_MESSAGE_BYTES) { return false; }
            int payloadLength = 0;
            if (kind == MEDIA && (packet.Epoch != epoch || !VoicePacketCodec.TryWrite(packet, destination.Slice(HEADER_BYTES), out payloadLength))) { return false; }
            BinaryPrimitives.WriteUInt32LittleEndian(destination, MAGIC);
            destination[4] = (byte)VoicePacketCodec.PROTOCOL_VERSION;
            destination[5] = kind;
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(6), epoch);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(14), localPeerId);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(22), ++sentSequence);
            int signedLength = HEADER_BYTES + payloadLength;
            if (!authentication.TryComputeHash(destination.Slice(0, signedLength), hash, out int hashLength) || hashLength != hash.Length) { return false; }
            hash.AsSpan(0, AUTHENTICATION_BYTES).CopyTo(destination.Slice(signedLength));
            written = signedLength + AUTHENTICATION_BYTES;
            return true;
        }

        public bool TryRead(ReadOnlySpan<byte> source, out byte kind, out VoicePacket packet) {
            kind = 0;
            packet = default;
            if (!TryReadPeerId(source, out ulong peerId) || peerId != remotePeerId || BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(6)) != epoch) { return false; }
            int signedLength = source.Length - AUTHENTICATION_BYTES;
            int payloadLength = signedLength - HEADER_BYTES;
            if (!authentication.TryComputeHash(source.Slice(0, signedLength), hash, out int hashLength) || hashLength != hash.Length ||
                !CryptographicOperations.FixedTimeEquals(hash.AsSpan(0, AUTHENTICATION_BYTES), source.Slice(signedLength))) { return false; }
            byte receivedKind = source[5];
            if (receivedKind < HELLO || receivedKind > MEDIA || receivedKind != MEDIA && payloadLength != 0) { return false; }
            if (receivedKind == MEDIA && (!VoicePacketCodec.TryRead(source.Slice(HEADER_BYTES, payloadLength), out packet) || packet.Epoch != epoch)) { return false; }
            ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(22));
            if (!AcceptSequence(sequence)) { return false; }
            kind = receivedKind;
            return true;
        }

        public static bool TryReadPeerId(ReadOnlySpan<byte> source, out ulong peerId) {
            peerId = ulong.MaxValue;
            if (source.Length < HEADER_BYTES + AUTHENTICATION_BYTES || source.Length > MAXIMUM_MESSAGE_BYTES ||
                BinaryPrimitives.ReadUInt32LittleEndian(source) != MAGIC || source[4] != VoicePacketCodec.PROTOCOL_VERSION) { return false; }
            peerId = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(14));
            return true;
        }

        private bool AcceptSequence(ulong sequence) {
            if (sequence == 0) { return false; }
            if (sequence > receivedSequence) {
                ulong advance = sequence - receivedSequence;
                receivedSequences = advance >= 64 ? 1UL : receivedSequences << (int)advance | 1UL;
                receivedSequence = sequence;
                return true;
            }
            ulong age = receivedSequence - sequence;
            if (age >= 64) { return false; }
            ulong bit = 1UL << (int)age;
            if ((receivedSequences & bit) != 0) { return false; }
            receivedSequences |= bit;
            return true;
        }

    }

}
