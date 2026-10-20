//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>GetObjectInfo</c> (fsop 0x12) — the metadata reader behind *INFO, *EX and *CAT's directory
/// block. Supports the several MDFS "info command" sub-forms (byte 5): access/dates, the canonical
/// path form, and the catalogue-directory form (command 6). A successful reply is framed with AUN
/// control byte 0 (the dispatcher handles this via the opcode-aware wrap).
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleGetObjectInfo</c>, unchanged — the last opcode of the
/// strangler refactor. Every <c>Reply(request, fsRequest, X)</c> became a bare <c>return X</c>; the
/// dispatcher owns the framing (<see cref="Handlers.FsOpCommandHandler"/>). The login gate is the
/// dispatcher's job, so the old in-body <c>RequireSession</c> null-check is gone.
/// </remarks>
public sealed class GetObjectInfoOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.GetObjectInfo"/> (0x12).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.GetObjectInfo;

    /// <summary>
    /// Gets <c>true</c>: the operation needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="FsOpCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the operation is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Returns metadata for the object named in the request, in the form chosen by the info command
    /// byte.
    /// </summary>
    /// <remarks>
    /// Behaviour by case:
    ///  - a relative-to handle of 0, a path that does not resolve, or an object that does not exist gives
    ///    0xD6 "Not found" for command 6 and the object-not-found reply for the other commands.
    ///  - command 6 on a file gives 0xAF "Types don't match".
    ///  - the canonical form (<see cref="FileServerOpcodes.GetObjectInfoCanonical"/>) adds the path and a
    ///    CR after the info reply.
    ///  - command 6 fills in the 10-byte directory name and the entry count, capped at 255.
    /// The owner byte is 0 when the caller owns the object (see ObjectOwnership) and 0xFF when not. For commands 4, 5 and 96 a system user counts as owner (fs.c line 5694). For command 6 only the owner does (fs.c line 5735).
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The info reply for the command, one of the error replies above, or 0xCC "Bad filename" when
    /// the request does not parse.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;
        FileServerContext context = call.Context;
        FileServerRequest fsRequest = call.FsRequest;

        if (!GetObjectInfoRequestParser.TryParse(fsRequest, out byte command, out ReadOnlySpan<byte> pathBytes))
            return FileServerReply.Error(FileServerErrors.BadPath, "Bad filename");

        // fs.c lines 5392 to 5396: command 0x40 is an MDFS call and needs the MdfsFunctions flag.
        if (command == 0x40 && !context.Configuration.MdfsFunctions)
            return FileServerReply.Error(FileServerErrors.Unsupported, "MDFS Unsupported");

        string path = pathBytes.IsEmpty
            ? string.Empty
            : AcornPath.ReadCrTerminatedString(pathBytes);

        // PiFS conformance (utilities/fs.c fs_normalize_path_wildcard:2341): the relative-to
        // handle (request byte 3 / CWD) must reference an OPEN handle. PiFS returns NODIR ->
        // "Not found" (0xd6 for cmd 6) when it is 0/unopened. BeebEm's *CAT sends CWD handle 0,
        // so we must NOT silently substitute the session CWD here: doing so produced a success
        // directory block that BeebEm rejects and re-requests forever -> "Net error".
        byte rawRelativeTo = AcornFileHandles.DivHandle(fsRequest.Cwd, context.Configuration.ManyHandles);
        if (rawRelativeTo == 0)
        {
            if (command == 6)
                return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

            return FileServerReply.ObjectInfoNotFound();
        }

        byte relativeTo = GetObjectInfoRequestParser.ResolveRelativeTo(fsRequest, session, context.Configuration.ManyHandles);
        string unixPath;
        bool resolved;
        if (command == 6)
        {
            resolved = CatalogueEnumerator.TryResolveCatalogueDirectory(context, session, path, relativeTo, out unixPath);
        }
        else
        {
            resolved = context.Paths.TryResolve(session, path, relativeTo, out unixPath, out _, out _);
        }

        if (!resolved)
        {
            if (command == 6)
                return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

            return FileServerReply.ObjectInfoNotFound();
        }

        bool isDir = context.FileSystem.DirectoryExists(unixPath);
        bool isFile = !isDir && context.FileSystem.FileExists(unixPath);
        if (!isDir && !isFile)
        {
            if (command == 6)
                return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

            return FileServerReply.ObjectInfoNotFound();
        }

        if (command == 6 && !isDir)
            return FileServerReply.Error(FileServerErrors.TypesDontMatch, "Types don't match");

        byte fileType = isDir ? (byte)0x02 : (byte)0x01;
        uint load = 0;
        uint exec = 0;
        int length = 0;
        byte access = context.Configuration.DefaultFilePerm;
        byte day = 0;
        byte monthYear = 0;
        if (isFile)
        {
            AcornInfStore.TryReadAttributes(context.FileSystem, unixPath, out load, out exec, out length, out access);
            if (length == 0)
            {
                RetroIO.Server.Core.FileSystem.BackendFileInfo? info = context.FileSystem.GetFileInfo(unixPath);
                length = (int)(info?.Length ?? 0);
                if (info?.LastWriteTimeUtc != null)
                    AcornPath.EncodeFileDate(info.LastWriteTimeUtc.Value, out day, out monthYear);
            }
            else
            {
                RetroIO.Server.Core.FileSystem.BackendFileInfo? info = context.FileSystem.GetFileInfo(unixPath);
                if (info?.LastWriteTimeUtc != null)
                    AcornPath.EncodeFileDate(info.LastWriteTimeUtc.Value, out day, out monthYear);
            }
        }

        // fs.c line 5694: the owner byte of commands 4, 5 and 96 is 0 for the owner or a system user, 0xFF for anyone else.
        // fs.c line 5735: the owner byte of command 6 is 0 for the owner only, a system user is not counted.
        ushort owner = ObjectOwnership.GetOwner(context, unixPath);
        bool isOwner = ObjectOwnership.IsEffectiveOwner(session, owner);
        bool isDirectoryOwner = ObjectOwnership.IsOwner(session, owner);

        if (command == FileServerOpcodes.GetObjectInfoCanonical)
        {
            byte[] canonical = Encoding.ASCII.GetBytes(path + "\r");
            byte[] baseReply = FileServerReply.ObjectInfoForCommand(
                command, fileType, load, exec, length, access, day, monthYear, isOwner, ReadOnlySpan<byte>.Empty, 0);
            byte[] reply = new byte[baseReply.Length + canonical.Length];
            baseReply.CopyTo(reply, 0);
            canonical.CopyTo(reply.AsSpan(baseReply.Length));
            return reply;
        }

        byte entryCount = 0;
        byte[] dirName10 = new byte[10];
        if (command == 6)
        {
            CatalogueEnumerator.WriteCatalogueDirName10(
                dirName10, context.FileSystem, session, unixPath, context.Discs.GetDiscDirectoryPath(session.CurrentDisc));
            int count = CatalogueEnumerator.CountEntries(context.FileSystem, unixPath);
            entryCount = (byte)Math.Min(count, 255);
        }

        // The owner byte is catalogue metadata, not an access gate (see the owner lines above).
        byte[] payload = FileServerReply.ObjectInfoForCommand(
            command,
            fileType,
            load,
            exec,
            length,
            access,
            day,
            monthYear,
            isOwner,
            dirName10,
            entryCount,
            isDirectoryOwner);
        return payload;
    }
}
