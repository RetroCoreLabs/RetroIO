//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>CatHeader</c> (fsop 0x04) — returns the catalogue header for a directory: its Acorn leaf name,
/// the owner/public marker, and the disc name. A missing directory is <c>0xD6 "Not found"</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleCatHeader</c>, unchanged.
/// </remarks>
public sealed class CatHeaderOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.CatHeader;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;
        FileServerContext context = call.Context;

        string path = call.FsRequest.Parameters.Length > 0
            ? AcornPath.ReadCrTerminatedString(call.FsRequest.Parameters)
            : ".";

        if (!context.Paths.TryResolve(session, path, call.FsRequest.ResolvedCwd(session), out string unixPath, out _, out _) ||
            !context.FileSystem.DirectoryExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        // PiFS (fs.c:10388): "$" names the DISC ROOT only; every other directory reports its own
        // leaf. This used to report "$" for the user's HOME, which disagreed with PiFS and left the
        // disc root printing its host folder name. Kept in step with
        // CatalogueEnumerator.WriteCatalogueDirName10 so *CAT's header and directory block agree.
        string tail = context.FileSystem.GetFileName(unixPath);
        if (tail == "$" || string.IsNullOrEmpty(tail))
            tail = "$";
        else if (CatalogueEnumerator.IsDiscRootDirectory(unixPath, context.Discs.GetDiscDirectoryPath(session.CurrentDisc)))
            tail = "$";

        DiscEntry? disc = context.Discs.GetByIndex(session.CurrentDisc);
        string discName = disc?.AcornName ?? context.DiscName;
        return FileServerReply.CatHeader(tail, 'O', discName);
    }
}
