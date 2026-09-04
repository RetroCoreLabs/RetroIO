//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>PutByte</c> (fsop 0x09) — writes one byte (carried in the CWD wire field) to an open channel,
/// honouring the AUN retransmit sequence bit. An unknown or read-only channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandlePutByte</c>, unchanged. Like GetByte, the handle comes from
/// the URD wire field and the sequence check reads <c>call.Request.Control</c>.
/// </remarks>
public sealed class PutByteOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.PutByte;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        byte handle = AcornFileHandles.DivHandle(call.FsRequest.Urd);
        byte value = call.FsRequest.Cwd;
        OpenFileHandle? open = call.Context.Handles.Get(handle);
        if (open == null || open.ReadOnly)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        bool seqOk = ChannelSequence.Matches(open.Sequence, call.Request.Control);
        open.SeekToCursor(!seqOk);
        open.Stream.WriteByte(value);
        open.SyncCursorFromStream();
        open.Sequence = (byte)(call.Request.Control & 0x01);
        return FileServerReply.Success();
    }
}
