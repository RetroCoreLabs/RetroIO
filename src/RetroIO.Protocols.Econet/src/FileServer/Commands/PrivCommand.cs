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
/// <c>username priv</c> where <c>priv</c> is a letter, optionally with a <c>+</c>/<c>-</c>
/// set/unset prefix. Missing username or priv is <c>0xFE "Bad command"</c>; an unknown user is
/// <c>0xBC "User not found"</c>.
/// <para>
/// Supported privilege states (mutually exclusive, as in PiFS): <c>S</c>/<c>+S</c> System (admin,
/// unlocked); <c>U</c> or <c>-S</c>/<c>-L</c> Normal; <c>L</c>/<c>+L</c> Locked (password verifies but
/// login is refused with <c>0xBC "Account locked"</c>). System maps to <see cref="RetroIO.Identity.RetroUser.IsAdmin"/>,
/// Locked to the <c>econet.locked</c> flag checked in the login path. Persisted immediately; a live
/// session keeps its privilege until next login.
/// </para>
/// <para>
/// Still deferred: <c>N</c> (no-password-change), <c>C</c> (chroot) and the <c>D</c>/REMUSER
/// (delete) forms return <c>0xFF "Unsupported privilege"</c> rather than silently doing nothing —
/// delete belongs in the management API.
/// </para>
/// </remarks>
public sealed class PrivCommand : IOsCliCommand
{
    /// <summary>
    /// Gets the full command name in upper case: <c>PRIV</c>.
    /// </summary>
    public string Name => "PRIV";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 4, the whole name, so the
    /// command can only be typed in full.
    /// </summary>
    public int MinimumAbbreviation => 4;

    /// <summary>
    /// Gets <c>true</c>: the command needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="OsCliCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>true</c>: only a system-privileged user may run this command. The dispatcher answers
    /// everyone else with "Insufficient access".
    /// </summary>
    public bool RequiresSystem => true;

    /// <summary>
    /// Sets a named user's privilege from the argument <c>username priv</c>, where <c>priv</c> is a
    /// letter with an optional <c>+</c> or <c>-</c> in front.
    /// </summary>
    /// <remarks>
    /// The letters:
    ///  - <c>S</c>: System (admin, unlocked); with <c>-</c>, a Normal user.
    ///  - <c>U</c>: a Normal, unlocked user.
    ///  - <c>L</c>: Locked; with <c>-</c>, a Normal user.
    ///  - any other letter: 0xFF "Unsupported privilege".
    /// When the user store refuses the change (the user was removed after it was found), <c>S</c>,
    /// <c>U</c> and <c>L</c> reply 0xBC "User not found", as PiFS does for a user it cannot find.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad command" when fewer than two words are given or the privilege is only a
    /// sign; 0xBC "User not found" for an unknown user or when the user store refuses the change; or
    /// 0xFF "Unsupported privilege".
    /// </returns>
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
        bool stored;
        switch (privChar)
        {
            case 'S': // System: +S/S grants (admin, unlocked); -S -> Normal.
                stored = call.Context.Users.TrySetPrivilege(profile.UserId, isAdmin: setPriv, isLocked: false);
                break;

            case 'U': // Unlocked Normal user.
                stored = call.Context.Users.TrySetPrivilege(profile.UserId, isAdmin: false, isLocked: false);
                break;

            case 'L': // Locked: +L/L locks (login refused); -L -> Normal.
                stored = call.Context.Users.TrySetPrivilege(profile.UserId, isAdmin: false, isLocked: setPriv);
                break;

            default: // N (no-password-change) / C (chroot) / D (delete/REMUSER) not modelled yet —
                     // honest error, never a silent no-op. Delete belongs in the management API.
                return FileServerReply.Error(FileServerErrors.Unsupported, "Unsupported privilege");
        }

        // The user was found a moment ago; a refused store means it has gone since (PiFS 0xBC).
        if (!stored)
            return FileServerReply.Error(0xBC, "User not found");

        return FileServerReply.Success();
    }
}
