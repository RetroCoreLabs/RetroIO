//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// Shared argument parsing for <c>*SETLIB</c> and <c>*SETHOME</c>, which take either a single
/// <c>dir</c> (target = the caller) or a System-only <c>user dir</c> (target =
/// the named user). Mirrors the PiFS <c>sscanf("%10s %80s")</c> split and its error codes.
/// </summary>
internal static class SetDirectoryArguments
{
    // PiFS returns 0xFF with a specific message for every parse failure in these commands.
    private const byte ParseError = 0xFF;

    /// <summary>
    /// Parses the target user and directory token from a SETLIB/SETHOME argument.
    /// </summary>
    /// <param name="call">
    /// The OSCLI call (for the argument text, session and user directory).
    /// </param>
    /// <param name="systemOnlyTwoArg">
    /// When true (SETLIB), the two-argument "set another user" form requires the caller to be a System
    /// user — a non-System caller supplying a space gets PiFS's <c>"Bad parameters"</c>, not
    /// <c>"Insufficient access"</c>. When false (SETHOME, already System-gated by the dispatcher) the
    /// two-argument form is always accepted.
    /// </param>
    /// <param name="rejectDiscInTwoArg">
    /// When true (SETLIB), a directory naming a disc (leading <c>:</c>) in the two-argument form is
    /// rejected with <c>"Can't specify disc"</c>, matching PiFS.
    /// </param>
    /// <param name="targetUserId">
    /// The resolved target user (caller or named user).
    /// </param>
    /// <param name="dir">
    /// The directory token (may be <c>%</c> for "clear").
    /// </param>
    /// <param name="error">
    /// The reply to send on failure; null on success.
    /// </param>
    /// <returns>
    /// True when parsing succeeded.
    /// </returns>
    public static bool TryParse(
        in OsCliCall call,
        bool systemOnlyTwoArg,
        bool rejectDiscInTwoArg,
        out Guid targetUserId,
        out string dir,
        out byte[]? error)
    {
        targetUserId = Guid.Empty;
        dir = string.Empty;
        error = null;

        string argument = AcornPath.StripQuotes(call.Argument).Trim();
        string[] tokens = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length >= 2)
        {
            // Two-argument form: <user> <dir>.
            if (systemOnlyTwoArg && !call.Session!.IsAdmin)
            {
                error = FileServerReply.Error(ParseError, "Bad parameters");
                return false;
            }

            string username = tokens[0];
            dir = tokens[1];

            if (rejectDiscInTwoArg && dir.StartsWith(':'))
            {
                error = FileServerReply.Error(ParseError, "Can't specify disc");
                return false;
            }

            if (!call.Context.Users.TryGetProfileByUsername(username, out EconetUserProfile? profile) || profile == null)
            {
                error = FileServerReply.Error(ParseError, "No such user");
                return false;
            }

            targetUserId = profile.UserId;
            return true;
        }

        if (tokens.Length == 1)
        {
            // Single-argument form: set the caller's own directory.
            dir = tokens[0];
            targetUserId = call.Session!.RetroUserId;
            return true;
        }

        error = FileServerReply.Error(ParseError, "Bad parameters");
        return false;
    }
}
