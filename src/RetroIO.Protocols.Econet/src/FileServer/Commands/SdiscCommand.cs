//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*SDISC</c> — selects a different disc for the session.
/// </summary>
public sealed class SdiscCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "SDISC";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 2;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        string discReference = call.Argument.Trim();
        if (discReference.Length == 0)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        EconetUserProfile? profile = call.Context.Users.GetProfile(call.Session!.RetroUserId);
        if (profile == null)
            return FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?");

        if (!call.Context.Sessions.TryChangeDisc(call.Session!, profile, discReference, out byte errorCode, out string errorMessage))
            return FileServerReply.Error(errorCode, errorMessage);

        return FileServerReply.Success();
    }
}
