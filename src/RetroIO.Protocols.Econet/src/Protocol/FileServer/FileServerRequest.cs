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

    public byte ReplyPort => _data.Span[0];

    public byte Opcode => _data.Span[1];

    public byte Urd => _data.Length > 2 ? _data.Span[2] : (byte)0;

    public byte Cwd => _data.Length > 3 ? _data.Span[3] : (byte)0;

    public byte Lib => _data.Length > 4 ? _data.Span[4] : (byte)0;

    public ReadOnlySpan<byte> Parameters => _data.Length > 5 ? _data.Span.Slice(5) : ReadOnlySpan<byte>.Empty;

    public byte ResolvedUrd(FileServerSession session) =>
        AcornFileHandles.ResolveUrd(Urd, session, Opcode);

    public byte ResolvedCwd(FileServerSession session) =>
        AcornFileHandles.ResolveCwd(Cwd, session);

    public byte ResolvedLib(FileServerSession session) =>
        AcornFileHandles.ResolveLib(Lib, session);

    public ReadOnlySpan<byte> Raw => _data.Span;

    public int Length => _data.Length;

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
