//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*FSLIST</c> — reports the server version string.
/// </summary>
/// <remarks>
/// A RetroFS extra with no reference equivalent, so it is matched by full name only (no
/// abbreviation) to avoid inventing an abbreviation the reference never defined.
/// </remarks>
public sealed class FsListCommand : IOsCliCommand
{
    private readonly FileServerContext _context;

    /// <summary>
    /// Creates the command against the context whose version string it reports.
    /// </summary>
    /// <param name="context">
    /// The server context carrying the version string.
    /// </param>
    public FsListCommand(FileServerContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the full command name in upper case: <c>FSLIST</c>.
    /// </summary>
    public string Name => "FSLIST";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 6, the whole name, so the
    /// command can only be typed in full.
    /// </summary>
    public int MinimumAbbreviation => 6;

    /// <summary>
    /// Gets <c>true</c>: the command needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="OsCliCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the command is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Reports the server version string.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The version reply carrying <c>VersionString</c> from the server context in <paramref name="call"/>.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        return FileServerReply.Version(call.Context.VersionString);
    }
}
