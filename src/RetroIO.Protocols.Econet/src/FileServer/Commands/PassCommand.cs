//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// <summary>
    /// Gets the full command name in upper case: <c>PASS</c>.
    /// </summary>
    public string Name => "PASS";

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
    /// Gets <c>false</c>: the command is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Changes the caller's own password. The argument is the old password then the new one;
    /// <c>""</c> stands for an empty password.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad parameters" when fewer than two words are given; 0xBC with the user store's
    /// message when it is "Wrong password"; or 0xFE with the user store's message for any other refusal.
    /// </returns>
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
