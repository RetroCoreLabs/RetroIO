//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// PiBridge FS operation 0x60 (PiFS extensions).
/// </summary>
public sealed class PiBridgeCommandHandler
{
    private readonly FileServerContext _context;

    public PiBridgeCommandHandler(FileServerContext context)
    {
        _context = context;
    }

    public EconetMessage Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        byte arg = fsRequest.Parameters.Length > 0 ? fsRequest.Parameters[0] : (byte)0;
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);

        if (arg is >= 16 and <= 31 && (session == null || !session.IsAdmin))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.InsufficientAccess, "Insufficient access"));

        switch (arg)
        {
            case 0:
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Version("RetroFS Econet Bridge 1.0"));
            case 1:
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
            case 16:
                return HandleUserLookup(request, fsRequest);
            case 17:
                return Reply(request, fsRequest.ReplyPort, FileServerReply.WithReturnCode(0, _context.Configuration.ToConfigReplyBytes()));
            case 18:
                if (fsRequest.Parameters.Length > 1)
                    _context.Configuration.ApplyConfigBytes(fsRequest.Parameters.Slice(1));
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
            case 19:
                if (session != null)
                {
                    _context.Handles.CloseAllForStation(session.ClientNet, session.ClientStation);
                    _context.BulkTransfers.ReleaseStation(session.ClientNet, session.ClientStation);
                    _context.Sessions.Logout(session.ClientNet, session.ClientStation);
                }
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
            case 20:
                return HandleForceLogoff(request, fsRequest);
            default:
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
        }
    }

    private EconetMessage HandleUserLookup(EconetMessage request, FileServerRequest fsRequest)
    {
        if (fsRequest.Parameters.Length < 2)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.BadCommand, "Bad command"));

        string username = AcornPath.ReadCrTerminatedString(fsRequest.Parameters.Slice(1));
        if (!_context.Users.TryGetProfileByUsername(username, out EconetUserProfile? profile) || profile == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        byte[] reply =
        [
            0,
            0,
            (byte)profile.UserIndex,
            (byte)(profile.UserIndex >> 8),
            profile.Privilege,
            profile.Privilege2,
        ];
        return Reply(request, fsRequest.ReplyPort, reply);
    }

    private EconetMessage HandleForceLogoff(EconetMessage request, FileServerRequest fsRequest)
    {
        if (fsRequest.Parameters.Length < 2)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.BadCommand, "Bad command"));

        byte secArg = fsRequest.Parameters[1];
        int count = secArg switch
        {
            0 => LogoffByUsername(fsRequest),
            1 => LogoffByUserIndex(fsRequest),
            2 => LogoffByStation(fsRequest),
            _ => 0,
        };

        return Reply(request, fsRequest.ReplyPort, FileServerReply.LogoffCount(count));
    }

    private int LogoffByUsername(FileServerRequest fsRequest)
    {
        if (fsRequest.Parameters.Length < 3)
            return 0;

        string username = AcornPath.ReadCrTerminatedString(fsRequest.Parameters.Slice(2));
        return _context.Sessions.LogoutByUsername(username, _context.Handles, _context.BulkTransfers);
    }

    private int LogoffByUserIndex(FileServerRequest fsRequest)
    {
        if (fsRequest.Parameters.Length < 4)
            return 0;

        ushort userIndex = (ushort)(fsRequest.Parameters[2] | (fsRequest.Parameters[3] << 8));
        return _context.Sessions.LogoutByUserIndex(userIndex, _context.Handles, _context.BulkTransfers);
    }

    private int LogoffByStation(FileServerRequest fsRequest)
    {
        if (fsRequest.Parameters.Length < 4)
            return 0;

        byte net = fsRequest.Parameters[2];
        byte station = fsRequest.Parameters[3];
        if (!_context.Sessions.IsLoggedIn(net, station))
            return 0;

        _context.Handles.CloseAllForStation(net, station);
        _context.BulkTransfers.ReleaseStation(net, station);
        _context.Sessions.Logout(net, station);
        return 1;
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
