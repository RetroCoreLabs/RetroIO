//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Bulk;

/// <summary>
/// One packet queued for sending during a bulk transfer.
/// </summary>
public sealed class BulkPacket
{
    /// <summary>
    /// Gets the Econet port the packet is sent to.
    /// </summary>
    public required byte EconetPort { get; init; }
    /// <summary>
    /// Gets the control byte of the packet.
    /// </summary>
    public required byte Control { get; init; }
    /// <summary>
    /// Gets the sequence number of the packet.
    /// </summary>
    public required uint Sequence { get; init; }
    /// <summary>
    /// Gets the data of the packet.
    /// </summary>
    public required byte[] Payload { get; init; }
}

/// <summary>
/// State of a file load in progress from the server to a client station.
/// </summary>
public sealed class OutboundLoadTransfer
{
    /// <summary>
    /// Gets the network number of the receiving station.
    /// </summary>
    public required byte ClientNet { get; init; }
    /// <summary>
    /// Gets the station number of the receiving station.
    /// </summary>
    public required byte ClientStation { get; init; }
    /// <summary>
    /// Gets the Econet port the file data is sent to.
    /// </summary>
    public required byte DataPort { get; init; }
    /// <summary>
    /// Gets the Econet port the final reply is sent to.
    /// </summary>
    public required byte ReplyPort { get; init; }
    /// <summary>
    /// Gets or sets the sequence number of the request that started the transfer.
    /// </summary>
    public required uint TriggerSequence { get; set; }
    /// <summary>
    /// Gets the packets that are still to be sent, in sending order.
    /// </summary>
    public required Queue<BulkPacket> Queue { get; init; }
    /// <summary>
    /// Gets the file name reported in the activity log for this transfer.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;
    /// <summary>
    /// Gets the number of file bytes in the transfer.
    /// </summary>
    public long TotalBytes { get; init; }
    /// <summary>
    /// Gets the name of the user who requested the load.
    /// </summary>
    public string Username { get; init; } = string.Empty;
}

/// <summary>
/// State of a file save in progress from a client station to the server.
/// </summary>
public sealed class InboundSaveTransfer
{
    /// <summary>
    /// Gets the network number of the sending station.
    /// </summary>
    public required byte ClientNet { get; init; }
    /// <summary>
    /// Gets the station number of the sending station.
    /// </summary>
    public required byte ClientStation { get; init; }
    /// <summary>
    /// Gets the Econet port on the server that receives the file data.
    /// </summary>
    public required byte BulkPort { get; init; }
    /// <summary>
    /// Gets the Econet port the data acknowledgements are sent to.
    /// </summary>
    public required byte AckPort { get; init; }
    /// <summary>
    /// Gets the Econet port the final reply is sent to.
    /// </summary>
    public required byte ReplyPort { get; init; }
    /// <summary>
    /// Gets the open file stream that the received data is written to.
    /// </summary>
    public required FileStream Stream { get; init; }
    /// <summary>
    /// Gets or sets the number of bytes still to be received.
    /// </summary>
    public long Remaining { get; set; }
    /// <summary>
    /// Gets the total number of bytes the client announced.
    /// </summary>
    public long ExpectedBytes { get; init; }
    /// <summary>
    /// Gets the host path of the file being written.
    /// </summary>
    public string UnixPath { get; init; } = string.Empty;
    /// <summary>
    /// Gets the Acorn file name as sent by the client.
    /// </summary>
    public string AcornFilename { get; init; } = string.Empty;
    /// <summary>
    /// Gets the Acorn load address of the file.
    /// </summary>
    public uint LoadAddress { get; init; }
    /// <summary>
    /// Gets the Acorn execution address of the file.
    /// </summary>
    public uint ExecAddress { get; init; }
    /// <summary>
    /// Gets the name of the user who started the save.
    /// </summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>
    /// Gets the user number of the user who started the save, who becomes the owner of the file (fs.c line 5873).
    /// </summary>
    public ushort OwnerId { get; init; }
}

/// <summary>
/// Keeps track of the file loads and saves in progress and hands out the Econet ports for incoming data.
/// </summary>
public sealed class BulkTransferManager
{
    /// <summary>
    /// The largest number of data bytes sent in one bulk packet.
    /// </summary>
    public const int MaxChunkSize = 1280;

    private static readonly byte[] ReservedPorts = [0x99, 0x9F, 0xD1, 0xDF];

    private readonly object _sync = new();
    private readonly Dictionary<ushort, OutboundLoadTransfer> _loadTransfers = new();
    private readonly InboundSaveTransfer?[] _saveTransfers = new InboundSaveTransfer[256];
    private readonly bool[] _allocatedPorts = new bool[256];

    /// <summary>
    /// Reserves the lowest free Econet port, skipping port 0 and the reserved ports.
    /// </summary>
    /// <param name="port">
    /// Receives the reserved port, or 0 when none is free.
    /// </param>
    /// <returns>
    /// True when a port was reserved, otherwise false.
    /// </returns>
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

    /// <summary>
    /// Marks an Econet port as free again.
    /// </summary>
    /// <param name="port">
    /// The port to release.
    /// </param>
    public void ReleasePort(byte port)
    {
        lock (_sync)
        {
            if (port < _allocatedPorts.Length)
                _allocatedPorts[port] = false;
        }
    }

    /// <summary>
    /// Builds the lookup key of a station.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    /// <returns>
    /// The network number in the high byte and the station number in the low byte.
    /// </returns>
    public static ushort StationKey(byte net, byte station) => (ushort)((net << 8) | station);

    /// <summary>
    /// Stores a load transfer under its station, replacing any earlier one for that station.
    /// </summary>
    /// <param name="transfer">
    /// The transfer to store.
    /// </param>
    public void RegisterLoadTransfer(OutboundLoadTransfer transfer)
    {
        lock (_sync)
            _loadTransfers[StationKey(transfer.ClientNet, transfer.ClientStation)] = transfer;
    }

    /// <summary>
    /// Looks up the load transfer of a station.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    /// <returns>
    /// The transfer, or null when the station has none.
    /// </returns>
    public OutboundLoadTransfer? GetLoadTransfer(byte net, byte station)
    {
        lock (_sync)
        {
            _loadTransfers.TryGetValue(StationKey(net, station), out OutboundLoadTransfer? transfer);
            return transfer;
        }
    }

    /// <summary>
    /// Takes the next packet from the load transfer of a station.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    /// <param name="ackSequence">
    /// The sequence number of the acknowledgement. Not used.
    /// </param>
    /// <param name="completedTransfer">
    /// Receives the transfer when the packet taken was its last one, otherwise null.
    /// </param>
    /// <returns>
    /// The next packet, or null when the station has no transfer or its queue is empty. An empty transfer is removed.
    /// </returns>
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

    /// <summary>
    /// Removes the load transfer of a station.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    public void AbortLoad(byte net, byte station)
    {
        lock (_sync)
            _loadTransfers.Remove(StationKey(net, station));
    }

    /// <summary>
    /// Stores a save transfer under its bulk port and marks that port as in use.
    /// </summary>
    /// <param name="transfer">
    /// The transfer to store.
    /// </param>
    public void RegisterSaveTransfer(InboundSaveTransfer transfer)
    {
        lock (_sync)
        {
            _saveTransfers[transfer.BulkPort] = transfer;
            _allocatedPorts[transfer.BulkPort] = true;
        }
    }

    /// <summary>
    /// Looks up the save transfer that receives data on a port.
    /// </summary>
    /// <param name="bulkPort">
    /// The Econet port the data arrives on.
    /// </param>
    /// <returns>
    /// The transfer, or null when the port has none.
    /// </returns>
    public InboundSaveTransfer? GetSaveTransfer(byte bulkPort)
    {
        lock (_sync)
            return _saveTransfers[bulkPort];
    }

    /// <summary>
    /// Ends the save transfer on a port: closes its file stream, removes it and frees the port.
    /// </summary>
    /// <param name="bulkPort">
    /// The Econet port the data arrived on.
    /// </param>
    /// <returns>
    /// The transfer that was removed, or null when the port had none.
    /// </returns>
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

    /// <summary>
    /// Drops all transfers of a station: removes its load transfer and closes and removes its save transfers.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
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
