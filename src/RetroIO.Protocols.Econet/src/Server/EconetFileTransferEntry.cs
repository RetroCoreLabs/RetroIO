//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// The direction of a file transfer, seen from the server.
/// </summary>
public enum EconetFileTransferDirection
{
    /// <summary>
    /// The server sends the file to the client.
    /// </summary>
    Send,
    /// <summary>
    /// The server receives the file from the client.
    /// </summary>
    Receive,
}

/// <summary>
/// One file transfer recorded in the activity log.
/// </summary>
public sealed class EconetFileTransferEntry
{
    /// <summary>
    /// Gets the time the transfer was recorded, in UTC.
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
    /// Gets the direction of the transfer.
    /// </summary>
    public EconetFileTransferDirection Direction { get; init; }
    /// <summary>
    /// Gets the file name or path.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;
    /// <summary>
    /// Gets the number of bytes transferred.
    /// </summary>
    public long Bytes { get; init; }
    /// <summary>
    /// Gets a value indicating whether the transfer finished correctly.
    /// </summary>
    public bool Success { get; init; }
    /// <summary>
    /// Gets a short status text. The default is OK.
    /// </summary>
    public string Status { get; init; } = "OK";

    /// <summary>
    /// Gets the client address as the network number, a dot and the station number.
    /// </summary>
    public string Station => $"{ClientNet}.{ClientStation}";

    /// <summary>
    /// Gets the direction as the text Send or Receive.
    /// </summary>
    public string DirectionLabel => Direction == EconetFileTransferDirection.Send ? "Send" : "Receive";
}
