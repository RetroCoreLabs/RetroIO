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
    public const int DefaultCapacity = 100;

    private readonly object _sync = new();
    private readonly EconetCommandEntry[] _commands;
    private readonly EconetFileTransferEntry[] _transfers;
    private int _commandHead;
    private int _commandCount;
    private int _transferHead;
    private int _transferCount;

    public EconetActivityLog(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
        _commands = new EconetCommandEntry[capacity];
        _transfers = new EconetFileTransferEntry[capacity];
    }

    public int Capacity { get; }

    public event EventHandler? ActivityRecorded;

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

    public IReadOnlyList<EconetCommandEntry> GetRecentCommands(int count)
    {
        lock (_sync)
            return CopyRecent(_commands, _commandHead, _commandCount, count);
    }

    public IReadOnlyList<EconetFileTransferEntry> GetRecentTransfers(int count)
    {
        lock (_sync)
            return CopyRecent(_transfers, _transferHead, _transferCount, count);
    }

    private static T[] CopyRecent<T>(T[] buffer, int head, int count, int requested)
    {
        int take = Math.Min(requested, count);
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
