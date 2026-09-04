//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public string Name => "SAVE";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 2;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
            System.IO.FileInfo? info = call.Context.FileSystem.GetFileInfo(unixPath);
            length = (int)(info?.Length ?? 0);
        }

        return FileServerReply.SaveOsCli(load, exec, length, filename);
    }
}
