//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading;

using RetroIO.Telemetry;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// Real, live traffic counters for the TNFS server, backing the Management API's
/// GetStatistics endpoint (previously hardcoded to all-zero placeholder values).
/// One instance is shared as a DI singleton between TnfsServerService (which increments the
/// counters on every request) and TnfsController (which reads them for reporting).
/// </summary>
public sealed class TnfsServerStatistics
{
    private long _totalRequests;
    private long _bytesTransferred;
    private long _filesTransferred;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>
    /// Initialises the counters and their metrics in the shared RetroServer shape.
    /// </summary>
    public TnfsServerStatistics()
    {
        Telemetry = new RetroTelemetry(TelemetryName);
        Telemetry.AddCounter("retroio.tnfs.requests", "{request}", "Requests handled.", () => TotalRequests);
        Telemetry.AddCounter("retroio.tnfs.io", "By", "Bytes in requests and replies together.", () => BytesTransferred);
        Telemetry.AddCounter("retroio.tnfs.files.opened", "{file}", "Files opened for transfer.", () => FilesTransferred);
        Telemetry.AddGauge("retroio.tnfs.uptime", "s", "Seconds since the server started.", () => (long)Uptime.TotalSeconds);
    }

    /// <summary>
    /// The name of the TNFS server in metrics and logs, and its meter name for OpenTelemetry.
    /// </summary>
    public const string TelemetryName = "RetroIO.Tnfs";

    /// <summary>
    /// Gets the metrics of the server, in the shape every RetroServer part uses. Its log does not go
    /// through this yet; it still goes only to the server logger (see the plan).
    /// </summary>
    public RetroTelemetry Telemetry { get; }

    /// <summary>
    /// Records one handled request and the total bytes involved (request + response payload).
    /// Thread-safe — TnfsServerService's message handler can run concurrently across transports.
    /// </summary>
    public void RecordRequest(long bytesTransferred)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _bytesTransferred, bytesTransferred);
    }

    /// <summary>
    /// Records one file successfully opened for transfer (TNFS_OPEN). This is the interpretation
    /// of "files transferred" used here — a count of files opened, not a count of individual
    /// READ/WRITE calls (those are already covered by TotalRequests).
    /// </summary>
    public void RecordFileTransfer()
    {
        Interlocked.Increment(ref _filesTransferred);
    }

    /// <summary>
    /// Gets the number of requests recorded.
    /// </summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    /// <summary>
    /// Gets the total bytes recorded for all requests.
    /// </summary>
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);
    /// <summary>
    /// Gets the number of files recorded as opened for transfer.
    /// </summary>
    public long FilesTransferred => Interlocked.Read(ref _filesTransferred);
    /// <summary>
    /// Gets the time since the statistics object was created.
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startedAt;

    /// <summary>
    /// Average requests/sec since the server started. 0 immediately after startup, not NaN/Inf.
    /// </summary>
    public double RequestsPerSecond
    {
        get
        {
            double seconds = Uptime.TotalSeconds;
            return seconds > 0 ? TotalRequests / seconds : 0;
        }
    }

    /// <summary>
    /// Average MB/sec since the server started. 0 immediately after startup, not NaN/Inf.
    /// </summary>
    public double MbPerSecond
    {
        get
        {
            double seconds = Uptime.TotalSeconds;
            return seconds > 0 ? (BytesTransferred / (1024.0 * 1024.0)) / seconds : 0;
        }
    }
}
