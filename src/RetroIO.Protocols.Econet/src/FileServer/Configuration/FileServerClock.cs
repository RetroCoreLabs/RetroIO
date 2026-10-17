//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Mutable server clock used by ReadTime / SetRtc.
/// </summary>
public sealed class FileServerClock
{
    private DateTime _utc = DateTime.UtcNow;

    /// <summary>
    /// Gets the current server time in UTC. It is the value last set with SetUtc, or the creation time of the clock.
    /// </summary>
    public DateTime UtcNow
    {
        get => _utc;
    }

    /// <summary>
    /// Sets the time the clock reports.
    /// </summary>
    /// <param name="utc">
    /// The new server time in UTC.
    /// </param>
    public void SetUtc(DateTime utc)
    {
        _utc = utc;
    }
}
