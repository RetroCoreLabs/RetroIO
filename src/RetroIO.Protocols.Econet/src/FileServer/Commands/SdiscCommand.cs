//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// <summary>
    /// Gets the full command name in upper case: <c>SDISC</c>.
    /// </summary>
    public string Name => "SDISC";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 2, so <c>*SD.</c> selects
    /// this command.
    /// </summary>
    public int MinimumAbbreviation => 2;

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
    /// Selects the disc named by the argument for the session.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad command" for an empty argument; 0xBF "Who are you ?" when the session's
    /// user has no profile; or the error code and message from the session store when it refuses the
    /// change.
    /// </returns>
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
