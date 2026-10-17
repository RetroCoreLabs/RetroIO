//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tnfs.Protocol;


/// <summary>
/// TNFS protocol endian conversion helpers.
/// TNFS uses little-endian byte order for all multi-byte values.
/// </summary>
public static class TnfsEndian
{
    /// <summary>
    /// Read ushort from little-endian byte array.
    /// </summary>
    /// <param name="buffer">
    /// Source buffer
    /// </param>
    /// <param name="offset">
    /// Offset in buffer
    /// </param>
    /// <returns>
    /// Unsigned 16-bit integer
    /// </returns>
    public static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
    }

    /// <summary>
    /// Read uint from little-endian byte array.
    /// </summary>
    /// <param name="buffer">
    /// Source buffer
    /// </param>
    /// <param name="offset">
    /// Offset in buffer
    /// </param>
    /// <returns>
    /// Unsigned 32-bit integer
    /// </returns>
    public static uint ReadUInt32(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset] |
                     (buffer[offset + 1] << 8) |
                     (buffer[offset + 2] << 16) |
                     (buffer[offset + 3] << 24));
    }

    /// <summary>
    /// Write ushort to little-endian byte array.
    /// </summary>
    /// <param name="buffer">
    /// Destination buffer
    /// </param>
    /// <param name="offset">
    /// Offset in buffer
    /// </param>
    /// <param name="value">
    /// Value to write
    /// </param>
    public static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// Write uint to little-endian byte array.
    /// </summary>
    /// <param name="buffer">
    /// Destination buffer
    /// </param>
    /// <param name="offset">
    /// Offset in buffer
    /// </param>
    /// <param name="value">
    /// Value to write
    /// </param>
    public static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>
    /// Write int to little-endian byte array.
    /// </summary>
    /// <param name="buffer">
    /// Destination buffer
    /// </param>
    /// <param name="offset">
    /// Offset in buffer
    /// </param>
    /// <param name="value">
    /// Value to write
    /// </param>
    public static void WriteInt32(byte[] buffer, int offset, int value)
    {
        uint unsigned = unchecked((uint)value);
        buffer[offset] = (byte)(unsigned & 0xFF);
        buffer[offset + 1] = (byte)((unsigned >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((unsigned >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((unsigned >> 24) & 0xFF);
    }
}
