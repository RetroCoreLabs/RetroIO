//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Common
{
    /// <summary>
    /// Utility methods for XOT (X.25 Over TCP) packet handling.
    /// Standardizes header construction across XOTLayer and XotSerialBridge.
    /// </summary>
    public static class XotUtils
    {
        /// <summary>
        /// The size of the XOT header in bytes, which is 4.
        /// </summary>
        public const int HeaderSize = 4;
        /// <summary>
        /// The version value of the XOT header, which is 0.
        /// </summary>
        public const ushort Version = 0;

        /// <summary>
        /// Build XOT header into span (stack-allocated, zero-copy).
        /// </summary>
        public static void BuildHeader(Span<byte> header, int packetLength)
        {
            if (header.Length < HeaderSize)
                throw new ArgumentException("Header span must be at least 4 bytes");

            header[0] = 0x00; // Version high byte
            header[1] = 0x00; // Version low byte
            header[2] = (byte)(packetLength >> 8);   // Length high byte
            header[3] = (byte)(packetLength & 0xFF); // Length low byte
        }

        /// <summary>
        /// Build XOT header as byte array.
        /// </summary>
        public static byte[] BuildHeaderArray(int packetLength)
        {
            byte[] header = new byte[HeaderSize];
            header[0] = 0x00;
            header[1] = 0x00;
            header[2] = (byte)(packetLength >> 8);
            header[3] = (byte)(packetLength & 0xFF);
            return header;
        }

        /// <summary>
        /// Parse XOT header to extract packet length.
        /// </summary>
        public static (ushort version, ushort length) ParseHeader(ReadOnlySpan<byte> header)
        {
            if (header.Length < HeaderSize)
                throw new ArgumentException("Header span must be at least 4 bytes");

            ushort version = (ushort)((header[0] << 8) | header[1]);
            ushort length = (ushort)((header[2] << 8) | header[3]);
            return (version, length);
        }

        /// <summary>
        /// Build complete XOT packet (header + X.25 data).
        /// </summary>
        public static byte[] BuildPacket(byte[] x25Data, int length = -1)
        {
            int dataLength = length >= 0 ? length : x25Data.Length;
            byte[] packet = new byte[HeaderSize + dataLength];

            packet[0] = 0x00;
            packet[1] = 0x00;
            packet[2] = (byte)(dataLength >> 8);
            packet[3] = (byte)(dataLength & 0xFF);

            Array.Copy(x25Data, 0, packet, HeaderSize, dataLength);
            return packet;
        }
    }
}
