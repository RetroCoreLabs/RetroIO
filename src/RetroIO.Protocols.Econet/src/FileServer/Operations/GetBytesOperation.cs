//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using System;
using System.Collections.Generic;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>GetBytes</c> (fsop 0x0A) — the bulk read half of *LOAD/OSGBPB. Reads up to a chunk from an open
/// channel and queues an outbound bulk transfer (the data packet + the completion reply) on the URD
/// data port. A short/malformed request is <c>0xFE</c>; an unknown channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleGetBytes</c>, unchanged. Reads <c>request.Control</c> (the
/// odd/even sequence bit) via <see cref="ChannelSequence"/> and <c>request.Sequence</c> for bulk packet
/// numbering, which is why the whole AUN message is carried in <see cref="FsOpCall.Request"/>.
/// </remarks>
public sealed class GetBytesOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.GetBytes"/> (0x0A).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.GetBytes;

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
    /// Reads up to one bulk chunk from an open channel and queues an outbound transfer: the data packet
    /// on the URD data port, then the completion reply on the reply port.
    /// </summary>
    /// <remarks>
    /// The parameters are the handle, an offset flag, a 24-bit byte count and a 24-bit offset. A zero
    /// offset flag seeks to the given offset; otherwise the read starts at the cursor, or at the previous
    /// cursor when the sequence bit does not match. The count is capped at the bulk chunk size, and a
    /// short read is padded with zeros.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success, with the data and the completion sent through the queued transfer; 0xFE "Bad server
    /// request" when there are fewer than 8 parameter bytes; or 0xDE "Channel ?" for an unknown channel.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerContext context = call.Context;
        EconetMessage request = call.Request;

        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        if (parameters.Length < 8)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad server request");

        // The handle byte on the wire is a bit mask unless ManyHandle is set (fs.c FS_DIVHANDLE, line 11379).
        byte handle = AcornFileHandles.DivHandle(parameters[0], context.Configuration.ManyHandles);
        byte offsetStatus = parameters[1];
        int bytes = parameters[2] | (parameters[3] << 8) | (parameters[4] << 16);
        int offset = parameters[5] | (parameters[6] << 8) | (parameters[7] << 16);
        byte txPort = call.FsRequest.Urd;

        OpenFileHandle? open = context.Handles.Get(call.Request.ClientNet, call.Request.ClientStation, handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        bool seqOk = ChannelSequence.Matches(open.Sequence, request.Control);
        if (offsetStatus == 0)
            open.Stream.Position = offset;
        else if (!seqOk)
            open.Stream.Position = open.CursorOld;
        else
            open.Stream.Position = open.Cursor;

        byte[] buffer = new byte[Math.Min(bytes, BulkTransferManager.MaxChunkSize)];
        int read = open.Stream.Read(buffer, 0, buffer.Length);
        bool eof = read < buffer.Length || open.Stream.Position >= open.Stream.Length;
        if (read < buffer.Length)
            Array.Clear(buffer, read, buffer.Length - read);

        open.SyncCursorFromStream();
        open.Sequence = (byte)(request.Control & 0x01);
        if (eof)
            open.PastEof = true;

        Queue<BulkPacket> queue = new Queue<BulkPacket>();
        queue.Enqueue(new BulkPacket
        {
            EconetPort = txPort,
            Control = 0x80,
            Sequence = request.Sequence + 1,
            Payload = buffer,
        });
        queue.Enqueue(new BulkPacket
        {
            EconetPort = call.FsRequest.ReplyPort,
            Control = request.Control,
            Sequence = request.Sequence + 2,
            Payload = FileServerReply.GetBytesComplete(eof, read),
        });

        context.BulkTransfers.RegisterLoadTransfer(new OutboundLoadTransfer
        {
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            DataPort = txPort,
            ReplyPort = call.FsRequest.ReplyPort,
            TriggerSequence = request.Sequence,
            Queue = queue,
        });

        return FileServerReply.Success();
    }
}
