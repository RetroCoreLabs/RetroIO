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
    /// <summary>
    /// Gets the full command name in upper case: <c>LOAD</c>.
    /// </summary>
    public string Name => "LOAD";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 2, so <c>*LO.</c> selects
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
    /// Checks that the file named by the argument exists and returns the load reply for the ROM to fetch
    /// it.
    /// </summary>
    /// <remarks>
    /// An optional second word that parses as a decimal number is sent back as the load address, with
    /// the address-supplied flag set.
    /// </remarks>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The load reply, or 0xD6 "Not found" when the file does not resolve or does not exist.
    /// </returns>
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
