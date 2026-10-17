//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// Counters for the TNFS server.
/// </summary>
public class TnfsStatisticsResponse
{
    /// <summary>
    /// Gets or sets the number of connections that are open now.
    /// </summary>
    public int ActiveConnections { get; set; }
    /// <summary>
    /// Gets or sets the total number of requests handled.
    /// </summary>
    public long TotalRequests { get; set; }
    /// <summary>
    /// Gets or sets the total number of bytes transferred.
    /// </summary>
    public long BytesTransferred { get; set; }
    /// <summary>
    /// Gets or sets the total number of files transferred.
    /// </summary>
    public long FilesTransferred { get; set; }
    /// <summary>
    /// Gets or sets the server uptime as text.
    /// </summary>
    public string Uptime { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the request rate in requests per second.
    /// </summary>
    public double RequestsPerSecond { get; set; }
    /// <summary>
    /// Gets or sets the transfer rate in megabytes per second.
    /// </summary>
    public double MbPerSecond { get; set; }
}

/// <summary>
/// A list of TNFS sessions.
/// </summary>
public class TnfsSessionsResponse
{
    /// <summary>
    /// Gets or sets the sessions.
    /// </summary>
    public List<TnfsSessionInfo> Sessions { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of sessions.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One TNFS client session.
/// </summary>
public class TnfsSessionInfo
{
    /// <summary>
    /// Gets or sets the session identifier.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the client IP address as text.
    /// </summary>
    public string ClientIP { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the user name of the session.
    /// </summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the transport the session uses, as text.
    /// </summary>
    public string Transport { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets when the session connected.
    /// </summary>
    public DateTime ConnectedAt { get; set; }
    /// <summary>
    /// Gets or sets when the session was last active.
    /// </summary>
    public DateTime LastActivity { get; set; }
    /// <summary>
    /// Gets or sets the number of files the session has open.
    /// </summary>
    public int OpenFiles { get; set; }
    /// <summary>
    /// Gets or sets the number of directories the session has open.
    /// </summary>
    public int OpenDirectories { get; set; }
}

/// <summary>
/// Statistics history for the TNFS server.
/// </summary>
public class TnfsHistoryResponse
{
    /// <summary>
    /// Gets or sets the time range covered, as text.
    /// </summary>
    public string TimeRange { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the interval between data points, as text.
    /// </summary>
    public string Interval { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the data points.
    /// </summary>
    public List<TnfsDataPoint> DataPoints { get; set; } = new();
}

/// <summary>
/// One point in the TNFS statistics history.
/// </summary>
public class TnfsDataPoint
{
    /// <summary>
    /// Gets or sets the time of the data point.
    /// </summary>
    public DateTime Timestamp { get; set; }
    /// <summary>
    /// Gets or sets the number of requests.
    /// </summary>
    public long Requests { get; set; }
    /// <summary>
    /// Gets or sets the amount of data transferred, in megabytes.
    /// </summary>
    public double BytesMB { get; set; }
    /// <summary>
    /// Gets or sets the number of files transferred.
    /// </summary>
    public long Files { get; set; }
}
