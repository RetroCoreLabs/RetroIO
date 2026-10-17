//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// A log of recent commands and file transfers.
/// </summary>
public interface IEconetActivityLog
{
    /// <summary>
    /// Occurs after a command or a file transfer has been recorded.
    /// </summary>
    event EventHandler? ActivityRecorded;

    /// <summary>
    /// Adds a command to the log.
    /// </summary>
    /// <param name="entry">
    /// The entry to add.
    /// </param>
    void RecordCommand(EconetCommandEntry entry);

    /// <summary>
    /// Adds a file transfer to the log.
    /// </summary>
    /// <param name="entry">
    /// The entry to add.
    /// </param>
    void RecordFileTransfer(EconetFileTransferEntry entry);

    /// <summary>
    /// Gets the most recent commands.
    /// </summary>
    /// <param name="count">
    /// The largest number of entries to return.
    /// </param>
    /// <returns>
    /// The entries, newest first.
    /// </returns>
    IReadOnlyList<EconetCommandEntry> GetRecentCommands(int count);

    /// <summary>
    /// Gets the most recent file transfers.
    /// </summary>
    /// <param name="count">
    /// The largest number of entries to return.
    /// </param>
    /// <returns>
    /// The entries, newest first.
    /// </returns>
    IReadOnlyList<EconetFileTransferEntry> GetRecentTransfers(int count);
}
