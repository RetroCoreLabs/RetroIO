//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Configuration;
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
        byte relativeTo = GetObjectInfoRequestParser.ResolveRelativeTo(fsRequest, session, _context.Configuration.ManyHandles);

        if (!CatalogueEnumerator.TryResolveCatalogueDirectory(_context, session, path, relativeTo, out string unixPath))
        {
            if (arg == 0)
                return Reply(request, fsRequest.ReplyPort, FileServerReply.ExamineEntries(0, 0, ReadOnlySpan<byte>.Empty, true));

            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));
        }

        List<string> names = CatalogueEnumerator.ListSortedNames(_context.FileSystem, unixPath);

        // arg 3 = human-readable "long" text format used by *CAT (PiFS fs_examine: "%-10s %4s/%-2s\0").
        if (arg == 3)
            return BuildTextExamine(request, fsRequest, session, unixPath, names, start, count);

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

            // fs.c lines 5089 to 5094: with the MdfsFunctions flag set, the three bytes after the date hold the owner
            // (main account, then bits 8 to 10 of the number moved down by 3, then 0 for the auxiliary account).
            // With the flag clear PiFS puts the internal number there, which this server does not have (bytes stay 0).
            if (_context.Configuration.MdfsFunctions)
            {
                ushort owner = ObjectOwnership.GetOwner(_context, _context.FileSystem.CombinePath(unixPath, names[i]));
                Span<byte> entry = entryBuffer.AsSpan(entryOffset, 27);
                entry[21] = (byte)(owner & 0xFF);
                entry[22] = (byte)((owner & 0x700) >> 3);
                entry[23] = 0;
            }

            entryOffset += 27;
            examined++;
        }

        byte[] payload = FileServerReply.ExamineEntries(examined, start + examined, entryBuffer.AsSpan(0, entryOffset), true);
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private EconetMessage BuildTextExamine(
        EconetMessage request,
        FileServerRequest fsRequest,
        FileServerSession session,
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
            if (!isDirectory && !AcornAttributeStore.TryReadAttributes(_context.FileSystem, fullPath, out _, out _, out _, out perm))
                perm = defaultFilePerm;

            // fs.c line 5170: is_owner is the effective owner test, so a system user counts as the owner.
            bool isOwner = ObjectOwnership.IsEffectiveOwner(session, ObjectOwnership.GetOwner(_context, fullPath));
            entries.AddRange(Encoding.ASCII.GetBytes(FormatTextEntry(name, isDirectory, perm, _context.Configuration, isOwner)));
            entries.Add(0);
            examined++;
        }

        byte[] payload = FileServerReply.ExamineEntries(examined, start + examined, entries.ToArray(), true);
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    // Renders one *CAT entry exactly as PiFS fs_examine arg 3: "%-10s %4s/%-2s" (owner left, public right).
    // Bit meanings come from the shared EconetAccess flags (the same byte *ACCESS writes).
    // The AcornDir flag changes the letters (fs.c lines 5174 and 5175): a directory whose access is exactly owner write,
    // owner read and other read (other write clear) is shown with none of those letters, because the Acorn clients
    // take them as implied. The MdfsInfo flag also changes the letters (fs.c lines 5180 to 5187), by whether the viewer
    // owns the object: with the flag set the owner letters are lower case for a viewer who is not the owner, and the
    // other-user letters are lower case for the owner. With the flag clear every letter is upper case.
    private static string FormatTextEntry(string name, bool isDirectory, byte perm, FileServerConfiguration configuration, bool isOwner)
    {
        const byte implied = (byte)(EconetAccess.OwnerWrite | EconetAccess.OwnerRead | EconetAccess.PublicRead);
        if (isDirectory && configuration.AcornDirectoryDisplay &&
            (perm & (implied | (byte)EconetAccess.PublicWrite)) == implied)
        {
            perm = (byte)(perm & ~implied);
        }

        string ownerAccess =
            (isDirectory ? "D" : string.Empty) +
            ((perm & (byte)EconetAccess.Locked) != 0 ? "L" : string.Empty) +
            ((perm & (byte)EconetAccess.OwnerWrite) != 0 ? (isOwner || !configuration.MdfsInfo ? "W" : "w") : string.Empty) +
            ((perm & (byte)EconetAccess.OwnerRead) != 0 ? (isOwner || !configuration.MdfsInfo ? "R" : "r") : string.Empty);

        string publicAccess =
            ((perm & (byte)EconetAccess.PublicWrite) != 0 ? (configuration.MdfsInfo && isOwner ? "w" : "W") : string.Empty) +
            ((perm & (byte)EconetAccess.PublicRead) != 0 ? (configuration.MdfsInfo && isOwner ? "r" : "R") : string.Empty);

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
