//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*SETHOME</c> — permanently sets a user's home (URD) directory (takes effect at their next login).
/// </summary>
/// <remarks>
/// Reference behaviour (PiFS): this is a System-only command (in fs.c it lives inside the System-user
/// command block). It accepts <c>*SETHOME &lt;dir&gt;</c> (the System user's own home) or
/// <c>*SETHOME &lt;user&gt; &lt;dir&gt;</c> (another user's); an unknown user is <c>0xFF "No such
/// user"</c> and a missing directory token is <c>0xFF "Bad parameters"</c>. <c>%</c> clears the home
/// (reverting to the disc-layout default). A directory that does not resolve to an existing directory
/// is <c>0xA8 "Bad directory"</c>.
/// <para>
/// The System gate is enforced centrally by the dispatcher (<see cref="RequiresSystem"/>), so a
/// non-System caller gets <c>0xBD "Insufficient access"</c> — a deliberate, honest divergence from
/// PiFS, where the command is simply not recognised for a non-System user.
/// </para>
/// <para>
/// Like PiFS this is PERSIST-ONLY (writes <c>econet.home</c>, does not touch the live session); the new
/// home takes effect at the target user's next login. Divergence: RetroFS does not update the target's
/// home DISC here, so a cross-disc home set for another user relies on that user's existing home disc.
/// </para>
/// </remarks>
public sealed class SetHomeCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "SETHOME";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 4;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => true;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        if (!SetDirectoryArguments.TryParse(
                call, systemOnlyTwoArg: false, rejectDiscInTwoArg: false,
                out Guid targetUserId, out string dir, out byte[]? error))
        {
            return error!;
        }

        // "%" clears the home — persist an empty value so the next login falls back to the default.
        if (dir == "%")
        {
            call.Context.Users.TrySetHome(targetUserId, string.Empty);
            return FileServerReply.Success();
        }

        // Validate the directory resolves for the acting session, exactly as *DIR does.
        string acorn = AcornDirectoryNavigation.Resolve(call.Session!.AcornHomePath, dir);
        if (!call.Context.Paths.TryResolve(call.Session!, acorn, out string unixPath, out _, out _) ||
            !call.Context.FileSystem.DirectoryExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.BadDirectory, "Bad directory");
        }

        call.Context.Users.TrySetHome(targetUserId, acorn);
        return FileServerReply.Success();
    }
}
