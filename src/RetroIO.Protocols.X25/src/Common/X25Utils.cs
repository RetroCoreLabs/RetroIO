//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Common;
namespace RetroIO.Protocols.X25
{
    /// <summary>
    /// Utility methods for X.25 packet handling.
    /// </summary>
    public static class X25Utils
    {
        /// <summary>
        /// Smallest X.25 packet header size in bytes, used as the default header size when extracting a payload.
        /// </summary>
        public const int MinHeaderSize = 3;

        /// <summary>
        /// Extract user data payload from X.25 packet.
        /// </summary>
        public static byte[] ExtractPayload(byte[] packetData, int length, int headerSize = MinHeaderSize)
        {
            if (packetData == null || length <= headerSize)
                return Array.Empty<byte>();

            int payloadLength = length - headerSize;
            byte[] payload = new byte[payloadLength];
            Array.Copy(packetData, headerSize, payload, 0, payloadLength);
            return payload;
        }

        /// <summary>
        /// Extract GFI (General Format Identifier) from first byte.
        /// </summary>
        public static byte ExtractGFI(byte firstByte)
        {
            return (byte)((firstByte >> 4) & 0x0F);
        }

        /// <summary>
        /// Extract LCN (Logical Channel Number) from first two bytes.
        /// </summary>
        public static ushort ExtractLCN(byte byte1, byte byte2)
        {
            return (ushort)(((byte1 & 0x0F) << 8) | byte2);
        }

        /// <summary>
        /// Extract packet type identifier from third byte.
        /// </summary>
        public static byte ExtractPTI(byte thirdByte)
        {
            return thirdByte;
        }

        /// <summary>
        /// Build X.25 header bytes.
        /// </summary>
        public static void BuildHeader(Span<byte> header, byte gfi, ushort lcn, byte pti)
        {
            if (header.Length < MinHeaderSize)
                throw new ArgumentException("Header span must be at least 3 bytes");

            header[0] = (byte)((gfi << 4) | ((lcn >> 8) & 0x0F));
            header[1] = (byte)(lcn & 0xFF);
            header[2] = pti;
        }

        /// <summary>
        /// Validate X.25 packet and return error reason if invalid, null if valid.
        /// Extracted from ProtocolRuntime for reuse.
        /// </summary>
        public static string? ValidatePacket(Models.X25Packet? packet)
        {
            // Basic packet validation
            if (packet == null)
            {
                return "X25_Null_Packet";
            }

            // Validate packet type
            if (packet.PacketType == Models.X25PacketType.Unknown)
            {
                return "X25_Unknown_Packet_Type";
            }

            // Validate LCN range (0-4095 for standard X.25)
            if (packet.LCN < 0 || packet.LCN > 4095)
            {
                return "X25_Invalid_LCN_Range";
            }

            // Validate data packet length
            if (packet.PacketType == Models.X25PacketType.Data)
            {
                if (packet.FullPacket == null || packet.FullPacket.Length < MinHeaderSize)
                {
                    return "X25_Invalid_Data_Packet_Length";
                }
            }

            return null; // No errors
        }
    }
}
