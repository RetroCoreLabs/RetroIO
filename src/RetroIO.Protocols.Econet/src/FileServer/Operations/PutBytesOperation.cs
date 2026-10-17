//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>PutBytes</c> (fsop 0x0B) — the bulk write half of *SAVE/OSGBPB. Registers an inbound bulk
/// transfer that streams the client's data into an open, writable channel. A short/malformed request
/// is <c>0xFE</c>; an unknown or read-only channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandlePutBytes</c>, unchanged.
/// </remarks>
public sealed class PutBytesOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.PutBytes;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerContext context = call.Context;
        EconetMessage request = call.Request;

        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        if (parameters.Length < 8)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad server request");

        byte handle = parameters[0];
        byte offsetStatus = parameters[1];
        int bytes = parameters[2] | (parameters[3] << 8) | (parameters[4] << 16);
        int offset = parameters[5] | (parameters[6] << 8) | (parameters[7] << 16);
        byte bulkPort = call.FsRequest.Urd;

        OpenFileHandle? open = context.Handles.Get(handle);
        if (open == null || open.ReadOnly)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        if (offsetStatus == 0)
            open.Stream.Position = offset;
        else
            open.Stream.Position = open.Cursor;

        context.BulkTransfers.RegisterSaveTransfer(new InboundSaveTransfer
        {
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            BulkPort = bulkPort,
            AckPort = call.FsRequest.ReplyPort,
            ReplyPort = call.FsRequest.ReplyPort,
            Stream = open.Stream,
            Remaining = bytes,
            ExpectedBytes = bytes,
            UnixPath = open.UnixPath,
            Username = string.Empty,
        });

        open.Sequence = (byte)(request.Control & 0x01);
        open.SyncCursorFromStream();
        return FileServerReply.Success();
    }
}
