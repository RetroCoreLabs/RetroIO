//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.SetBootOpt;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;

        if (call.FsRequest.Parameters.Length > 0)
        {
            byte newBootOpt = call.FsRequest.Parameters[0];
            // PiFS fs_set_bootopt rejects options above 7.
            if (newBootOpt > 7)
                return FileServerReply.Error(FileServerErrors.InsufficientAccess, "Bad option");

            session.BootOpt = newBootOpt;
            // Persist to the user record so it is remembered across logins (and reported at login).
            call.Context.Users.TrySetBootOpt(session.RetroUserId, newBootOpt);
        }

        return FileServerReply.Success();
    }
}
