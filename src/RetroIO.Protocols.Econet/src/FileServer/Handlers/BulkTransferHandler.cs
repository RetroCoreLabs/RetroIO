//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

public sealed class BulkTransferHandler
{
    private readonly FileServerContext _context;

    public BulkTransferHandler(FileServerContext context)
    {
        _context = context;
    }

    public EconetMessage? HandleData(EconetMessage request)
    {
        InboundSaveTransfer? transfer = _context.BulkTransfers.GetSaveTransfer(request.EconetPort);
        if (transfer == null)
            return null;

        if (request.Payload.Length > 0)
        {
            transfer.Stream.Write(request.Payload, 0, request.Payload.Length);
            transfer.Remaining -= request.Payload.Length;
        }

        if (transfer.Remaining <= 0)
        {
            const byte defaultAccess = 0x33;
            long bytes = transfer.ExpectedBytes - transfer.Remaining;
            InboundSaveTransfer? completed = _context.BulkTransfers.CompleteSave(request.EconetPort);
            if (completed != null)
            {
                AcornInfStore.WriteAttributes(
                    _context.FileSystem,
                    completed.UnixPath,
                    completed.LoadAddress,
                    completed.ExecAddress,
                    (int)bytes,
                    defaultAccess);

                EconetActivityRecorder.RecordFileTransfer(
                    _context.ActivityLog,
                    completed.ClientNet,
                    completed.ClientStation,
                    completed.Username,
                    EconetFileTransferDirection.Receive,
                    completed.UnixPath,
                    bytes,
                    success: true,
                    status: "OK");
            }

            EncodeAcornDate(_context.ServerClock.UtcNow, out byte day, out byte monthYear);
            string acornName = completed?.AcornFilename ?? string.Empty;
            if (_context.Configuration.SaveCloseDelayMs > 0)
                Thread.Sleep(_context.Configuration.SaveCloseDelayMs);

            return BuildReply(request, transfer.ReplyPort, FileServerReply.SaveComplete(defaultAccess, day, monthYear, acornName));
        }

        return BuildReply(request, transfer.AckPort, FileServerReply.Success());
    }

    public EconetMessage? HandleAck(EconetMessage request)
    {
        BulkPacket? packet = _context.BulkTransfers.DequeueLoadPacket(
            request.ClientNet,
            request.ClientStation,
            request.Sequence,
            out OutboundLoadTransfer? completedTransfer);

        if (completedTransfer != null)
        {
            EconetActivityRecorder.RecordFileTransfer(
                _context.ActivityLog,
                completedTransfer.ClientNet,
                completedTransfer.ClientStation,
                completedTransfer.Username,
                EconetFileTransferDirection.Send,
                completedTransfer.FilePath,
                completedTransfer.TotalBytes,
                success: true,
                status: "OK");
        }

        if (packet == null)
            return null;

        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = packet.EconetPort,
            Control = packet.Control,
            Sequence = packet.Sequence,
            Payload = packet.Payload,
            ClientAddress = request.ClientAddress,
            ClientPort = request.ClientPort,
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            Transport = request.Transport,
        };
    }

    public void HandleNak(EconetMessage request)
    {
        OutboundLoadTransfer? transfer = _context.BulkTransfers.GetLoadTransfer(request.ClientNet, request.ClientStation);
        if (transfer != null)
        {
            EconetActivityRecorder.RecordFileTransfer(
                _context.ActivityLog,
                transfer.ClientNet,
                transfer.ClientStation,
                transfer.Username,
                EconetFileTransferDirection.Send,
                transfer.FilePath,
                transfer.TotalBytes,
                success: false,
                status: "Aborted");
        }

        _context.BulkTransfers.AbortLoad(request.ClientNet, request.ClientStation);
    }

    private static void EncodeAcornDate(DateTime utc, out byte day, out byte monthYear)
    {
        day = (byte)utc.Day;
        int yearOffset = utc.Year - 1981;
        monthYear = (byte)(((yearOffset & 0x0F) << 4) | (utc.Month & 0x0F));
    }

    private static EconetMessage BuildReply(EconetMessage request, byte replyPort, byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = replyPort,
            Control = 0x80,
            Sequence = request.Sequence,
            Payload = payload,
            ClientAddress = request.ClientAddress,
            ClientPort = request.ClientPort,
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            Transport = request.Transport,
        };
    }
}
