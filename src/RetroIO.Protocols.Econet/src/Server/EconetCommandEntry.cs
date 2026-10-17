//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// One command recorded in the activity log.
/// </summary>
public sealed class EconetCommandEntry
{
    /// <summary>
    /// Gets the time the command was recorded, in UTC.
    /// </summary>
    public DateTime TimestampUtc { get; init; }
    /// <summary>
    /// Gets the network number of the client.
    /// </summary>
    public byte ClientNet { get; init; }
    /// <summary>
    /// Gets the station number of the client.
    /// </summary>
    public byte ClientStation { get; init; }
    /// <summary>
    /// Gets the user name of the client.
    /// </summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>
    /// Gets the file server opcode of the command.
    /// </summary>
    public byte Opcode { get; init; }
    /// <summary>
    /// Gets the short label of the command.
    /// </summary>
    public string Command { get; init; } = string.Empty;
    /// <summary>
    /// Gets the trace text of the request.
    /// </summary>
    public string Detail { get; init; } = string.Empty;
    /// <summary>
    /// Gets a value indicating whether the reply was judged to be a success.
    /// </summary>
    public bool Success { get; init; }
    /// <summary>
    /// Gets the trace text of the reply. The default is OK.
    /// </summary>
    public string Result { get; init; } = "OK";

    /// <summary>
    /// Gets the client address as the network number, a dot and the station number.
    /// </summary>
    public string Station => $"{ClientNet}.{ClientStation}";
}
