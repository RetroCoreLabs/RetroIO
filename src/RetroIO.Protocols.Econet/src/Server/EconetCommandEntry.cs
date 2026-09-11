//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Server;

public sealed class EconetCommandEntry
{
    public DateTime TimestampUtc { get; init; }
    public byte ClientNet { get; init; }
    public byte ClientStation { get; init; }
    public string Username { get; init; } = string.Empty;
    public byte Opcode { get; init; }
    public string Command { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Result { get; init; } = "OK";

    public string Station => $"{ClientNet}.{ClientStation}";
}
