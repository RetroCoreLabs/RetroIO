//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// SJ file server operations 0x40 / 0x41 — PiFS compatibility stubs (protocol analysis §6.3).
/// </summary>
public sealed class SjOpcodeHandler
{
    private readonly FileServerContext _context;

    public SjOpcodeHandler(FileServerContext context)
    {
        _context = context;
    }

    public EconetMessage? Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        if (fsRequest.Opcode is not (FileServerOpcodes.SjOperation or FileServerOpcodes.SjOperationReply))
            return null;

        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.InsufficientAccess, "Insufficient access"));

        return fsRequest.Opcode switch
        {
            FileServerOpcodes.SjOperation => HandleReadAccount(request, fsRequest, session),
            FileServerOpcodes.SjOperationReply => HandleSystemInfo(request, fsRequest, session),
            _ => null,
        };
    }

    private EconetMessage HandleReadAccount(EconetMessage request, FileServerRequest fsRequest, FileServerSession session)
    {
        ushort userIndex = _context.Sessions.GetSessionIndex(session);
        byte[] payload =
        [
            0, 0,
            0xFF, 0xFF,
            0x01,
            0x00,
            (byte)(userIndex & 0xFF),
            (byte)((userIndex >> 8) & 0xFF),
            0xFF, 0xFF,
        ];
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private EconetMessage HandleSystemInfo(EconetMessage request, FileServerRequest fsRequest, FileServerSession session)
    {
        if (fsRequest.Parameters.Length < 1)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Unsupported, "Unsupported"));

        byte rwOp = fsRequest.Parameters[0];
        bool isWrite = (rwOp & 0x01) == 0;

        if (isWrite && !session.IsAdmin)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.InsufficientAccess, "Insufficient access"));

        byte[] payload = rwOp switch
        {
            1 or 9 => [0, 0, 0],
            3 or 5 or 7 or 11 or 15 => [0, 0, 0],
            2 or 4 or 6 or 8 or 10 or 12 => [0, 0],
            _ => [],
        };

        if (payload.Length == 0)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Unsupported, "Unsupported"));

        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private static EconetMessage Reply(EconetMessage request, byte replyPort, byte[] payload)
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
