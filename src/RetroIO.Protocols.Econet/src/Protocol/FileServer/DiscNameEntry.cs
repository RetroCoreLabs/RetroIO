//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

public readonly struct DiscNameEntry
{
    public DiscNameEntry(byte index, string name)
    {
        Index = index;
        Name = name;
    }

    public byte Index { get; }
    public string Name { get; }
}
