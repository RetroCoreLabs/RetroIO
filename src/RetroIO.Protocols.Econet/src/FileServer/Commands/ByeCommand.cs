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

    /// <inheritdoc/>
    public string Name => "BYE";

    /// <inheritdoc/>
    /// <remarks>
    /// Must be typed in full. The legacy dispatch arm this replaced accepted only exact "BYE" or
    /// "BYE ", so a minimum equal to the name's length preserves that behaviour exactly rather than
    /// silently introducing "*B." as a logout — which would be a new, untested abbreviation.
    /// </remarks>
    public int MinimumAbbreviation => 3;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        return FileServerLogout.Execute(call.Context, call.Request.ClientNet, call.Request.ClientStation);
    }
}
