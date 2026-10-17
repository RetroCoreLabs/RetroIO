//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// PiFS <c>fs_free</c> (0x1A) and user free-space stub (0x1E) per protocol analysis §6.3.
/// </summary>
public sealed class FreeSpaceCommandHandler
{
    private readonly FileServerContext _context;

    public FreeSpaceCommandHandler(FileServerContext context)
    {
        _context = context;
    }

    public EconetMessage? Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        return fsRequest.Opcode switch
        {
            FileServerOpcodes.FreeSpace => HandleDiscFreeSpace(request, fsRequest),
            FileServerOpcodes.UserFreeSpace => Reply(request, fsRequest.ReplyPort, FileServerReply.UserFreeSpaceDummy()),
            _ => null,
        };
    }

    private EconetMessage HandleDiscFreeSpace(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        string discReference = fsRequest.Parameters.Length > 0
            ? AcornPath.ReadCrTerminatedString(fsRequest.Parameters)
            : string.Empty;

        DiscEntry? disc;
        if (string.IsNullOrWhiteSpace(discReference))
            disc = _context.Discs.GetByIndex(session.CurrentDisc);
        else if (!_context.Discs.TryFind(discReference, out disc))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Unsupported, "No such disc"));

        if (disc == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Unsupported, "No such disc"));

        string discPath = _context.Discs.GetDiscDirectoryPath(disc.Index);
        if (!TryQueryDiscSpace(discPath, out uint free, out uint extent))
        {
            free = 0x7FFFFF;
            extent = 0x7FFFFF;
        }

        // Wire-format divergence (task 0.4): the later FS returns free + total-extent (8 bytes); the
        // 1983 AUG returned ONLY the 3-byte free-block count. Default (LaterFS) keeps the verified
        // 8-byte frame; a client pinned to Classic1983 gets the 5-byte free-only frame.
        Configuration.WireProfile profile = _context.WireProfiles.Resolve(request.ClientNet, request.ClientStation);
        byte[] payload = profile == Configuration.WireProfile.Classic1983
            ? FileServerReply.DiscFreeSpaceClassic1983(free)
            : FileServerReply.DiscFreeSpace(free, extent);
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private bool TryQueryDiscSpace(string discPath, out uint free, out uint extent)
    {
        free = 0;
        extent = 0;
        try
        {
            string fullPath = _context.FileSystem.GetFullPath(discPath);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(fullPath))
                return false;

            DriveInfo drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;

            long freeUnits = drive.AvailableFreeSpace / 256;
            long extentUnits = drive.TotalSize / 256;
            if (freeUnits > 0x7FFFFF)
                freeUnits = 0x7FFFFF;
            if (extentUnits > 0x7FFFFF)
                extentUnits = 0x7FFFFF;

            free = (uint)freeUnits;
            extent = (uint)extentUnits;
            return true;
        }
        catch
        {
            return false;
        }
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
