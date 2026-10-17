//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Builds activity log entries from file server traffic and writes them to the log.
/// </summary>
public static class EconetActivityRecorder
{
    /// <summary>
    /// Records a handled command in the activity log and in the JSON log. Does nothing when there is no log.
    /// </summary>
    /// <param name="log">
    /// The activity log, or null for none.
    /// </param>
    /// <param name="clientNet">
    /// The network number of the client.
    /// </param>
    /// <param name="clientStation">
    /// The station number of the client.
    /// </param>
    /// <param name="username">
    /// The user name of the client.
    /// </param>
    /// <param name="fsRequest">
    /// The request.
    /// </param>
    /// <param name="replyPayload">
    /// The reply bytes, or null when there is no reply.
    /// </param>
    /// <param name="session">
    /// The session of the client, or null when it is not logged in.
    /// </param>
    /// <param name="aunSequence">
    /// The AUN sequence number of the request, for the JSON log.
    /// </param>
    public static void RecordCommand(
        IEconetActivityLog? log,
        byte clientNet,
        byte clientStation,
        string username,
        FileServerRequest fsRequest,
        byte[]? replyPayload,
        FileServerSession? session = null,
        uint? aunSequence = null)
    {
        if (log == null)
            return;

        bool success = EconetProtocolDiagnostics.EvaluateReplySuccess(fsRequest, replyPayload);

        log.RecordCommand(new EconetCommandEntry
        {
            TimestampUtc = DateTime.UtcNow,
            ClientNet = clientNet,
            ClientStation = clientStation,
            Username = username,
            Opcode = fsRequest.Opcode,
            Command = FileServerOpcodeNames.GetMonitorCommandLabel(fsRequest.Opcode, fsRequest),
            Detail = EconetProtocolDiagnostics.FormatRequestTrace(fsRequest, session),
            Success = success,
            Result = EconetProtocolDiagnostics.FormatReplyTrace(fsRequest, replyPayload, success),
        });

        EconetServerJsonLog.WriteCommand(
            clientNet,
            clientStation,
            username,
            fsRequest,
            replyPayload,
            session,
            aunSequence);
    }

    /// <summary>
    /// Records a request that was too short to parse. Does nothing when there is no log.
    /// </summary>
    /// <param name="log">
    /// The activity log, or null for none.
    /// </param>
    /// <param name="clientNet">
    /// The network number of the client.
    /// </param>
    /// <param name="clientStation">
    /// The station number of the client.
    /// </param>
    /// <param name="payload">
    /// The bytes of the request.
    /// </param>
    public static void RecordMalformedCommand(
        IEconetActivityLog? log,
        byte clientNet,
        byte clientStation,
        ReadOnlySpan<byte> payload)
    {
        if (log == null)
            return;

        log.RecordCommand(new EconetCommandEntry
        {
            TimestampUtc = DateTime.UtcNow,
            ClientNet = clientNet,
            ClientStation = clientStation,
            Command = "Malformed FS request",
            Detail = FileServerPacketFormatter.FormatRawPayload(payload),
            Success = false,
            Result = "FAIL rejected — payload too short to parse",
        });

        EconetServerJsonLog.WriteMalformed(clientNet, clientStation, payload);
    }

    /// <summary>
    /// Records a file transfer in the activity log. Does nothing when there is no log.
    /// </summary>
    /// <param name="log">
    /// The activity log, or null for none.
    /// </param>
    /// <param name="clientNet">
    /// The network number of the client.
    /// </param>
    /// <param name="clientStation">
    /// The station number of the client.
    /// </param>
    /// <param name="username">
    /// The user name of the client.
    /// </param>
    /// <param name="direction">
    /// The direction of the transfer.
    /// </param>
    /// <param name="filePath">
    /// The file name or path.
    /// </param>
    /// <param name="bytes">
    /// The number of bytes transferred.
    /// </param>
    /// <param name="success">
    /// True when the transfer finished correctly.
    /// </param>
    /// <param name="status">
    /// A short status text.
    /// </param>
    public static void RecordFileTransfer(
        IEconetActivityLog? log,
        byte clientNet,
        byte clientStation,
        string username,
        EconetFileTransferDirection direction,
        string filePath,
        long bytes,
        bool success,
        string status)
    {
        if (log == null)
            return;

        log.RecordFileTransfer(new EconetFileTransferEntry
        {
            TimestampUtc = DateTime.UtcNow,
            ClientNet = clientNet,
            ClientStation = clientStation,
            Username = username,
            Direction = direction,
            FilePath = filePath,
            Bytes = bytes,
            Success = success,
            Status = status,
        });
    }
}
