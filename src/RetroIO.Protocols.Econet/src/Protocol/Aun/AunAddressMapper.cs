//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using System.Net.Sockets;

namespace RetroIO.Protocols.Econet.Protocol.Aun;

/// <summary>
/// Maps AUN UDP client IPv4 addresses to Econet net.station (RISC OS convention: last octet = station).
/// </summary>
public sealed class AunAddressMapper
{
    private readonly byte _network;
    private readonly byte _stationOctetIndex;

    public AunAddressMapper(byte network, byte stationOctetIndex = 3)
    {
        _network = network;
        _stationOctetIndex = stationOctetIndex;
    }

    public byte Network => _network;

    public bool TryResolve(IPAddress address, out byte net, out byte station)
    {
        net = _network;
        station = 0;

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        station = bytes[_stationOctetIndex];
        return true;
    }
}
