//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using RetroIO.Storage.Utilities;

namespace RetroIO.Tools.TnfsStress;

/// <summary>
/// Thread-safe statistics tracker for TNFS stress test
/// </summary>
public sealed class Statistics
{
    private const int BoxWidth = 74;

    private long _totalOperations;
    private long _totalErrors;
    private long _totalBytesUploaded;
    private long _totalBytesDownloaded;
    private long _totalFilesCreated;
    private long _totalFilesDeleted;
    private long _activeThreads;
    private readonly DateTime _startTime;

    public Statistics()
    {
        _startTime = DateTime.UtcNow;
    }

    public void IncrementOperations() => Interlocked.Increment(ref _totalOperations);
    public void IncrementErrors() => Interlocked.Increment(ref _totalErrors);
    public void IncrementActiveThreads() => Interlocked.Increment(ref _activeThreads);
    public void DecrementActiveThreads() => Interlocked.Decrement(ref _activeThreads);

    public void AddBytesUploaded(long bytes) => Interlocked.Add(ref _totalBytesUploaded, bytes);
    public void AddBytesDownloaded(long bytes) => Interlocked.Add(ref _totalBytesDownloaded, bytes);

    public void IncrementFilesCreated() => Interlocked.Increment(ref _totalFilesCreated);
    public void IncrementFilesDeleted() => Interlocked.Increment(ref _totalFilesDeleted);

    public long TotalOperations => Interlocked.Read(ref _totalOperations);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public long TotalBytesUploaded => Interlocked.Read(ref _totalBytesUploaded);
    public long TotalBytesDownloaded => Interlocked.Read(ref _totalBytesDownloaded);
    public long TotalFilesCreated => Interlocked.Read(ref _totalFilesCreated);
    public long TotalFilesDeleted => Interlocked.Read(ref _totalFilesDeleted);
    public long ActiveThreads => Interlocked.Read(ref _activeThreads);

    public TimeSpan Elapsed => DateTime.UtcNow - _startTime;

    public void DisplayStats()
    {
        var elapsed = Elapsed.TotalSeconds;
        var opsPerSec = elapsed > 0 ? TotalOperations / elapsed : 0;
        var mbUploaded = TotalBytesUploaded / (1024.0 * 1024.0);
        var mbDownloaded = TotalBytesDownloaded / (1024.0 * 1024.0);
        var uploadMbps = elapsed > 0 ? mbUploaded / elapsed : 0;
        var downloadMbps = elapsed > 0 ? mbDownloaded / elapsed : 0;

        ConsoleBox.Top(BoxWidth);
        ConsoleBox.Title("TNFS STRESS TEST STATISTICS", BoxWidth);
        ConsoleBox.Divider(BoxWidth);
        ConsoleBox.LabelValue("  Active Threads:      ", ActiveThreads, BoxWidth);
        ConsoleBox.Line($"  Total Operations:    {TotalOperations,10}  ({opsPerSec,8:F2} ops/sec)", BoxWidth);
        ConsoleBox.LabelValue("  Total Errors:        ", TotalErrors, BoxWidth);
        ConsoleBox.LabelValue("  Files Created:       ", TotalFilesCreated, BoxWidth);
        ConsoleBox.LabelValue("  Files Deleted:       ", TotalFilesDeleted, BoxWidth);
        ConsoleBox.Line($"  Data Uploaded:       {mbUploaded,10:F2} MB  ({uploadMbps,6:F2} MB/sec)", BoxWidth);
        ConsoleBox.Line($"  Data Downloaded:     {mbDownloaded,10:F2} MB  ({downloadMbps,6:F2} MB/sec)", BoxWidth);
        ConsoleBox.LabelValue("  Elapsed Time:        ", $"{elapsed,10:F1} sec", BoxWidth);
        ConsoleBox.Bottom(BoxWidth);
    }
}
