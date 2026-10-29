//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// Live counters of a DriveWire server, shared between the server (which counts) and the management API (which reports).
/// </summary>
/// <remarks>
/// These are totals since the server started. A RESET transaction from a client sets the per-drive counters in
/// <see cref="Drives.DriveWireDriveTable"/> back to zero, as the specification asks; it does not touch these totals.
/// </remarks>
public sealed class DriveWireServerStatistics
{
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private long _transactions;
    private long _sectorsRead;
    private long _sectorsWritten;
    private long _readErrors;
    private long _writeErrors;
    private long _checksumErrors;
    private long _rereads;
    private long _rewrites;
    private long _timeouts;
    private long _unknownOpCodes;
    private long _resets;
    private long _connections;
    private long _activeConnections;
    private long _bytesReceived;
    private long _bytesSent;

    /// <summary>
    /// Gets the transactions handled, of every kind.
    /// </summary>
    public long Transactions => Interlocked.Read(ref _transactions);

    /// <summary>
    /// Gets the sectors sent without error.
    /// </summary>
    public long SectorsRead => Interlocked.Read(ref _sectorsRead);

    /// <summary>
    /// Gets the sectors written without error.
    /// </summary>
    public long SectorsWritten => Interlocked.Read(ref _sectorsWritten);

    /// <summary>
    /// Gets the reads answered with an error code other than E$CRC.
    /// </summary>
    public long ReadErrors => Interlocked.Read(ref _readErrors);

    /// <summary>
    /// Gets the writes answered with an error code other than E$CRC.
    /// </summary>
    public long WriteErrors => Interlocked.Read(ref _writeErrors);

    /// <summary>
    /// Gets the reads and writes answered with E$CRC because the checksums did not match.
    /// </summary>
    public long ChecksumErrors => Interlocked.Read(ref _checksumErrors);

    /// <summary>
    /// Gets the reread transactions (OP_REREAD, OP_REREADEX).
    /// </summary>
    public long Rereads => Interlocked.Read(ref _rereads);

    /// <summary>
    /// Gets the rewrite transactions (OP_REWRITE).
    /// </summary>
    public long Rewrites => Interlocked.Read(ref _rewrites);

    /// <summary>
    /// Gets the transactions given up because the client stopped sending in the middle.
    /// </summary>
    public long Timeouts => Interlocked.Read(ref _timeouts);

    /// <summary>
    /// Gets the operation codes this server does not know.
    /// </summary>
    public long UnknownOpCodes => Interlocked.Read(ref _unknownOpCodes);

    /// <summary>
    /// Gets the RESET transactions.
    /// </summary>
    public long Resets => Interlocked.Read(ref _resets);

    /// <summary>
    /// Gets the client connections accepted since the start.
    /// </summary>
    public long Connections => Interlocked.Read(ref _connections);

    /// <summary>
    /// Gets the client connections open now.
    /// </summary>
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>
    /// Gets the bytes received from clients.
    /// </summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Gets the bytes sent to clients.
    /// </summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>
    /// Gets the time since these counters were created.
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startedAt;

    internal void RecordTransaction() => Interlocked.Increment(ref _transactions);

    internal void RecordSectorRead() => Interlocked.Increment(ref _sectorsRead);

    internal void RecordSectorWritten() => Interlocked.Increment(ref _sectorsWritten);

    internal void RecordReadError() => Interlocked.Increment(ref _readErrors);

    internal void RecordWriteError() => Interlocked.Increment(ref _writeErrors);

    internal void RecordChecksumError() => Interlocked.Increment(ref _checksumErrors);

    internal void RecordReread() => Interlocked.Increment(ref _rereads);

    internal void RecordRewrite() => Interlocked.Increment(ref _rewrites);

    internal void RecordTimeout() => Interlocked.Increment(ref _timeouts);

    internal void RecordUnknownOpCode() => Interlocked.Increment(ref _unknownOpCodes);

    internal void RecordReset() => Interlocked.Increment(ref _resets);

    internal void RecordConnectionOpened()
    {
        Interlocked.Increment(ref _connections);
        Interlocked.Increment(ref _activeConnections);
    }

    internal void RecordConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

    internal void RecordBytesReceived(long count) => Interlocked.Add(ref _bytesReceived, count);

    internal void RecordBytesSent(long count) => Interlocked.Add(ref _bytesSent, count);
}
