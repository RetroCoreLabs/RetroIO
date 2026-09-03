//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Bulk;

public sealed class BulkPacket
{
    public required byte EconetPort { get; init; }
    public required byte Control { get; init; }
    public required uint Sequence { get; init; }
    public required byte[] Payload { get; init; }
}

public sealed class OutboundLoadTransfer
{
    public required byte ClientNet { get; init; }
    public required byte ClientStation { get; init; }
    public required byte DataPort { get; init; }
    public required byte ReplyPort { get; init; }
    public required uint TriggerSequence { get; set; }
    public required Queue<BulkPacket> Queue { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public string Username { get; init; } = string.Empty;
}

public sealed class InboundSaveTransfer
{
    public required byte ClientNet { get; init; }
    public required byte ClientStation { get; init; }
    public required byte BulkPort { get; init; }
    public required byte AckPort { get; init; }
    public required byte ReplyPort { get; init; }
    public required FileStream Stream { get; init; }
    public long Remaining { get; set; }
    public long ExpectedBytes { get; init; }
    public string UnixPath { get; init; } = string.Empty;
    public string AcornFilename { get; init; } = string.Empty;
    public uint LoadAddress { get; init; }
    public uint ExecAddress { get; init; }
    public string Username { get; init; } = string.Empty;
}

public sealed class BulkTransferManager
{
    public const int MaxChunkSize = 1280;

    private static readonly byte[] ReservedPorts = [0x99, 0x9F, 0xD1, 0xDF];

    private readonly object _sync = new();
    private readonly Dictionary<ushort, OutboundLoadTransfer> _loadTransfers = new();
    private readonly InboundSaveTransfer?[] _saveTransfers = new InboundSaveTransfer[256];
    private readonly bool[] _allocatedPorts = new bool[256];

    public bool TryAllocatePort(out byte port)
    {
        lock (_sync)
        {
            for (int candidate = 1; candidate < _allocatedPorts.Length; candidate++)
            {
                if (IsReserved((byte)candidate))
                    continue;

                if (_allocatedPorts[candidate])
                    continue;

                _allocatedPorts[candidate] = true;
                port = (byte)candidate;
                return true;
            }
        }

        port = 0;
        return false;
    }

    public void ReleasePort(byte port)
    {
        lock (_sync)
        {
            if (port < _allocatedPorts.Length)
                _allocatedPorts[port] = false;
        }
    }

    public static ushort StationKey(byte net, byte station) => (ushort)((net << 8) | station);

    public void RegisterLoadTransfer(OutboundLoadTransfer transfer)
    {
        lock (_sync)
            _loadTransfers[StationKey(transfer.ClientNet, transfer.ClientStation)] = transfer;
    }

    public OutboundLoadTransfer? GetLoadTransfer(byte net, byte station)
    {
        lock (_sync)
        {
            _loadTransfers.TryGetValue(StationKey(net, station), out OutboundLoadTransfer? transfer);
            return transfer;
        }
    }

    public BulkPacket? DequeueLoadPacket(byte net, byte station, uint ackSequence, out OutboundLoadTransfer? completedTransfer)
    {
        completedTransfer = null;
        lock (_sync)
        {
            ushort key = StationKey(net, station);
            if (!_loadTransfers.TryGetValue(key, out OutboundLoadTransfer? transfer))
                return null;

            if (transfer.Queue.Count == 0)
            {
                _loadTransfers.Remove(key);
                return null;
            }

            BulkPacket packet = transfer.Queue.Dequeue();
            if (transfer.Queue.Count == 0)
            {
                completedTransfer = transfer;
                _loadTransfers.Remove(key);
            }

            return packet;
        }
    }

    public void AbortLoad(byte net, byte station)
    {
        lock (_sync)
            _loadTransfers.Remove(StationKey(net, station));
    }

    public void RegisterSaveTransfer(InboundSaveTransfer transfer)
    {
        lock (_sync)
        {
            _saveTransfers[transfer.BulkPort] = transfer;
            _allocatedPorts[transfer.BulkPort] = true;
        }
    }

    public InboundSaveTransfer? GetSaveTransfer(byte bulkPort)
    {
        lock (_sync)
            return _saveTransfers[bulkPort];
    }

    public InboundSaveTransfer? CompleteSave(byte bulkPort)
    {
        lock (_sync)
        {
            InboundSaveTransfer? transfer = _saveTransfers[bulkPort];
            if (transfer != null)
            {
                transfer.Stream.Dispose();
                _saveTransfers[bulkPort] = null;
                ReleasePort(bulkPort);
            }

            return transfer;
        }
    }

    public void ReleaseStation(byte net, byte station)
    {
        lock (_sync)
        {
            _loadTransfers.Remove(StationKey(net, station));
            for (int port = 0; port < _saveTransfers.Length; port++)
            {
                InboundSaveTransfer? transfer = _saveTransfers[port];
                if (transfer != null && transfer.ClientNet == net && transfer.ClientStation == station)
                {
                    transfer.Stream.Dispose();
                    _saveTransfers[port] = null;
                    ReleasePort((byte)port);
                }
            }
        }
    }

    private static bool IsReserved(byte port)
    {
        for (int i = 0; i < ReservedPorts.Length; i++)
        {
            if (ReservedPorts[i] == port)
                return true;
        }

        return false;
    }
}
