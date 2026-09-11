//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
/// Reference behaviour (PiFS): any logged-in user may set their OWN library with <c>*SETLIB &lt;dir&gt;</c>;
/// a System user may additionally set another user's with <c>*SETLIB &lt;user&gt; &lt;dir&gt;</c>. A
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
    /// <inheritdoc/>
    public string Name => "SETLIB";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 4;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    // Self-service is allowed for any user; the two-argument "set another user" form is System-gated
    // inside Execute (so a non-admin still gets the PiFS "Bad parameters" answer, not "Insufficient
    // access").
    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
