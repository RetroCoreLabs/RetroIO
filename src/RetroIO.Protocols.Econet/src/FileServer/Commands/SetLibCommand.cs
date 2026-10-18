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
/// <c>*SETLIB</c> — permanently sets a user's library directory (takes effect at their next login).
/// </summary>
/// <remarks>
/// Reference behaviour (PiFS): any logged-in user may set their OWN library with <c>*SETLIB dir</c>;
/// a System user may additionally set another user's with <c>*SETLIB user dir</c>. A
/// non-System user supplying a space (i.e. attempting the two-argument form) is rejected with
/// <c>0xFF "Bad parameters"</c>. In the System two-argument form the directory may not name a disc
/// (leading <c>:</c>) — <c>0xFF "Can't specify disc"</c>; an unknown user is <c>0xFF "No such user"</c>.
/// <c>%</c> clears the library (reverting to the disc-layout default). A directory that does not
/// resolve to an existing directory is <c>0xA8 "Bad library"</c>.
/// <para>
/// Like PiFS this is PERSIST-ONLY: it writes the user's <c>econet.lib</c> record and does not touch the
/// caller's live session; the new library takes effect at the target user's next login, where the login
/// path resolves it (see <see cref="LoginCommand"/>).
/// </para>
/// <para>
/// Divergence: RetroFS does not enforce PiFS's "library must be on the user's home disc" rule beyond
/// what path resolution already gives; the single-disc case is unaffected.
/// </para>
/// </remarks>
public sealed class SetLibCommand : IOsCliCommand
{
    /// <summary>
    /// Gets the full command name in upper case: <c>SETLIB</c>.
    /// </summary>
    public string Name => "SETLIB";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 4, so <c>*SETL.</c> selects
    /// this command.
    /// </summary>
    public int MinimumAbbreviation => 4;

    /// <summary>
    /// Gets <c>true</c>: the command needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="OsCliCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    // Self-service is allowed for any user; the two-argument "set another user" form is System-gated
    // inside Execute (so a non-admin still gets the PiFS "Bad parameters" answer, not "Insufficient
    // access").
    /// <summary>
    /// Gets <c>false</c>: any logged-in user may set their own library. The two-word form that sets
    /// another user's library is limited to system users inside <see cref="Execute"/>.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Stores a user's library directory for their next login. The argument is <c>dir</c> for the
    /// caller or, for a system user only, <c>user dir</c> for another user; <c>%</c> clears it.
    /// </summary>
    /// <remarks>
    /// The directory is resolved against the caller's library path and must be an existing directory.
    /// The live session is not changed, and the result of the user store call is not checked.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; the error reply from argument parsing; or 0xA8 "Bad library" when the directory does
    /// not resolve to an existing directory.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        if (!SetDirectoryArguments.TryParse(
                call, systemOnlyTwoArg: true, rejectDiscInTwoArg: true,
                out Guid targetUserId, out string dir, out byte[]? error))
        {
            return error!;
        }

        // "%" clears the library — persist an empty value so the next login falls back to the default.
        if (dir == "%")
        {
            call.Context.Users.TrySetLib(targetUserId, string.Empty);
            return FileServerReply.Success();
        }

        // Validate the directory resolves for the acting session, exactly as *LIB does.
        string acorn = AcornDirectoryNavigation.Resolve(call.Session!.AcornLibPath, dir);
        if (!call.Context.Paths.TryResolve(call.Session!, acorn, out string unixPath, out _, out _) ||
            !call.Context.FileSystem.DirectoryExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.BadLibrary, "Bad library");
        }

        call.Context.Users.TrySetLib(targetUserId, acorn);
        return FileServerReply.Success();
    }
}
