//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// One disc index and name pair of a read disc names reply.
/// </summary>
public readonly struct DiscNameEntry
{
    /// <summary>
    /// Creates an entry.
    /// </summary>
    /// <param name="index">
    /// The disc index.
    /// </param>
    /// <param name="name">
    /// The disc name.
    /// </param>
    public DiscNameEntry(byte index, string name)
    {
        Index = index;
        Name = name;
    }

    /// <summary>
    /// Gets the disc index.
    /// </summary>
    public byte Index { get; }
    /// <summary>
    /// Gets the disc name.
    /// </summary>
    public string Name { get; }
}
