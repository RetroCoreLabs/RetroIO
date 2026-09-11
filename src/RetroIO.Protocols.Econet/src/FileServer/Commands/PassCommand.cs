//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*PASS</c> — changes the caller's own password.
/// </summary>
/// <remarks>
/// <para><b>Previously missing.</b></para>
/// Takes the current password then the new one: <c>*PASS old new</c>. An empty field is typed as
/// <c>""</c> — used when the account currently has no password, or to clear it. The new password is
/// persisted through RetroIDP.
///
/// The password bytes are redacted from all logs by <c>EconetLogRedaction</c>, the same as login.
/// </remarks>
public sealed class PassCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "PASS";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 4;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<old> <new>", each token possibly the empty marker "".
        string[] parts = call.Argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad parameters");

        string oldPassword = UnquoteEmpty(parts[0]);
        string newPassword = UnquoteEmpty(parts[1]);

        if (!call.Context.Users.TryChangePassword(call.Session!.RetroUserId, oldPassword, newPassword, out string errorMessage))
        {
            byte code = errorMessage == "Wrong password" ? FileServerErrors.WrongPassword : FileServerErrors.BadCommand;
            return FileServerReply.Error(code, errorMessage);
        }

        return FileServerReply.Success();
    }

    // Acorn uses "" to denote an empty password field.
    private static string UnquoteEmpty(string token) => token == "\"\"" ? string.Empty : token;
}
