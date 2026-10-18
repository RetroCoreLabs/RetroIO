//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*DISCMASK</c> (a.k.a. <c>*DISKMASK</c>) — a System user sets which discs a user can see.
/// </summary>
/// <remarks>
/// Reference behaviour (PiFS, facts only): System-only, abbreviation 5. Argument is
/// <c>username discs</c> where <c>discs</c> is <c>ALL</c> (0xFFFF), <c>NONE</c> (0x0000),
/// or up to four hex digits. Fewer than two tokens is <c>0xFF "Bad parameters"</c>; an unknown user is
/// <c>0xFF "Unknown user"</c>; a value that leaves the mask unchanged (including an unparseable one) is
/// <c>0xFF "Bad parameter or mask unchanged"</c>. Otherwise the new mask is persisted and takes effect
/// at the user's next login (read back by <see cref="EconetUserProfile.DiscMask"/> /
/// <c>DiscCatalog.IsDiscVisible</c>). Persist-only — the live session is untouched.
/// </remarks>
public sealed class SetDiscMaskCommand : IOsCliCommand
{
    private readonly string _name;

    /// <summary>
    /// Creates the command under one spelling (<c>DISCMASK</c> or <c>DISKMASK</c>).
    /// </summary>
    public SetDiscMaskCommand(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Gets the spelling given to the constructor: <c>DISCMASK</c> or <c>DISKMASK</c>.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 5, so <c>*DISCM.</c> or
    /// <c>*DISKM.</c> selects this command.
    /// </summary>
    public int MinimumAbbreviation => 5;

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
    /// Sets which discs a named user can see, from the argument <c>username discs</c>, where
    /// <c>discs</c> is <c>ALL</c> (0xFFFF), <c>NONE</c> (0x0000) or a hex number.
    /// </summary>
    /// <remarks>
    /// The new mask is stored for the user's next login; the live session is not changed. The result
    /// of the user store call is not checked.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; or 0xFF with "Bad parameters" when fewer than two words are given, "Unknown user"
    /// for an unknown user, or "Bad parameter or mask unchanged" when the value does not parse or
    /// equals the current mask.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        string[] tokens = call.Argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return FileServerReply.Error(FileServerErrors.Unsupported, "Bad parameters");

        string username = tokens[0];
        string discs = tokens[1];

        if (!call.Context.Users.TryGetProfileByUsername(username, out EconetUserProfile? profile) || profile == null)
            return FileServerReply.Error(FileServerErrors.Unsupported, "Unknown user");

        ushort current = profile.DiscMask;
        ushort mask = current;
        if (string.Equals(discs, "ALL", StringComparison.OrdinalIgnoreCase))
            mask = 0xFFFF;
        else if (string.Equals(discs, "NONE", StringComparison.OrdinalIgnoreCase))
            mask = 0x0000;
        else if (!ushort.TryParse(discs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask))
            mask = current; // Unparseable -> unchanged (matches PiFS), rejected below.

        // PiFS treats "no net change" (including a bad value) as an error rather than a silent no-op.
        if (mask == current)
            return FileServerReply.Error(FileServerErrors.Unsupported, "Bad parameter or mask unchanged");

        call.Context.Users.TrySetDiscMask(profile.UserId, mask);
        return FileServerReply.Success();
    }
}
