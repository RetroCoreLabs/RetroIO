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
    /// The move uses <c>File.Move</c> or <c>Directory.Move</c> on the host paths directly, not the
    /// filesystem backend.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad command" when fewer than two names are given; or 0xD6 "Not found" when
    /// either name does not resolve or the old name is neither a file nor a directory.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<from> <to>".
        string[] parts = call.Argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        if (!call.Context.Paths.TryResolve(call.Session!, parts[0], out string fromPath, out _, out _) ||
            !call.Context.Paths.TryResolve(call.Session!, parts[1], out string toPath, out _, out _))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        if (call.Context.FileSystem.FileExists(fromPath))
            File.Move(fromPath, toPath);
        else if (call.Context.FileSystem.DirectoryExists(fromPath))
            Directory.Move(fromPath, toPath);
        else
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

        return FileServerReply.Success();
    }
}
