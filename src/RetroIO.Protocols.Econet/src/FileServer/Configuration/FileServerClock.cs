//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Mutable server clock used by ReadTime / SetRtc.
/// </summary>
public sealed class FileServerClock
{
    private DateTime _utc = DateTime.UtcNow;

    public DateTime UtcNow
    {
        get => _utc;
    }

    public void SetUtc(DateTime utc)
    {
        _utc = utc;
    }
}
