//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

public class TnfsStatisticsResponse
{
    public int ActiveConnections { get; set; }
    public long TotalRequests { get; set; }
    public long BytesTransferred { get; set; }
    public long FilesTransferred { get; set; }
    public string Uptime { get; set; } = string.Empty;
    public double RequestsPerSecond { get; set; }
    public double MbPerSecond { get; set; }
}

public class TnfsSessionsResponse
{
    public List<TnfsSessionInfo> Sessions { get; set; } = new();
    public int Total { get; set; }
}

public class TnfsSessionInfo
{
    public string SessionId { get; set; } = string.Empty;
    public string ClientIP { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public int OpenFiles { get; set; }
    public int OpenDirectories { get; set; }
}

public class TnfsHistoryResponse
{
    public string TimeRange { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;
    public List<TnfsDataPoint> DataPoints { get; set; } = new();
}

public class TnfsDataPoint
{
    public DateTime Timestamp { get; set; }
    public long Requests { get; set; }
    public double BytesMB { get; set; }
    public long Files { get; set; }
}
