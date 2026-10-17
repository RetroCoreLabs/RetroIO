//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Storage.Utilities.Logging;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Structured JSONL events for Econet file-server protocol analysis.
/// </summary>
public static class EconetServerJsonLog
{
    public static void WriteCommand(
        byte clientNet,
        byte clientStation,
        string username,
        FileServerRequest fsRequest,
        byte[]? replyPayload,
        FileServerSession? session,
        uint? aunSequence = null,
        byte? replyPort = null)
    {
        if (!RetroFsServerJsonLog.IsConfigured)
            return;

        bool success = EconetProtocolDiagnostics.EvaluateReplySuccess(fsRequest, replyPayload);
        string level = success ? "info" : "warning";

        Dictionary<string, object?> fields = new Dictionary<string, object?>
        {
            ["clientNet"] = clientNet,
            ["clientStation"] = clientStation,
            ["username"] = string.IsNullOrEmpty(username) ? null : username,
            ["opcode"] = fsRequest.Opcode,
            ["opcodeName"] = FileServerOpcodeNames.GetName(fsRequest.Opcode),
            ["command"] = FileServerOpcodeNames.GetMonitorCommandLabel(fsRequest.Opcode, fsRequest),
            ["success"] = success,
            ["aunSequence"] = aunSequence,
            ["replyPort"] = replyPort ?? fsRequest.ReplyPort,
            // Redact any login password from the raw bytes too, not just the trace — the JSON log's
            // "hex" field would otherwise carry the password in the clear. Non-login requests redact
            // to null and are logged unchanged.
            ["request"] = BuildPacketObject(
                EconetLogRedaction.RedactRawRequest(fsRequest.Opcode, fsRequest.Raw) ?? fsRequest.Raw.ToArray(),
                EconetProtocolDiagnostics.FormatRequestTrace(fsRequest, session)),
            ["reply"] = BuildReplyObject(fsRequest, replyPayload, success),
        };

        if (session != null)
        {
            fields["sessionCwd"] = session.CurrentDirectoryUnix;
            fields["wireUrd"] = fsRequest.Urd;
            fields["wireCwd"] = fsRequest.Cwd;
            fields["wireLib"] = fsRequest.Lib;
        }

        RetroFsServerJsonLog.Write("econet-command", level, fields);
    }

    public static void WriteMalformed(
        byte clientNet,
        byte clientStation,
        ReadOnlySpan<byte> payload)
    {
        if (!RetroFsServerJsonLog.IsConfigured)
            return;

        byte[] copy = payload.ToArray();
        RetroFsServerJsonLog.Write("econet-malformed", "warning", new Dictionary<string, object?>
        {
            ["clientNet"] = clientNet,
            ["clientStation"] = clientStation,
            ["request"] = BuildPacketObject(copy, FileServerPacketFormatter.FormatRawPayload(copy)),
            ["success"] = false,
        });
    }

    public static void WriteInboundAun(EconetMessage message)
    {
        if (!RetroFsServerJsonLog.IsConfigured)
            return;

        RetroFsServerJsonLog.Write("econet-aun-inbound", "debug", new Dictionary<string, object?>
        {
            ["clientNet"] = message.ClientNet,
            ["clientStation"] = message.ClientStation,
            ["packetType"] = message.PacketType.ToString(),
            ["aunPort"] = message.EconetPort,
            ["aunControlWire"] = message.Control,
            ["aunSequence"] = message.Sequence,
            ["payloadLength"] = message.Payload.Length,
        });
    }

    public static void WriteOutboundAun(EconetMessage message)
    {
        if (!RetroFsServerJsonLog.IsConfigured)
            return;

        if (message.PacketType != AunPacketType.Unicast)
            return;

        byte[] wire = new byte[AunPacket.HeaderSize + message.Payload.Length];
        AunPacket.Encode(
            wire,
            message.PacketType,
            message.EconetPort,
            message.Control,
            message.Sequence,
            message.Payload);

        RetroFsServerJsonLog.Write("econet-aun-outbound", "debug", new Dictionary<string, object?>
        {
            ["clientNet"] = message.ClientNet,
            ["clientStation"] = message.ClientStation,
            ["aunPort"] = message.EconetPort,
            ["aunControlWire"] = wire[2],
            ["aunSequence"] = message.Sequence,
            ["fsLength"] = message.Payload.Length,
            ["wire"] = BuildPacketObject(wire, $"ptype=&{wire[0]:X2} port=&{wire[1]:X2} ctrl=&{wire[2]:X2} seq={message.Sequence}"),
        });
    }

    private static Dictionary<string, object?> BuildReplyObject(
        FileServerRequest request,
        byte[]? payload,
        bool success)
    {
        if (payload == null || payload.Length == 0)
        {
            return new Dictionary<string, object?>
            {
                ["length"] = 0,
                ["hexPairCount"] = 0,
                ["success"] = success,
                ["trace"] = success ? "OK (empty reply)" : "FAIL (no reply)",
            };
        }

        string hex = FileServerPacketFormatter.FormatHex(payload, payload.Length);
        int hexPairCount = CountHexPairs(hex);
        Dictionary<string, object?> reply = new Dictionary<string, object?>
        {
            ["length"] = payload.Length,
            ["hexPairCount"] = hexPairCount,
            ["lengthMatchesHex"] = payload.Length == hexPairCount,
            ["hex"] = hex,
            ["success"] = success,
            ["trace"] = EconetProtocolDiagnostics.FormatReplyTrace(request, payload, success),
        };

        if (request.Opcode == FileServerOpcodes.GetObjectInfo &&
            GetObjectInfoRequestParser.TryParse(request, out byte command, out _) &&
            command == 6 &&
            payload.Length >= 17)
        {
            reply["cmd6"] = EconetProtocolDiagnostics.DescribeCmd6Fields(payload);
        }

        return reply;
    }

    private static Dictionary<string, object?> BuildPacketObject(ReadOnlySpan<byte> data, string trace)
    {
        byte[] copy = data.ToArray();
        string hex = FileServerPacketFormatter.FormatHex(copy, copy.Length);
        return new Dictionary<string, object?>
        {
            ["length"] = copy.Length,
            ["hexPairCount"] = CountHexPairs(hex),
            ["hex"] = hex,
            ["trace"] = trace,
        };
    }

    private static int CountHexPairs(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex == "(none)")
            return 0;

        int count = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            if (hex[i] == ' ')
                continue;
            if (i + 1 < hex.Length && hex[i + 1] != ' ')
            {
                count++;
                i++;
            }
        }

        return count;
    }
}
