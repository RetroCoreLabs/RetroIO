//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
/// <c>&lt;username&gt; &lt;discs&gt;</c> where <c>discs</c> is <c>ALL</c> (0xFFFF), <c>NONE</c> (0x0000),
/// or up to four hex digits. Fewer than two tokens is <c>0xFF "Bad parameters"</c>; an unknown user is
/// <c>0xFF "Unknown user"</c>; a value that leaves the mask unchanged (including an unparseable one) is
/// <c>0xFF "Bad parameter or mask unchanged"</c>. Otherwise the new mask is persisted and takes effect
/// at the user's next login (read back by <see cref="EconetUserProfile.DiscMask"/> /
/// <c>DiscCatalog.IsDiscVisible</c>). Persist-only — the live session is untouched.
/// </remarks>
public sealed class SetDiscMaskCommand : IOsCliCommand
{
    private readonly string _name;

    /// <summary>Creates the command under one spelling (<c>DISCMASK</c> or <c>DISKMASK</c>).</summary>
    public SetDiscMaskCommand(string name)
    {
        _name = name;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <inheritdoc/>
    public int MinimumAbbreviation => 5;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => true;

    /// <inheritdoc/>
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
