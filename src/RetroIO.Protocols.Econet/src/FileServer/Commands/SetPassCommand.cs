//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*SETPASS</c> — a System user sets ANOTHER user's password (no current-password check).
/// </summary>
/// <remarks>
/// Reference behaviour (PiFS, facts only): System-only, abbreviation 4. Argument is
/// <c>&lt;username&gt; &lt;password&gt;</c>; missing either is <c>0xFF "Bad parameter"</c>; an unknown
/// user is <c>0xFF "Unknown user"</c>. Unlike <c>*PASS</c> there is no old-password verification — this
/// is the admin override used to reset a locked-out user. The empty marker <c>""</c> clears the password
/// (only where the account permits an empty password, else <c>0xFF "Bad password"</c>).
/// <para>
/// The password argument is masked from every log by <see cref="EconetLogRedaction"/> (PiFS logs it in
/// the clear at <c>fs.c:10921</c> — RetroFS does not).
/// </para>
/// </remarks>
public sealed class SetPassCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "SETPASS";

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
            return FileServerReply.Error(FileServerErrors.Unsupported, "Bad parameter");

        string username = tokens[0];
        string newPassword = tokens[1] == "\"\"" ? string.Empty : tokens[1]; // Acorn "" = empty password

        if (!call.Context.Users.TryGetProfileByUsername(username, out EconetUserProfile? profile) || profile == null)
            return FileServerReply.Error(FileServerErrors.Unsupported, "Unknown user");

        if (!call.Context.Users.TrySetPassword(profile.UserId, newPassword, out string errorMessage))
            return FileServerReply.Error(FileServerErrors.Unsupported, errorMessage);

        return FileServerReply.Success();
    }
}
