//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Sessions;

/// <summary>
/// Parsed Level-3 file server request carried in the AUN data[] payload.
/// </summary>
public readonly struct FileServerRequest
{
    private readonly ReadOnlyMemory<byte> _data;

    private FileServerRequest(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <summary>
    /// Gets the port the reply is sent to, from byte 0.
    /// </summary>
    public byte ReplyPort => _data.Span[0];

    /// <summary>
    /// Gets the opcode, from byte 1.
    /// </summary>
    public byte Opcode => _data.Span[1];

    /// <summary>
    /// Gets the user root directory handle from byte 2, or 0 when the request is shorter.
    /// </summary>
    public byte Urd => _data.Length > 2 ? _data.Span[2] : (byte)0;

    /// <summary>
    /// Gets the current directory handle from byte 3, or 0 when the request is shorter.
    /// </summary>
    public byte Cwd => _data.Length > 3 ? _data.Span[3] : (byte)0;

    /// <summary>
    /// Gets the library directory handle from byte 4, or 0 when the request is shorter.
    /// </summary>
    public byte Lib => _data.Length > 4 ? _data.Span[4] : (byte)0;

    /// <summary>
    /// Gets the bytes after the fifth byte, or an empty span when there are none.
    /// </summary>
    public ReadOnlySpan<byte> Parameters => _data.Length > 5 ? _data.Span.Slice(5) : ReadOnlySpan<byte>.Empty;

    /// <summary>
    /// Converts the user root handle of the request to an internal handle.
    /// </summary>
    /// <param name="session">
    /// The session used as fallback.
    /// </param>
    /// <returns>
    /// The internal handle.
    /// </returns>
    public byte ResolvedUrd(FileServerSession session) =>
        AcornFileHandles.ResolveUrd(Urd, session, Opcode);

    /// <summary>
    /// Converts the current directory handle of the request to an internal handle.
    /// </summary>
    /// <param name="session">
    /// The session used as fallback.
    /// </param>
    /// <returns>
    /// The internal handle.
    /// </returns>
    public byte ResolvedCwd(FileServerSession session) =>
        AcornFileHandles.ResolveCwd(Cwd, session);

    /// <summary>
    /// Converts the library handle of the request to an internal handle.
    /// </summary>
    /// <param name="session">
    /// The session used as fallback.
    /// </param>
    /// <returns>
    /// The internal handle.
    /// </returns>
    public byte ResolvedLib(FileServerSession session) =>
        AcornFileHandles.ResolveLib(Lib, session);

    /// <summary>
    /// Gets all bytes of the request.
    /// </summary>
    public ReadOnlySpan<byte> Raw => _data.Span;

    /// <summary>
    /// Gets the number of bytes in the request.
    /// </summary>
    public int Length => _data.Length;

    /// <summary>
    /// Wraps request bytes in a request value.
    /// </summary>
    /// <param name="data">
    /// The request bytes.
    /// </param>
    /// <param name="request">
    /// Receives the request, or the default value when parsing fails.
    /// </param>
    /// <returns>
    /// True when there are at least 2 bytes, otherwise false.
    /// </returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, out FileServerRequest request)
    {
        if (data.Length < 2)
        {
            request = default;
            return false;
        }

        request = new FileServerRequest(data);
        return true;
    }
}
