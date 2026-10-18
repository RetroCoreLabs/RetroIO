//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*RENAME</c> — renames or moves a file or directory.
/// </summary>
public sealed class RenameCommand : IOsCliCommand
{
    /// <summary>
    /// Gets the full command name in upper case: <c>RENAME</c>.
    /// </summary>
    public string Name => "RENAME";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 3, so <c>*REN.</c> selects
    /// this command.
    /// </summary>
    public int MinimumAbbreviation => 3;

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
    /// Renames or moves a file or directory. The argument is the old name then the new name, both
    /// resolved for the caller's session.
    /// </summary>
    /// <remarks>
    /// The move goes through the filesystem backend, so it works with any backend, not only the
    /// local disk.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad command" when fewer than two names are given; 0xD6 "Not found" when
    /// the old name does not resolve or is neither a file nor a directory; or 0xFE "Already exists"
    /// when the new name is already taken.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<from> <to>".
        string[] parts = call.Argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        if (!call.Context.Paths.TryResolve(call.Session!, parts[0], out string fromPath, out _, out _))
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

        // The new name does not exist yet, so (as with *CDIR) TryResolve returns false but still fills
        // in the computed host path, which is exactly the path to move to.
        call.Context.Paths.TryResolve(call.Session!, parts[1], out string toPath, out _, out _);
        if (string.IsNullOrEmpty(toPath))
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

        if (call.Context.FileSystem.FileExists(toPath) || call.Context.FileSystem.DirectoryExists(toPath))
            return FileServerReply.Error(FileServerErrors.BadCommand, "Already exists");

        // Through the backend: the paths belong to it and need not be paths on the host disk.
        if (call.Context.FileSystem.FileExists(fromPath))
            call.Context.FileSystem.MoveFile(fromPath, toPath);
        else if (call.Context.FileSystem.DirectoryExists(fromPath))
            call.Context.FileSystem.MoveDirectory(fromPath, toPath);
        else
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

        return FileServerReply.Success();
    }
}
