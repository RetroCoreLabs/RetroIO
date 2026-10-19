//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*SAVE</c> — resolves the target and returns save metadata for the ROM to push the data.
/// </summary>
public sealed class SaveCommand : IOsCliCommand
{
    /// <summary>
    /// Gets the full command name in upper case: <c>SAVE</c>.
    /// </summary>
    public string Name => "SAVE";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 2, so <c>*SA.</c> selects
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
    /// Resolves the target file and returns the save reply for the ROM to send the data. The argument
    /// is the file name, then optional load, exec and length numbers, each parsed as decimal.
    /// </summary>
    /// <remarks>
    /// A missing or bad load address is 0, and a missing or bad exec address takes the load address. A
    /// missing or zero length takes the size of the existing file when there is one.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The save reply, or 0xD6 "Not found" when the name does not resolve.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<filename> [load] [exec] [length]". exec defaults to load; a zero/absent
        // length is filled from the existing file's size when it already exists.
        string[] parts = call.Argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string filename = parts.Length > 0 ? AcornPath.StripQuotes(parts[0]) : string.Empty;
        uint load = parts.Length > 1 && uint.TryParse(parts[1], out uint parsedLoad) ? parsedLoad : 0;
        uint exec = parts.Length > 2 && uint.TryParse(parts[2], out uint parsedExec) ? parsedExec : load;
        int length = parts.Length > 3 && int.TryParse(parts[3], out int parsedLength) ? parsedLength : 0;

        if (!call.Context.Paths.TryResolve(call.Session!, filename, out string unixPath, out _, out _))
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

        if (length == 0 && call.Context.FileSystem.FileExists(unixPath))
        {
            RetroIO.Server.Core.FileSystem.BackendFileInfo? info = call.Context.FileSystem.GetFileInfo(unixPath);
            length = (int)(info?.Length ?? 0);
        }

        return FileServerReply.SaveOsCli(load, exec, length, filename);
    }
}
