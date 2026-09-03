//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace RetroIO.Server.Core.Utils;

/// <summary>
/// Little-endian binary read/write helpers for TNFS protocol.
/// All TNFS multi-byte values are little-endian.
/// </summary>
public static class EndianHelper
{
    /// <summary>
    /// Read little-endian uint16 from span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUInt16(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    /// <summary>
    /// Read little-endian uint32 from span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    /// <summary>
    /// Read little-endian int32 from span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    /// <summary>
    /// Write little-endian uint16 to span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt16(Span<byte> buffer, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    }

    /// <summary>
    /// Write little-endian uint32 to span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt32(Span<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    }

    /// <summary>
    /// Read null-terminated ASCII string from buffer.
    /// </summary>
    public static string ReadNullTerminatedString(ReadOnlySpan<byte> buffer)
    {
        int nullIndex = buffer.IndexOf((byte)0);
        if (nullIndex < 0)
        {
            // No null terminator found - use entire buffer
            return System.Text.Encoding.ASCII.GetString(buffer);
        }

        return System.Text.Encoding.ASCII.GetString(buffer.Slice(0, nullIndex));
    }

    /// <summary>
    /// Write null-terminated ASCII string to buffer.
    /// Returns number of bytes written (including null terminator).
    /// </summary>
    public static int WriteNullTerminatedString(Span<byte> buffer, string value)
    {
        int bytesWritten = System.Text.Encoding.ASCII.GetBytes(value, buffer);
        buffer[bytesWritten] = 0;  // Null terminator
        return bytesWritten + 1;
    }
}
