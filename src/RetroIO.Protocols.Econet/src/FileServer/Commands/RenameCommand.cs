//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public string Name => "RENAME";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 3;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
