//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>GetByte</c> (fsop 0x08) — reads one byte from an open channel, honouring the AUN retransmit
/// sequence bit. Reply is <c>[0, 0, value, flags]</c> (flags bit7 = at-EOF); a directory or exhausted
/// channel returns the PiFS <c>0xFE/0xC0</c> get-byte sentinel; an unknown channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleGetByte</c>, unchanged. The channel handle comes from the
/// URD wire field via <see cref="AcornFileHandles.DivHandle"/>, and the sequence check reads
/// <c>call.Request.Control</c> — which is why <see cref="FsOpCall"/> carries the whole request.
/// </remarks>
public sealed class GetByteOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.GetByte;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        byte handle = AcornFileHandles.DivHandle(call.FsRequest.Urd);
        byte ctrl = call.Request.Control;
        OpenFileHandle? open = call.Context.Handles.Get(handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        if (open.IsDirectory)
            return FileServerReply.GetByte(0xFE, 0xC0);

        if (open.PastEof)
            return FileServerReply.Error(FileServerErrors.Eof, "EOF");

        bool seqOk = ChannelSequence.Matches(open.Sequence, ctrl);
        open.SeekToCursor(!seqOk);
        int value = open.Stream.ReadByte();
        byte flags = 0;
        if (value < 0)
        {
            open.PastEof = true;
            return FileServerReply.GetByte(0xFE, 0xC0);
        }

        if (open.Stream.Position >= open.Stream.Length)
            flags = 0x80;

        open.SyncCursorFromStream();
        open.Sequence = (byte)(ctrl & 0x01);
        return FileServerReply.GetByte((byte)value, flags);
    }
}
