//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// Handles the examine request, which lists the entries of a directory.
/// </summary>
public sealed class ExamineCommandHandler
{
    private readonly FileServerContext _context;

    /// <summary>
    /// Creates the handler.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    public ExamineCommandHandler(FileServerContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Builds the reply to an examine request for the directory named in the request.
    /// </summary>
    /// <param name="request">
    /// The inbound message.
    /// </param>
    /// <param name="fsRequest">
    /// The decoded file server request.
    /// </param>
    /// <returns>
    /// The reply message.
    /// </returns>
    public EconetMessage? Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        if (!ExamineRequestParser.TryParse(fsRequest.Parameters, out byte arg, out byte start, out byte count, out ReadOnlySpan<byte> pathBytes))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.BadPath, "Bad filename"));

        string path = AcornPath.ReadCrTerminatedString(pathBytes);
        byte relativeTo = GetObjectInfoRequestParser.ResolveRelativeTo(fsRequest, session);

        if (!CatalogueEnumerator.TryResolveCatalogueDirectory(_context, session, path, relativeTo, out string unixPath))
        {
            if (arg == 0)
                return Reply(request, fsRequest.ReplyPort, FileServerReply.ExamineEntries(0, 0, ReadOnlySpan<byte>.Empty, true));

            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));
        }

        List<string> names = CatalogueEnumerator.ListSortedNames(_context.FileSystem, unixPath);

        // arg 3 = human-readable "long" text format used by *CAT (PiFS fs_examine: "%-10s %4s/%-2s\0").
        if (arg == 3)
            return BuildTextExamine(request, fsRequest, unixPath, names, start, count);

        int examined = 0;
        byte[] entryBuffer = new byte[Math.Max(count, (byte)1) * 27];
        int entryOffset = 0;
        byte defaultPerm = _context.Configuration.DefaultFilePerm;
        for (int i = start; i < names.Count && examined < count; i++)
        {
            CatalogueEnumerator.WriteEntry(
                _context.FileSystem,
                entryBuffer.AsSpan(entryOffset, 27),
                unixPath,
                names[i],
                defaultPerm);
            entryOffset += 27;
            examined++;
        }

        byte[] payload = FileServerReply.ExamineEntries(examined, start + examined, entryBuffer.AsSpan(0, entryOffset), true);
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private EconetMessage BuildTextExamine(
        EconetMessage request,
        FileServerRequest fsRequest,
        string unixPath,
        List<string> names,
        byte start,
        byte count)
    {
        List<byte> entries = new List<byte>();
        int examined = 0;
        byte defaultFilePerm = _context.Configuration.DefaultFilePerm;
        byte defaultDirPerm = _context.Configuration.DefaultDirPerm;

        for (int i = start; i < names.Count && examined < count; i++)
        {
            string name = names[i];
            string fullPath = _context.FileSystem.CombinePath(unixPath, name);
            bool isDirectory = _context.FileSystem.IsDirectory(fullPath);

            byte perm = isDirectory ? defaultDirPerm : defaultFilePerm;
            if (!isDirectory && !AcornInfStore.TryReadAttributes(_context.FileSystem, fullPath, out _, out _, out _, out perm))
                perm = defaultFilePerm;

            entries.AddRange(Encoding.ASCII.GetBytes(FormatTextEntry(name, isDirectory, perm)));
            entries.Add(0);
            examined++;
        }

        byte[] payload = FileServerReply.ExamineEntries(examined, start + examined, entries.ToArray(), true);
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    // Renders one *CAT entry exactly as PiFS fs_examine arg 3: "%-10s %4s/%-2s" (owner left, public right).
    // Bit meanings come from the shared EconetAccess flags (the same byte *ACCESS writes).
    private static string FormatTextEntry(string name, bool isDirectory, byte perm)
    {
        string ownerAccess =
            (isDirectory ? "D" : string.Empty) +
            ((perm & (byte)EconetAccess.Locked) != 0 ? "L" : string.Empty) +
            ((perm & (byte)EconetAccess.OwnerWrite) != 0 ? "W" : string.Empty) +
            ((perm & (byte)EconetAccess.OwnerRead) != 0 ? "R" : string.Empty);

        string publicAccess =
            ((perm & (byte)EconetAccess.PublicWrite) != 0 ? "W" : string.Empty) +
            ((perm & (byte)EconetAccess.PublicRead) != 0 ? "R" : string.Empty);

        return $"{name,-10} {ownerAccess,4}/{publicAccess,-2}";
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
