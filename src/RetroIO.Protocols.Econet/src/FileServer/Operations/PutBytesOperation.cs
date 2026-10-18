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
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.PutBytes"/> (0x0B).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.PutBytes;

    /// <summary>
    /// Gets <c>true</c>: the operation needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="FsOpCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the operation is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Registers an inbound bulk transfer that writes the client's data into an open, writable channel.
    /// </summary>
    /// <remarks>
    /// The parameters are the handle, an offset flag, a 24-bit byte count and a 24-bit offset. A zero
    /// offset flag seeks to the given offset; otherwise the write starts at the cursor. The data arrives
    /// on the URD port, and acknowledgements and the reply go to the reply port.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad server request" when there are fewer than 8 parameter bytes; or 0xDE
    /// "Channel ?" for an unknown or read-only channel.
    /// </returns>
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

        OpenFileHandle? open = context.Handles.Get(call.Request.ClientNet, call.Request.ClientStation, handle);
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
