//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading;

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

    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);
    public long FilesTransferred => Interlocked.Read(ref _filesTransferred);
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
