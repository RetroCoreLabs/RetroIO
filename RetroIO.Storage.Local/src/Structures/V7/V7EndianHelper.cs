//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Local.V7.Structures
{
    /// <summary>
    /// Helper for reading PDP-11 endian (middle-endian) and big-endian 32-bit values.
    ///
    /// Used by UNIX V7 filesystem for endianness handling:
    /// - PDP-11 stores 32-bit longs as: HIGH_WORD LOW_WORD (both words are little-endian)
    /// - Big-endian systems (SunOS, SPARC, Motorola 68000) store 32-bit values in pure big-endian format
    /// </summary>
    public static class V7EndianHelper
    {
        /// <summary>
        /// Reads a 32-bit signed integer in PDP-endian format.
        /// PDP-11 stores 32-bit values as two 16-bit words in swapped order.
        /// </summary>
        /// <param name="buffer">Buffer containing the data.</param>
        /// <param name="offset">Offset where the 32-bit value starts.</param>
        /// <returns>32-bit signed integer value.</returns>
        public static int ReadInt32PDP(byte[] buffer, int offset)
        {
            // Read two 16-bit little-endian words
            // In PDP-endian: bytes B0 B1 B2 B3 are stored as: B1 B0 B3 B2
            // So for value 0xAABBCCDD: stored as BB AA DD CC
            // word0 (offset 0) contains LOW 16 bits of final value
            // word1 (offset 2) contains HIGH 16 bits of final value
            ushort word0 = BitConverter.ToUInt16(buffer, offset);
            ushort word1 = BitConverter.ToUInt16(buffer, offset + 2);

            // Combine: word0 in upper 16 bits, word1 in lower 16 bits (PDP swap!)
            return (int)((word0 << 16) | word1);
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer in PDP-endian format.
        /// </summary>
        public static uint ReadUInt32PDP(byte[] buffer, int offset)
        {
            ushort word0 = BitConverter.ToUInt16(buffer, offset);
            ushort word1 = BitConverter.ToUInt16(buffer, offset + 2);

            return ((uint)word0 << 16) | word1;
        }

        /// <summary>
        /// Reads a 32-bit signed integer in big-endian format (SunOS, SPARC, Motorola 68000).
        /// </summary>
        public static int ReadInt32BE(byte[] buffer, int offset)
        {
            return (int)ReadUInt32BE(buffer, offset);
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer in big-endian format (SunOS, SPARC, Motorola 68000).
        /// </summary>
        public static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            // Big-endian: MSB first
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }

        /// <summary>
        /// Writes a 32-bit signed integer in PDP-endian format.
        /// </summary>
        public static void WriteInt32PDP(byte[] buffer, int offset, int value)
        {
            // In PDP-endian: word0 (HIGH 16 bits) goes first, word1 (LOW 16 bits) goes second
            // For 0x12345678: word0=0x1234, word1=0x5678 → stored as 34 12 78 56
            ushort word0 = (ushort)((value >> 16) & 0xFFFF);  // High 16 bits go to word 0
            ushort word1 = (ushort)(value & 0xFFFF);          // Low 16 bits go to word 1

            BitConverter.TryWriteBytes(buffer.AsSpan(offset), word0);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 2), word1);
        }

        /// <summary>
        /// Writes a 32-bit unsigned integer in PDP-endian format.
        /// </summary>
        public static void WriteUInt32PDP(byte[] buffer, int offset, uint value)
        {
            ushort word0 = (ushort)((value >> 16) & 0xFFFF);  // High 16 bits go to word 0
            ushort word1 = (ushort)(value & 0xFFFF);          // Low 16 bits go to word 1

            BitConverter.TryWriteBytes(buffer.AsSpan(offset), word0);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 2), word1);
        }

        /// <summary>
        /// Writes a 32-bit signed integer in big-endian format.
        /// </summary>
        public static void WriteInt32BE(byte[] buffer, int offset, int value)
        {
            WriteUInt32BE(buffer, offset, (uint)value);
        }

        /// <summary>
        /// Writes a 32-bit unsigned integer in big-endian format.
        /// </summary>
        public static void WriteUInt32BE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
