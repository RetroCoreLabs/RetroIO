//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

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
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.GetBytes;

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
        byte txPort = call.FsRequest.Urd;

        OpenFileHandle? open = context.Handles.Get(handle);
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
