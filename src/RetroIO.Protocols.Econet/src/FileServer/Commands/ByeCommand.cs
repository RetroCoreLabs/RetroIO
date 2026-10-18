//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Sessions;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*BYE</c> — logs the calling station out, closing its handles and releasing bulk transfers.
/// </summary>
public sealed class ByeCommand : IOsCliCommand
{
    private readonly FileServerContext _context;

    /// <summary>
    /// Creates the command against the server context it logs out from.
    /// </summary>
    /// <param name="context">
    /// The server context owning the sessions, handles, and bulk transfers to release.
    /// </param>
    public ByeCommand(FileServerContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the full command name in upper case: <c>BYE</c>.
    /// </summary>
    public string Name => "BYE";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 3, the whole name, so the
    /// command can only be typed in full.
    /// </summary>
    /// <remarks>
    /// Must be typed in full. The legacy dispatch arm this replaced accepted only exact "BYE" or
    /// "BYE ", so a minimum equal to the name's length preserves that behaviour exactly rather than
    /// silently introducing "*B." as a logout — which would be a new, untested abbreviation.
    /// </remarks>
    public int MinimumAbbreviation => 3;

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
    /// Logs the calling station out through <c>FileServerLogout.Execute</c>, using the client net and
    /// station of the request.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The reply payload built by <c>FileServerLogout.Execute</c>.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        return FileServerLogout.Execute(call.Context, call.Request.ClientNet, call.Request.ClientStation);
    }
}
