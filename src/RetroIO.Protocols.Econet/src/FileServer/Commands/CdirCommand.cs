//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*CDIR</c> — creates a directory under the current directory.
/// </summary>
/// <remarks>
/// <para><b>Previously missing from OSCLI.</b></para>
/// The CreateDir file-server operation (fsop 0x1B) already existed, but there was no <c>*CDIR</c>
/// command, so a client that sends the command as OSCLI text — which the reference server expects,
/// since it handles <c>*CDIR</c> in its own OSCLI dispatch and does the make-directory there — fell
/// through to the unknown-command reply and failed. This adds the command, doing the work directly
/// and returning success, matching the reference.
///
/// The directory is created relative to the session's current directory, as the reference does.
/// </remarks>
public sealed class CdirCommand : IOsCliCommand
{
    /// <summary>
    /// Gets the full command name in upper case: <c>CDIR</c>.
    /// </summary>
    public string Name => "CDIR";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 2, so <c>*CD.</c> selects
    /// this command.
    /// </summary>
    public int MinimumAbbreviation => 2;

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
    /// Creates a directory at the path named by the argument, resolved through the session's path
    /// resolver.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad command" when the name is empty or gives no host path; or 0xFE
    /// "Already exists" when a file or directory is already there.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        string dirname = AcornPath.StripQuotes(call.Argument);
        if (dirname.Length == 0)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        // Resolve the target through the path resolver so absolute forms work: "$.X" means the disc
        // root and ":disc.X" means another disc, NOT a literal directory called "$"/":disc" under the
        // CWD. A relative "FRED" still resolves under the current directory. The new directory does not
        // exist yet, so (as with *COPY's destination) TryResolve returns false but still fills in the
        // computed host path — which is exactly the path we create.
        call.Context.Paths.TryResolve(call.Session!, dirname, out string unixPath, out _, out _);
        if (string.IsNullOrEmpty(unixPath))
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        if (call.Context.FileSystem.DirectoryExists(unixPath) || call.Context.FileSystem.FileExists(unixPath))
            return FileServerReply.Error(FileServerErrors.BadCommand, "Already exists");

        call.Context.FileSystem.CreateDirectory(unixPath);

        // fs.c line 7131: a new directory is owned by the user who made it.
        ObjectOwnership.SetOwner(call.Context, call.Session!, unixPath);
        return FileServerReply.Success();
    }
}
