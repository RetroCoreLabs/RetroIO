//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.CatHeader"/> (0x04).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.CatHeader;

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
    /// Returns the catalogue header for the directory named in the parameters, or for the current
    /// directory when there are no parameters.
    /// </summary>
    /// <remarks>
    /// The leaf name is <c>$</c> for the disc root and the directory's own leaf otherwise. The owner
    /// marker is always <c>O</c>. The disc name is the current disc's Acorn name, or the context's disc
    /// name when the disc is not found.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The catalogue header reply, or 0xD6 "Not found" when the path does not resolve to an existing
    /// directory.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;
        FileServerContext context = call.Context;

        string path = call.FsRequest.Parameters.Length > 0
            ? AcornPath.ReadCrTerminatedString(call.FsRequest.Parameters)
            : ".";

        if (!context.Paths.TryResolve(session, path, call.FsRequest.ResolvedCwd(session, context.Configuration.ManyHandles), out string unixPath, out _, out _) ||
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
