//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Fixed-size in-memory ring buffer for recent Econet commands and file transfers.
/// </summary>
public sealed class EconetActivityLog : IEconetActivityLog
{
    /// <summary>
    /// The default number of entries kept for commands and for transfers, 100.
    /// </summary>
    public const int DefaultCapacity = 100;

    private readonly object _sync = new();
    private readonly EconetCommandEntry[] _commands;
    private readonly EconetFileTransferEntry[] _transfers;
    private int _commandHead;
    private int _commandCount;
    private int _transferHead;
    private int _transferCount;

    /// <summary>
    /// Creates the log.
    /// </summary>
    /// <param name="capacity">
    /// The number of entries kept for commands and, separately, for transfers.
    /// </param>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is less than 1.
    /// </exception>
    public EconetActivityLog(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
        _commands = new EconetCommandEntry[capacity];
        _transfers = new EconetFileTransferEntry[capacity];
    }

    /// <summary>
    /// Gets the number of entries kept for commands and, separately, for transfers.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// Occurs after a command or a file transfer has been recorded. It is raised outside the lock.
    /// </summary>
    public event EventHandler? ActivityRecorded;

    /// <summary>
    /// Adds a command to the log, overwriting the oldest one when the log is full, then raises
    /// <see cref="ActivityRecorded"/>.
    /// </summary>
    /// <param name="entry">
    /// The entry to add.
    /// </param>
    public void RecordCommand(EconetCommandEntry entry)
    {
        lock (_sync)
        {
            _commands[_commandHead] = entry;
            _commandHead = (_commandHead + 1) % Capacity;
            if (_commandCount < Capacity)
                _commandCount++;
        }

        ActivityRecorded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds a file transfer to the log, overwriting the oldest one when the log is full, then raises
    /// <see cref="ActivityRecorded"/>.
    /// </summary>
    /// <param name="entry">
    /// The entry to add.
    /// </param>
    public void RecordFileTransfer(EconetFileTransferEntry entry)
    {
        lock (_sync)
        {
            _transfers[_transferHead] = entry;
            _transferHead = (_transferHead + 1) % Capacity;
            if (_transferCount < Capacity)
                _transferCount++;
        }

        ActivityRecorded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets the most recent commands.
    /// </summary>
    /// <param name="count">
    /// The largest number of entries to return.
    /// </param>
    /// <returns>
    /// A new array of at most <paramref name="count"/> entries, newest first. A <paramref name="count"/>
    /// of 0 or less gives an empty array.
    /// </returns>
    public IReadOnlyList<EconetCommandEntry> GetRecentCommands(int count)
    {
        lock (_sync)
            return CopyRecent(_commands, _commandHead, _commandCount, count);
    }

    /// <summary>
    /// Gets the most recent file transfers.
    /// </summary>
    /// <param name="count">
    /// The largest number of entries to return.
    /// </param>
    /// <returns>
    /// A new array of at most <paramref name="count"/> entries, newest first. A <paramref name="count"/>
    /// of 0 or less gives an empty array.
    /// </returns>
    public IReadOnlyList<EconetFileTransferEntry> GetRecentTransfers(int count)
    {
        lock (_sync)
            return CopyRecent(_transfers, _transferHead, _transferCount, count);
    }

    private static T[] CopyRecent<T>(T[] buffer, int head, int count, int requested)
    {
        // A negative request would make the array size negative (OverflowException); treat it as 0.
        int take = Math.Max(0, Math.Min(requested, count));
        T[] result = new T[take];
        if (take == 0)
            return result;

        int capacity = buffer.Length;
        int start = (head - take + capacity) % capacity;
        for (int i = 0; i < take; i++)
            result[i] = buffer[(start + i) % capacity]!;

        Array.Reverse(result);
        return result;
    }
}
