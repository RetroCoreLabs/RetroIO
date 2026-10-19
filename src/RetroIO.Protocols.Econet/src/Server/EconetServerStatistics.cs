//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Running totals of the Econet file server, counted by <see cref="EconetFileServerService"/>
/// as it handles frames.
/// </summary>
/// <remarks>
/// One instance is shared as a singleton between the service, which increments the counters,
/// and the Management API, which reads them. Every counter is a real count of an event that
/// happened. Nothing here is sampled or estimated.
/// Thread-safe: the service can handle frames from several transports at the same time.
/// </remarks>
public sealed class EconetServerStatistics : IEconetStatisticsTracker
{
    private long _totalRequests;
    private long _bytesReceived;
    private long _bytesSent;
    private long _fileOperations;
    private long _errors;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>
    /// Records one handled request.
    /// </summary>
    /// <param name="bytesReceived">
    /// The number of payload bytes in the request.
    /// </param>
    /// <param name="bytesSent">
    /// The number of payload bytes in the reply that was sent, or 0 when no reply was sent.
    /// </param>
    /// <param name="isFileOperation">
    /// <c>true</c> when the request was a file operation.
    /// </param>
    public void RecordRequest(int bytesReceived, int bytesSent, bool isFileOperation)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _bytesReceived, bytesReceived);
        Interlocked.Add(ref _bytesSent, bytesSent);
        if (isFileOperation)
            Interlocked.Increment(ref _fileOperations);
    }

    /// <summary>
    /// Records one failure: a request that threw while it was handled, or a reply that could not be sent.
    /// </summary>
    public void RecordError()
    {
        Interlocked.Increment(ref _errors);
    }

    /// <summary>
    /// Gets the number of requests handled.
    /// </summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>
    /// Gets the number of payload bytes received in requests.
    /// </summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Gets the number of payload bytes sent in replies.
    /// </summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>
    /// Gets the bytes received plus the bytes sent.
    /// </summary>
    public long BytesTransferred => BytesReceived + BytesSent;

    /// <summary>
    /// Gets the number of requests that were file operations.
    /// </summary>
    public long FileOperations => Interlocked.Read(ref _fileOperations);

    /// <summary>
    /// Gets the number of failures recorded.
    /// </summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>
    /// Gets the time since this object was created, which is when the server was set up.
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startedAt;
}
