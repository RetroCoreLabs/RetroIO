//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*LOAD</c> — resolves a file and returns its load metadata for the ROM to fetch.
/// </summary>
public sealed class LoadCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "LOAD";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 2;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<filename> [load-address]". The optional load address, when a valid number,
        // is echoed back with the "address supplied" flag set.
        string argument = call.Argument;
        int spaceIndex = argument.IndexOf(' ');
        string filename = spaceIndex >= 0 ? argument.Substring(0, spaceIndex) : argument;
        filename = AcornPath.StripQuotes(filename);

        uint loadAddress = 0;
        bool loadFound = false;
        if (spaceIndex >= 0 && uint.TryParse(argument.Substring(spaceIndex + 1).Trim(), out uint parsed))
        {
            loadAddress = parsed;
            loadFound = true;
        }

        if (!call.Context.Paths.TryResolve(call.Session!, filename, out string unixPath, out _, out _) ||
            !call.Context.FileSystem.FileExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        return FileServerReply.LoadOsCli(loadAddress, loadFound, filename);
    }
}
