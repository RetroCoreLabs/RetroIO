//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
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
    /// <inheritdoc/>
    public string Name => "CDIR";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 2;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
        return FileServerReply.Success();
    }
}
