//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>SetBootOpt</c> (fsop 0x16) — sets the caller's boot option (the *OPT 4 value). Options above 7
/// are rejected with <c>0xBD "Bad option"</c> (PiFS <c>fs_set_bootopt</c>).
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleSetBootOpt</c>, unchanged. The login gate is the
/// dispatcher's job now (<see cref="RequiresLogin"/>), so <see cref="FsOpCall.Session"/> is non-null.
/// The new value is both applied to the live session and persisted to the user record so it survives
/// logout and is reported at the next login.
/// </remarks>
public sealed class SetBootOptOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.SetBootOpt"/> (0x16).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.SetBootOpt;

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
    /// Sets the caller's boot option from the first parameter byte, on the live session and in the
    /// stored user record.
    /// </summary>
    /// <remarks>
    /// With no parameter bytes nothing changes and the reply is success. When the user store refuses
    /// the change (the user was removed while logged in) the live session is left as it was and the
    /// reply is 0xBD "Bad user ID", the PiFS answer for a missing user.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success, 0xBD "Bad option" for a value above 7, or 0xBD "Bad user ID" when the user store
    /// refuses the change.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;

        if (call.FsRequest.Parameters.Length > 0)
        {
            byte newBootOpt = call.FsRequest.Parameters[0];
            // PiFS fs_set_bootopt rejects options above 7.
            if (newBootOpt > 7)
                return FileServerReply.Error(FileServerErrors.InsufficientAccess, "Bad option");

            // Persist to the user record so it is remembered across logins (and reported at login).
            // PiFS fs_set_bootopt answers 0xBD "Bad user ID" when the user cannot be found.
            if (!call.Context.Users.TrySetBootOpt(session.RetroUserId, newBootOpt))
                return FileServerReply.Error(FileServerErrors.InsufficientAccess, "Bad user ID");

            session.BootOpt = newBootOpt;
        }

        return FileServerReply.Success();
    }
}
