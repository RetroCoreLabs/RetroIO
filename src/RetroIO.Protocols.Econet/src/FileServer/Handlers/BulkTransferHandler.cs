//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// Handles the data, acknowledgement and negative acknowledgement packets of file loads and saves in progress.
/// </summary>
public sealed class BulkTransferHandler
{
    private readonly FileServerContext _context;

    /// <summary>
    /// Creates the handler.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    public BulkTransferHandler(FileServerContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Writes a data packet of a save to its file. When the last byte has arrived, completes the save and builds the final reply.
    /// </summary>
    /// <param name="request">
    /// The inbound data packet. Its port selects the save transfer.
    /// </param>
    /// <returns>
    /// The acknowledgement for the packet, the final reply when the save is complete, or null when no save is registered on the port.
    /// </returns>
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
                AcornAttributeStore.WriteAttributes(
                    _context.FileSystem,
                    completed.UnixPath,
                    completed.LoadAddress,
                    completed.ExecAddress,
                    (int)bytes,
                    defaultAccess,
                    completed.OwnerId);

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

            AcornPath.EncodeFileDate(_context.ServerClock.UtcNow, out byte day, out byte monthYear);
            string acornName = completed?.AcornFilename ?? string.Empty;
            if (_context.Configuration.SaveCloseDelayMs > 0)
                Thread.Sleep(_context.Configuration.SaveCloseDelayMs);

            return BuildReply(request, transfer.ReplyPort, FileServerReply.SaveComplete(defaultAccess, day, monthYear, acornName));
        }

        return BuildReply(request, transfer.AckPort, FileServerReply.Success());
    }

    /// <summary>
    /// Takes the next queued packet of the load transfer of the sending station.
    /// </summary>
    /// <param name="request">
    /// The inbound acknowledgement.
    /// </param>
    /// <returns>
    /// The next packet to send, or null when the station has no packet left.
    /// </returns>
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

    /// <summary>
    /// Aborts the load transfer of the sending station and records it as aborted in the activity log.
    /// </summary>
    /// <param name="request">
    /// The inbound negative acknowledgement.
    /// </param>
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
