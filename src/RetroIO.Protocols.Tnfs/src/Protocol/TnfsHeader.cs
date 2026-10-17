//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;

namespace RetroIO.Protocols.Tnfs.Protocol;

/// <summary>
/// TNFS message header (4 bytes).
/// All multi-byte values are little-endian.
/// </summary>
public struct TnfsHeader
{
    /// <summary>
    /// Session ID (2 bytes, little-endian).
    /// </summary>
    public ushort SessionId { get; set; }

    /// <summary>
    /// Sequence number (1 byte).
    /// Used for message retry detection.
    /// </summary>
    public byte SequenceNumber { get; set; }

    /// <summary>
    /// Command code (1 byte).
    /// Upper nibble = command class (0x00=session, 0x10=dir, 0x20=file).
    /// Lower nibble = command index within class.
    /// </summary>
    public byte Command { get; set; }

    /// <summary>
    /// Status/return code (1 byte).
    /// Only present in responses, not requests.
    /// </summary>
    public byte Status { get; set; }

    /// <summary>
    /// Client IP address (for routing).
    /// Not part of wire protocol.
    /// </summary>
    public IPAddress? ClientIP { get; set; }

    /// <summary>
    /// Client port (for routing).
    /// Not part of wire protocol.
    /// </summary>
    public int ClientPort { get; set; }

    /// <summary>
    /// TCP connection ID (0 if UDP).
    /// Not part of wire protocol.
    /// </summary>
    public int TcpConnectionId { get; set; }

    /// <summary>
    /// Parse TNFS header from buffer.
    /// </summary>
    public static TnfsHeader Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < TnfsConstants.TNFS_HEADERSZ)
            throw new ArgumentException($"Buffer too small for TNFS header (need {TnfsConstants.TNFS_HEADERSZ} bytes, got {buffer.Length})");

        return new TnfsHeader
        {
            SessionId = TnfsEndian.ReadUInt16(buffer.ToArray(), 0),
            SequenceNumber = buffer[2],
            Command = buffer[3],
            Status = 0  // Not present in request
        };
    }

    /// <summary>
    /// Serialize header to buffer for response.
    /// Returns number of bytes written (always 5: header + status).
    /// </summary>
    public int SerializeResponse(Span<byte> buffer)
    {
        if (buffer.Length < TnfsConstants.TNFS_HEADERSZ + 1)
            throw new ArgumentException($"Buffer too small for TNFS response header (need {TnfsConstants.TNFS_HEADERSZ + 1} bytes)");

        byte[] temp = buffer.ToArray();
        TnfsEndian.WriteUInt16(temp, 0, SessionId);
        temp[2] = SequenceNumber;
        temp[3] = Command;
        temp[4] = Status;
        temp.AsSpan().CopyTo(buffer);

        return 5;
    }

    /// <summary>
    /// Get command class (upper nibble).
    /// </summary>
    public byte GetCommandClass() => (byte)(Command & 0xF0);

    /// <summary>
    /// Get command index (lower nibble).
    /// </summary>
    public byte GetCommandIndex() => (byte)(Command & 0x0F);

    /// <summary>
    /// Check if this is a session command.
    /// </summary>
    public bool IsSessionCommand() => GetCommandClass() == TnfsConstants.CLASS_SESSION;

    /// <summary>
    /// Check if this is a directory command.
    /// </summary>
    public bool IsDirectoryCommand() => GetCommandClass() == TnfsConstants.CLASS_DIRECTORY;

    /// <summary>
    /// Check if this is a file command.
    /// </summary>
    public bool IsFileCommand() => GetCommandClass() == TnfsConstants.CLASS_FILE;

    public override string ToString()
    {
        return $"TNFS Header: SID=0x{SessionId:X4}, Seq={SequenceNumber}, Cmd=0x{Command:X2} ({TnfsConstants.GetCommandName(Command)}), Status=0x{Status:X2}";
    }
}
