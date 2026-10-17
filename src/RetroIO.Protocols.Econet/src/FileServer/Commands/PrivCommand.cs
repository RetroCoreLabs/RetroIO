//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*PRIV</c> — a System user sets another user's privilege.
/// </summary>
/// <remarks>
/// Reference behaviour (PiFS, facts only): System-only, abbreviation 4. Argument is
/// <c>&lt;username&gt; &lt;priv&gt;</c> where <c>priv</c> is a letter, optionally with a <c>+</c>/<c>-</c>
/// set/unset prefix. Missing username or priv is <c>0xFE "Bad command"</c>; an unknown user is
/// <c>0xBC "User not found"</c>.
/// <para>
/// Supported privilege states (mutually exclusive, as in PiFS): <c>S</c>/<c>+S</c> System (admin,
/// unlocked); <c>U</c> or <c>-S</c>/<c>-L</c> Normal; <c>L</c>/<c>+L</c> Locked (password verifies but
/// login is refused with <c>0xBC "Account locked"</c>). System maps to <see cref="RetroUser.IsAdmin"/>,
/// Locked to the <c>econet.locked</c> flag checked in the login path. Persisted immediately; a live
/// session keeps its privilege until next login.
/// </para>
/// <para>
/// <b>Still deferred:</b> <c>N</c> (no-password-change), <c>C</c> (chroot) and the <c>D</c>/REMUSER
/// (delete) forms return <c>0xFF "Unsupported privilege"</c> rather than silently doing nothing —
/// delete belongs in the management API.
/// </para>
/// </remarks>
public sealed class PrivCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "PRIV";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 4;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => true;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        string[] tokens = call.Argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        string username = tokens[0];
        string privToken = tokens[1];

        // Optional +/- set/unset prefix; default is to set.
        bool setPriv = true;
        char privChar;
        if (privToken[0] == '+' || privToken[0] == '-')
        {
            setPriv = privToken[0] == '+';
            if (privToken.Length < 2)
                return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");
            privChar = char.ToUpperInvariant(privToken[1]);
        }
        else
        {
            privChar = char.ToUpperInvariant(privToken[0]);
        }

        if (!call.Context.Users.TryGetProfileByUsername(username, out EconetUserProfile? profile) || profile == null)
            return FileServerReply.Error(0xBC, "User not found");

        // PiFS privileges are mutually-exclusive states, so each maps to a full (isAdmin, isLocked) pair.
        // A "-" prefix (setPriv false) demotes to the Normal user in every case.
        switch (privChar)
        {
            case 'S': // System: +S/S grants (admin, unlocked); -S -> Normal.
                call.Context.Users.TrySetPrivilege(profile.UserId, isAdmin: setPriv, isLocked: false);
                return FileServerReply.Success();

            case 'U': // Unlocked Normal user.
                call.Context.Users.TrySetPrivilege(profile.UserId, isAdmin: false, isLocked: false);
                return FileServerReply.Success();

            case 'L': // Locked: +L/L locks (login refused); -L -> Normal.
                call.Context.Users.TrySetPrivilege(profile.UserId, isAdmin: false, isLocked: setPriv);
                return FileServerReply.Success();

            default: // N (no-password-change) / C (chroot) / D (delete/REMUSER) not modelled yet —
                     // honest error, never a silent no-op. Delete belongs in the management API.
                return FileServerReply.Error(FileServerErrors.Unsupported, "Unsupported privilege");
        }
    }
}
