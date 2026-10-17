//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

public enum EconetFileTransferDirection
{
    Send,
    Receive,
}

public sealed class EconetFileTransferEntry
{
    public DateTime TimestampUtc { get; init; }
    public byte ClientNet { get; init; }
    public byte ClientStation { get; init; }
    public string Username { get; init; } = string.Empty;
    public EconetFileTransferDirection Direction { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public bool Success { get; init; }
    public string Status { get; init; } = "OK";

    public string Station => $"{ClientNet}.{ClientStation}";

    public string DirectionLabel => Direction == EconetFileTransferDirection.Send ? "Send" : "Receive";
}
