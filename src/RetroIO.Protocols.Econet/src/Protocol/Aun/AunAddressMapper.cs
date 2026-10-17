//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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

    /// <summary>
    /// Creates a mapper with a fixed network number and the position of the address byte that holds the station number.
    /// </summary>
    /// <param name="network">
    /// The Econet network number given to every client.
    /// </param>
    /// <param name="stationOctetIndex">
    /// Index, 0 to 3, of the IPv4 address byte used as the station number.
    /// </param>
    public AunAddressMapper(byte network, byte stationOctetIndex = 3)
    {
        _network = network;
        _stationOctetIndex = stationOctetIndex;
    }

    /// <summary>
    /// Gets the network number given to every client.
    /// </summary>
    public byte Network => _network;

    /// <summary>
    /// Maps an IPv4 address to a network and station number.
    /// </summary>
    /// <param name="address">
    /// The client address.
    /// </param>
    /// <param name="net">
    /// Receives the network number.
    /// </param>
    /// <param name="station">
    /// Receives the station number, the address byte at the configured index, or 0 when the address is not IPv4.
    /// </param>
    /// <returns>
    /// True for an IPv4 address, otherwise false.
    /// </returns>
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
