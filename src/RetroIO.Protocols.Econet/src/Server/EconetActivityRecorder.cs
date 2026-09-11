//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.Server;

public static class EconetActivityRecorder
{
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
