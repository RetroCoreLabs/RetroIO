//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*FSLIST</c> — reports the server version string.
/// </summary>
/// <remarks>
/// A RetroFS extra with no reference equivalent, so it is matched by full name only (no
/// abbreviation) to avoid inventing an abbreviation the reference never defined.
/// </remarks>
public sealed class FsListCommand : IOsCliCommand
{
    private readonly FileServerContext _context;

    /// <summary>
    /// Creates the command against the context whose version string it reports.
    /// </summary>
    /// <param name="context">
    /// The server context carrying the version string.
    /// </param>
    public FsListCommand(FileServerContext context)
    {
        _context = context;
    }

    /// <inheritdoc/>
    public string Name => "FSLIST";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 6;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        return FileServerReply.Version(call.Context.VersionString);
    }
}
