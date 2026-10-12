using System;
using System.Buffers.Binary;

namespace Lockyaw.VoiceChat {

    public static class VoicePacketCodec {

        public const ushort PROTOCOL_VERSION = 2;
        public const int MINIMUM_HEADER_BYTES = FIXED_HEADER_BYTES + 4;
        public const int MAXIMUM_HEADER_BYTES = FIXED_HEADER_BYTES + MAXIMUM_ROUTE_BYTES + MAXIMUM_BROADCAST_BYTES;
        public const int MAXIMUM_PACKET_BYTES = MAXIMUM_HEADER_BYTES + VoiceFrame.MAXIMUM_PAYLOAD_BYTES;

        // Fixed fields: version, kind, flags, role, epoch, stream, sequence, timestamp, sample count and level.
        private const int FIXED_HEADER_BYTES = 27;
        // Variable fields: route object, source client, membership group and revision.
        private const int MAXIMUM_ROUTE_BYTES = 10 + 10 + 5 + 3;
        // Broadcast fields: channel, source object, origin object, origin client and hop count.
        private const int MAXIMUM_BROADCAST_BYTES = 5 + 10 + 10 + 10 + 1;
        private const byte BROADCAST_FLAG = 1;
        private const byte TALK_SPURT_FLAG = 2;
        private const byte KNOWN_FLAGS = BROADCAST_FLAG | TALK_SPURT_FLAG;

        public static bool TryWrite(VoicePacket packet, Span<byte> destination, out int writtenBytes) {
            writtenBytes = 0;
            if (!packet.IsValid || !packet.CanTransmit || destination.Length < MAXIMUM_HEADER_BYTES + packet.Frame.PayloadLength) { return false; }
            VoiceFrame frame = packet.Frame;
            destination[0] = (byte)PROTOCOL_VERSION;
            destination[1] = (byte)packet.Kind;
            destination[2] = (byte)((packet.HasBroadcastRoute ? BROADCAST_FLAG : 0) | (frame.StartsTalkSpurt ? TALK_SPURT_FLAG : 0));
            destination[3] = (byte)packet.RouteId.Role;
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(4), packet.Epoch);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12), frame.StreamId);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16), frame.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(20), frame.SampleTimestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(24), frame.SampleCount);
            destination[26] = frame.Level;
            int position = FIXED_HEADER_BYTES;
            WriteVarint(destination, ref position, packet.RouteId.ObjectId);
            WriteVarint(destination, ref position, frame.SourceClientId);
            WriteVarint(destination, ref position, ZigZag(frame.Membership.Group));
            WriteVarint(destination, ref position, frame.Membership.Revision);
            if (packet.HasBroadcastRoute) {
                BroadcastRoute route = packet.BroadcastRoute;
                WriteVarint(destination, ref position, ZigZag(route.Channel));
                WriteVarint(destination, ref position, route.SourceNetworkObjectId);
                WriteVarint(destination, ref position, route.OriginNetworkObjectId);
                WriteVarint(destination, ref position, route.OriginClientId);
                destination[position++] = route.HopCount;
            }
            position += frame.CopyPayloadTo(destination.Slice(position));
            writtenBytes = position;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out VoicePacket packet) {
            packet = default;
            if (source.Length < MINIMUM_HEADER_BYTES || source.Length > MAXIMUM_PACKET_BYTES || source[0] != PROTOCOL_VERSION) { return false; }
            byte flags = source[2];
            if ((flags & ~KNOWN_FLAGS) != 0) { return false; }
            int position = FIXED_HEADER_BYTES;
            if (!TryReadVarint(source, ref position, out ulong objectId) ||
                !TryReadVarint(source, ref position, out ulong sourceClientId) ||
                !TryReadVarint(source, ref position, out ulong group) || group > uint.MaxValue ||
                !TryReadVarint(source, ref position, out ulong revision) || revision > ushort.MaxValue) {
                return false;
            }
            BroadcastRoute route = default;
            bool hasBroadcastRoute = (flags & BROADCAST_FLAG) != 0;
            if (hasBroadcastRoute) {
                if (!TryReadVarint(source, ref position, out ulong channel) || channel > uint.MaxValue ||
                    !TryReadVarint(source, ref position, out ulong sourceObjectId) ||
                    !TryReadVarint(source, ref position, out ulong originObjectId) ||
                    !TryReadVarint(source, ref position, out ulong originClientId) ||
                    position >= source.Length) {
                    return false;
                }
                route = new BroadcastRoute(UnZigZag((uint)channel), sourceObjectId, originObjectId, source[position++], originClientId);
            }
            if (source.Length - position > VoiceFrame.MAXIMUM_PAYLOAD_BYTES) { return false; }
            VoiceRouteId routeId = new(objectId, (VoiceRouteRole)source[3]);
            if (!routeId.IsValid) { return false; }
            VoiceFrame frame = new(
                sourceClientId,
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(20)),
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(24)),
                source[26],
                source.Slice(position),
                new ConversationMembership(UnZigZag((uint)group), (ushort)revision)) {
                StartsTalkSpurt = (flags & TALK_SPURT_FLAG) != 0
            };
            VoicePacketKind kind = (VoicePacketKind)source[1];
            ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(4));
            packet = hasBroadcastRoute ? new VoicePacket(kind, epoch, routeId, frame, route) : new VoicePacket(kind, epoch, routeId, frame);
            return packet.IsValid;
        }

        private static uint ZigZag(int value) => unchecked((uint)((value << 1) ^ (value >> 31)));

        private static int UnZigZag(uint value) => unchecked((int)(value >> 1) ^ -(int)(value & 1));

        private static void WriteVarint(Span<byte> destination, ref int position, ulong value) {
            while (value >= 0x80) {
                destination[position++] = (byte)(value | 0x80);
                value >>= 7;
            }
            destination[position++] = (byte)value;
        }

        // Accepts only the shortest encoding so each header has exactly one valid byte representation.
        private static bool TryReadVarint(ReadOnlySpan<byte> source, ref int position, out ulong value) {
            value = 0;
            for (int shift = 0; shift < 64 && position < source.Length; shift += 7) {
                byte current = source[position++];
                if (shift == 63 && current > 1) { return false; }
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) != 0) { continue; }
                return shift == 0 || current != 0;
            }
            return false;
        }

    }

}
